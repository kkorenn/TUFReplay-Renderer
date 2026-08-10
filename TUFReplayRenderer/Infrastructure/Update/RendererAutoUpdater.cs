using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Threading;
using Newtonsoft.Json.Linq;
using TUFReplayRenderer.Bootstrap;
using TUFReplayRenderer.Infrastructure.Render;

namespace TUFReplayRenderer.Infrastructure.Update;

/// <summary>
/// Self-update: checks GitHub releases in the background, downloads a newer release's zip,
/// verifies it against the SHA-256 published in the release's <c>update.json</c> asset, and
/// stages the payload under <c>Runtime/pending/&lt;version&gt;/</c> with a per-file staging
/// manifest. The never-updated loader assembly re-verifies and promotes it at the next launch —
/// a running assembly's file is OS-locked, so the swap can only ever happen there.
///
/// Releases whose <c>update.json</c> declares a MinBridgeApiVersion newer than the installed
/// TUFReplay provides are skipped: updating into a bridge mismatch would trade a working install
/// for a disabled one.
/// </summary>
internal static class RendererAutoUpdater
{
  private const string ReleasesUrl = "https://api.github.com/repos/kkorenn/TUFReplay-Renderer/releases?per_page=15";
  private const string UpdateManifestAssetName = "update.json";

  public static void BeginCheckInBackground()
  {
    RendererUpdateSettings settings = RendererUpdateSettings.Current;
    if (settings is { AutoUpdate: false })
    {
      RenderLog.Info("[Update] Auto-update is disabled.");
      return;
    }

    Thread worker = new Thread(() => Check(settings)) { IsBackground = true, Name = "TUFReplayRenderer update check" };
    worker.Start();
  }

  private static void Check(RendererUpdateSettings settings)
  {
    try
    {
      string currentVersion = Main.Instance?.Version;
      if (string.IsNullOrEmpty(currentVersion))
        return;

      JArray releases = JArray.Parse(DownloadString(ReleasesUrl));
      JObject best = null;
      SemVer bestVersion = default;
      foreach (JToken token in releases)
      {
        if (token is not JObject release)
          continue;
        if (release.Value<bool?>("draft") == true)
          continue;
        if (release.Value<bool?>("prerelease") == true && !settings.ReceiveBetaUpdates)
          continue;
        if (FindAsset(release, UpdateManifestAssetName) == null)
          continue;

        if (!SemVer.TryParse(release.Value<string>("tag_name"), out SemVer version))
          continue;
        if (best == null || version.CompareTo(bestVersion) > 0)
        {
          best = release;
          bestVersion = version;
        }
      }

      if (
        best == null
        || !SemVer.TryParse(currentVersion, out SemVer installed)
        || bestVersion.CompareTo(installed) <= 0
      )
        return;

      JObject manifest = JObject.Parse(DownloadString(FindAsset(best, UpdateManifestAssetName)));
      string manifestVersion = manifest.Value<string>("Version");
      string zipName = manifest.Value<string>("Zip") ?? "TUFReplay-Renderer.zip";
      string sha256 = manifest.Value<string>("Sha256");
      int minBridge = manifest.Value<int?>("MinBridgeApiVersion") ?? 0;
      string zipUrl = FindAsset(best, zipName);
      if (string.IsNullOrEmpty(manifestVersion) || string.IsNullOrEmpty(sha256) || zipUrl == null)
      {
        RenderLog.Warn("[Update] Release " + bestVersion + " has an incomplete update.json; skipped.");
        return;
      }

      if (minBridge > BridgeCompat.DetectedApiVersion)
      {
        RenderLog.Info(
          "[Update] "
            + manifestVersion
            + " needs bridge v"
            + minBridge
            + " but TUFReplay provides v"
            + BridgeCompat.DetectedApiVersion
            + "; staying on "
            + currentVersion
            + " until TUFReplay updates."
        );
        return;
      }

      Stage(manifestVersion, zipUrl, sha256);
    }
    catch (Exception exception)
    {
      RenderLog.Warn("[Update] Check failed: " + exception.Message);
    }
  }

  private static void Stage(string version, string zipUrl, string expectedSha256)
  {
    string installRoot = Main.Instance.InstallPath;
    string pendingDirectory = Path.Combine(installRoot, "Runtime", "pending", version);
    if (Directory.Exists(pendingDirectory))
    {
      RenderLog.Info("[Update] " + version + " is already staged; it applies at the next launch.");
      return;
    }

    string workDirectory = Path.Combine(installRoot, "Runtime", "pending", ".download-" + version);
    if (Directory.Exists(workDirectory))
      Directory.Delete(workDirectory, true);
    Directory.CreateDirectory(workDirectory);

    try
    {
      RenderLog.Info("[Update] Downloading " + version + " from " + zipUrl);
      string archivePath = Path.Combine(workDirectory, "release.zip");
      DownloadFile(zipUrl, archivePath);

      using (SHA256 sha = SHA256.Create())
      using (FileStream stream = File.OpenRead(archivePath))
      {
        string actual = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty);
        if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
          throw new InvalidOperationException("Downloaded archive failed its SHA-256 check.");
      }

      string extractDirectory = Path.Combine(workDirectory, "extracted");
      ZipFile.ExtractToDirectory(archivePath, extractDirectory);

      // The release zip is a full install (loader + Runtime/versions/<v>/payload). Only the
      // payload is staged: the loader must never replace itself.
      string payloadDirectory = FindPayloadDirectory(extractDirectory);
      if (payloadDirectory == null)
        throw new InvalidOperationException("The release archive has no Runtime/versions payload.");

      string stagingDirectory = Path.Combine(workDirectory, "staged");
      CopyTree(payloadDirectory, stagingDirectory);
      WriteStagingManifest(stagingDirectory, version);

      Directory.CreateDirectory(Path.GetDirectoryName(pendingDirectory));
      Directory.Move(stagingDirectory, pendingDirectory);
      RenderLog.Info("[Update] " + version + " staged; it applies at the next game launch.");
    }
    finally
    {
      try
      {
        if (Directory.Exists(workDirectory))
          Directory.Delete(workDirectory, true);
      }
      catch
      {
        // Best effort; the loader ignores non-version directories under pending.
      }
    }
  }

  private static string FindPayloadDirectory(string extractDirectory)
  {
    foreach (string versionsRoot in Directory.GetDirectories(extractDirectory, "versions", SearchOption.AllDirectories))
    {
      if (!string.Equals(Path.GetFileName(Path.GetDirectoryName(versionsRoot)), "Runtime", StringComparison.Ordinal))
        continue;
      string[] payloads = Directory.GetDirectories(versionsRoot);
      if (payloads.Length > 0)
        return payloads[0];
    }
    return null;
  }

  private static void CopyTree(string source, string destination)
  {
    Directory.CreateDirectory(destination);
    foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
    {
      string relative = file.Substring(source.Length + 1);
      string target = Path.Combine(destination, relative);
      Directory.CreateDirectory(Path.GetDirectoryName(target));
      File.Copy(file, target, true);
    }
  }

  /// <summary>staging.json: per-file SHA-256 the loader re-verifies before promoting.</summary>
  private static void WriteStagingManifest(string stagingDirectory, string version)
  {
    List<string> entries = new List<string>();
    using (SHA256 sha = SHA256.Create())
    {
      foreach (string file in Directory.GetFiles(stagingDirectory, "*", SearchOption.AllDirectories))
      {
        string relative = file.Substring(stagingDirectory.Length + 1).Replace(Path.DirectorySeparatorChar, '/');
        using (FileStream stream = File.OpenRead(file))
        {
          string hash = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty);
          entries.Add("    \"" + relative + "\": \"" + hash + "\"");
        }
      }
    }
    File.WriteAllText(
      Path.Combine(stagingDirectory, "staging.json"),
      "{\n  \"Version\": \"" + version + "\",\n  \"Files\": {\n" + string.Join(",\n", entries) + "\n  }\n}\n"
    );
  }

  private static string FindAsset(JObject release, string name)
  {
    if (release["assets"] is not JArray assets)
      return null;
    foreach (JToken asset in assets)
    {
      if (string.Equals(asset.Value<string>("name"), name, StringComparison.OrdinalIgnoreCase))
        return asset.Value<string>("browser_download_url");
    }
    return null;
  }

  private static string DownloadString(string url)
  {
    HttpWebRequest request = CreateRequest(url);
    using (WebResponse response = request.GetResponse())
    using (StreamReader reader = new StreamReader(response.GetResponseStream()))
      return reader.ReadToEnd();
  }

  private static void DownloadFile(string url, string destination)
  {
    HttpWebRequest request = CreateRequest(url);
    using (WebResponse response = request.GetResponse())
    using (Stream input = response.GetResponseStream())
    using (FileStream output = File.Create(destination))
      input.CopyTo(output);
  }

  private static HttpWebRequest CreateRequest(string url)
  {
    ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
    HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
    request.Timeout = 60_000;
    request.ReadWriteTimeout = 60_000;
    request.AllowAutoRedirect = true;
    // GitHub's API rejects requests without a user agent.
    request.UserAgent = "TUFReplay-Renderer/" + (Main.Instance?.Version ?? "unknown");
    request.Accept = "application/vnd.github+json";
    return request;
  }

  /// <summary>Release tags and Info.json versions: major.minor.patch with an optional -pre.N.</summary>
  private readonly struct SemVer : IComparable<SemVer>
  {
    private readonly int _major;
    private readonly int _minor;
    private readonly int _patch;
    private readonly string _preName;
    private readonly int _preNumber;
    private readonly bool _isPrerelease;

    private SemVer(int major, int minor, int patch, string preName, int preNumber, bool isPrerelease)
    {
      _major = major;
      _minor = minor;
      _patch = patch;
      _preName = preName ?? string.Empty;
      _preNumber = preNumber;
      _isPrerelease = isPrerelease;
    }

    public static bool TryParse(string text, out SemVer version)
    {
      version = default;
      if (string.IsNullOrEmpty(text))
        return false;
      if (text.StartsWith("v", StringComparison.OrdinalIgnoreCase))
        text = text.Substring(1);

      string core = text;
      string prerelease = null;
      int dash = text.IndexOf('-');
      if (dash >= 0)
      {
        core = text.Substring(0, dash);
        prerelease = text.Substring(dash + 1);
      }

      string[] parts = core.Split('.');
      if (
        parts.Length != 3
        || !int.TryParse(parts[0], out int major)
        || !int.TryParse(parts[1], out int minor)
        || !int.TryParse(parts[2], out int patch)
      )
        return false;

      string preName = null;
      int preNumber = 0;
      if (prerelease != null)
      {
        int lastDot = prerelease.LastIndexOf('.');
        if (lastDot >= 0 && int.TryParse(prerelease.Substring(lastDot + 1), out preNumber))
          preName = prerelease.Substring(0, lastDot);
        else
          preName = prerelease;
      }

      version = new SemVer(major, minor, patch, preName, preNumber, prerelease != null);
      return true;
    }

    public int CompareTo(SemVer other)
    {
      int result = _major.CompareTo(other._major);
      if (result != 0)
        return result;
      result = _minor.CompareTo(other._minor);
      if (result != 0)
        return result;
      result = _patch.CompareTo(other._patch);
      if (result != 0)
        return result;
      // A release outranks any of its prereleases.
      if (_isPrerelease != other._isPrerelease)
        return _isPrerelease ? -1 : 1;
      if (!_isPrerelease)
        return 0;
      result = string.CompareOrdinal(_preName, other._preName);
      if (result != 0)
        return result;
      return _preNumber.CompareTo(other._preNumber);
    }

    public override string ToString() =>
      _major + "." + _minor + "." + _patch + (_isPrerelease ? "-" + _preName + "." + _preNumber : string.Empty);
  }
}
