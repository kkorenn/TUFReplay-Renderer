using System;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Threading;
using Newtonsoft.Json;
using TUFReplayRenderer.Infrastructure.Render;

namespace TUFReplayRenderer.Infrastructure.FFmpeg;

/// <summary>
/// Downloads the FFmpeg shared libraries for the current platform in the background when no local
/// install is found. The mod package ships without FFmpeg (the libraries are ten times the size of
/// the mod, and a machine only ever needs its own platform's set); instead a small
/// <c>ffmpeg-manifest.json</c> next to the mod names one downloadable zip per platform with its
/// SHA-256. The download starts while the game is still booting, and rendering unlocks the moment
/// it finishes — no restart needed.
/// </summary>
public static class FFmpegRuntimeInstaller
{
  public const string ManifestFileName = "ffmpeg-manifest.json";

  /// <summary>Overrides the manifest's BaseUrl (e.g. to point at a mirror).</summary>
  private const string BaseUrlOverrideVariable = "TUFREPLAY_FFMPEG_DOWNLOAD_BASE";

  public const string StateIdle = "idle";
  public const string StateDownloading = "downloading";
  public const string StateInstalled = "installed";
  public const string StateFailed = "failed";
  public const string StateUnnecessary = "unnecessary";

  private static readonly object Gate = new object();
  private static string _state = StateIdle;
  private static int _progressPercent;
  private static string _error;
  private static bool _reprobePending;

  public static string State
  {
    get
    {
      lock (Gate)
        return _state;
    }
  }

  public static int ProgressPercent
  {
    get
    {
      lock (Gate)
        return _progressPercent;
    }
  }

  public static string Error
  {
    get
    {
      lock (Gate)
        return _error;
    }
  }

  /// <summary>
  /// Kicks off the background download when FFmpeg is missing. Called once at mod load, so the
  /// download runs while the game is still starting up. Safe to call again; only the first call
  /// after a failed probe does anything.
  /// </summary>
  public static void BeginInstallIfNeeded()
  {
    if (FFmpegNativeLibrary.IsAvailable)
    {
      lock (Gate)
        _state = StateUnnecessary;
      return;
    }

    Manifest manifest = LoadManifest(out string manifestError);
    lock (Gate)
    {
      if (_state != StateIdle)
        return;

      if (manifest == null)
      {
        _state = StateFailed;
        _error = manifestError;
        RenderLog.Warn("FFmpeg auto-download unavailable: " + manifestError);
        return;
      }

      _state = StateDownloading;
      _progressPercent = 0;
    }

    string rid = FFmpegNativeLibrary.RuntimeIdentifier();
    Thread worker = new Thread(() => Install(manifest, rid))
    {
      IsBackground = true,
      Name = "TUFReplayRenderer FFmpeg download",
    };
    worker.Start();
  }

  /// <summary>
  /// Promotes a finished background install on the main thread: re-probes FFmpeg so rendering
  /// becomes available without a restart. Called from the main-thread IPC handlers, which is also
  /// where availability is read.
  /// </summary>
  public static void PromoteInstallOnMainThread()
  {
    lock (Gate)
    {
      if (!_reprobePending)
        return;
      _reprobePending = false;
    }

    FFmpegNativeLibrary.Reprobe();
    if (FFmpegNativeLibrary.IsAvailable)
      RenderLog.Info(
        "FFmpeg became available after background download. ffmpeg=" + FFmpegNativeLibrary.DescribeVersion()
      );
    else
      RenderLog.Warn("FFmpeg still unavailable after background download. " + FFmpegNativeLibrary.FailureMessage);
  }

  /// <summary>One human-readable line for capability reports, or null when there is nothing to say.</summary>
  public static string DescribeForCapabilities()
  {
    lock (Gate)
    {
      switch (_state)
      {
        case StateDownloading:
          return "FFmpeg is being downloaded in the background ("
            + _progressPercent
            + "%). Rendering unlocks automatically when it finishes.";
        case StateInstalled:
          return "FFmpeg finished downloading; finalizing.";
        case StateFailed:
          return _error == null ? null : "Automatic FFmpeg download failed: " + _error;
        default:
          return null;
      }
    }
  }

  private static void Install(Manifest manifest, string rid)
  {
    try
    {
      if (!manifest.Files.TryGetValue(rid, out ManifestFile file) || string.IsNullOrEmpty(file?.FileName))
        throw new InvalidOperationException("The manifest has no FFmpeg build for platform '" + rid + "'.");

      string baseUrl = Environment.GetEnvironmentVariable(BaseUrlOverrideVariable);
      if (string.IsNullOrEmpty(baseUrl))
        baseUrl = manifest.BaseUrl;
      if (string.IsNullOrEmpty(baseUrl))
        throw new InvalidOperationException("The manifest has no download BaseUrl.");

      string url = baseUrl.TrimEnd('/') + "/" + file.FileName;
      string installRoot = Main.Instance.InstallPath;
      string stagingDir = Path.Combine(installRoot, "native", ".download");
      string archivePath = Path.Combine(stagingDir, file.FileName);
      string targetDir = Path.Combine(installRoot, "native", rid);

      RenderLog.Info("Downloading FFmpeg for " + rid + " from " + url);
      if (Directory.Exists(stagingDir))
        Directory.Delete(stagingDir, true);
      Directory.CreateDirectory(stagingDir);

      Download(url, archivePath, file.SizeBytes);
      VerifySha256(archivePath, file.Sha256);

      string extractDir = Path.Combine(stagingDir, "extracted");
      ZipFile.ExtractToDirectory(archivePath, extractDir);

      Directory.CreateDirectory(targetDir);
      foreach (string extracted in Directory.GetFiles(extractDir, "*", SearchOption.AllDirectories))
      {
        string destination = Path.Combine(targetDir, Path.GetFileName(extracted));
        File.Copy(extracted, destination, true);
        MakeExecutable(destination);
      }

      Directory.Delete(stagingDir, true);

      lock (Gate)
      {
        _state = StateInstalled;
        _progressPercent = 100;
        _reprobePending = true;
      }
      RenderLog.Info("FFmpeg for " + rid + " installed to " + targetDir);
    }
    catch (Exception exception)
    {
      lock (Gate)
      {
        _state = StateFailed;
        _error = exception.Message;
      }
      RenderLog.Warn("FFmpeg background download failed: " + exception.Message);
    }
  }

  private static void Download(string url, string destination, long expectedSize)
  {
    // Older Mono profiles default to pre-TLS1.2 protocols that GitHub rejects.
    ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;

    HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
    request.Timeout = 60_000;
    request.ReadWriteTimeout = 60_000;
    request.AllowAutoRedirect = true;
    request.UserAgent = "TUFReplay-Renderer/" + (Main.Instance?.Version ?? "unknown");

    using (WebResponse response = request.GetResponse())
    using (Stream input = response.GetResponseStream())
    using (FileStream output = File.Create(destination))
    {
      long total = response.ContentLength > 0 ? response.ContentLength : expectedSize;
      byte[] buffer = new byte[81920];
      long written = 0;
      int read;
      while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
      {
        output.Write(buffer, 0, read);
        written += read;
        if (total > 0)
        {
          int percent = (int)Math.Min(99L, written * 100L / total);
          lock (Gate)
            _progressPercent = percent;
        }
      }
    }
  }

  private static void VerifySha256(string path, string expected)
  {
    if (string.IsNullOrEmpty(expected))
      throw new InvalidOperationException(
        "The manifest entry has no SHA-256; refusing to install an unverified archive."
      );

    using (SHA256 sha = SHA256.Create())
    using (FileStream stream = File.OpenRead(path))
    {
      string actual = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty);
      if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("Downloaded FFmpeg archive failed its SHA-256 check.");
    }
  }

  /// <summary>
  /// Zip extraction drops POSIX permission bits. dlopen only needs read access, but restore the
  /// executable bit anyway to match a hand-installed layout. The macOS dylibs carry their ad-hoc
  /// code signature inside the Mach-O file itself, so it survives the zip round-trip.
  /// </summary>
  private static void MakeExecutable(string path)
  {
    if (Path.DirectorySeparatorChar == '\\')
      return;

    try
    {
      System
        .Diagnostics.Process.Start(
          new System.Diagnostics.ProcessStartInfo("/bin/chmod", "+x \"" + path + "\"") { UseShellExecute = false }
        )
        ?.WaitForExit(5_000);
    }
    catch
    {
      // Best effort; read permission is what dlopen actually needs.
    }
  }

  private static Manifest LoadManifest(out string error)
  {
    error = null;
    try
    {
      Main main = Main.Instance;
      if (main == null)
      {
        error = "The mod is not initialized.";
        return null;
      }

      foreach (string root in new[] { main.PayloadPath, main.InstallPath })
      {
        if (string.IsNullOrEmpty(root))
          continue;
        string path = Path.Combine(root, ManifestFileName);
        if (!File.Exists(path))
          continue;

        Manifest manifest = JsonConvert.DeserializeObject<Manifest>(File.ReadAllText(path));
        if (manifest?.Files == null || manifest.Files.Count == 0)
        {
          error = ManifestFileName + " has no file entries.";
          return null;
        }
        return manifest;
      }

      error = ManifestFileName + " was not found next to the mod.";
      return null;
    }
    catch (Exception exception)
    {
      error = "Could not read " + ManifestFileName + ": " + exception.Message;
      return null;
    }
  }

#pragma warning disable 0649 // Populated by JsonConvert.
  private sealed class Manifest
  {
    public string Version;
    public string BaseUrl;
    public System.Collections.Generic.Dictionary<string, ManifestFile> Files;
  }

  private sealed class ManifestFile
  {
    public string FileName;
    public string Sha256;
    public long SizeBytes;
  }
#pragma warning restore 0649
}
