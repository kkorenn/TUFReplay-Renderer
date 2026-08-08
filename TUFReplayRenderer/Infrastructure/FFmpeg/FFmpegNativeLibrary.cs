using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using TUFReplayRenderer.Infrastructure.Render;

namespace TUFReplayRenderer.Infrastructure.FFmpeg;

/// <summary>
/// Locates the shared FFmpeg libraries and makes them loadable before any P/Invoke runs.
///
/// FFmpeg.AutoGen resolves native libraries through <c>ffmpeg.RootPath</c>, but under Unity's Mono
/// runtime that resolution only succeeds when the directory actually contains the exact SONAMEs the
/// bindings ask for. Probing (and calling <c>avcodec_version</c>) up front turns an otherwise fatal
/// first-call <see cref="DllNotFoundException"/> deep inside encoder setup into one actionable
/// message at feature-enable time.
/// </summary>
public static class FFmpegNativeLibrary
{
  private const string DirectoryOverrideVariable = "TUFREPLAY_FFMPEG_DIR";

  private static readonly object Gate = new object();
  private static bool _probed;
  private static bool _available;
  private static string _resolvedDirectory;
  private static string _failureMessage;
  private static uint _avcodecVersion;

  /// <summary>True when the FFmpeg shared libraries loaded and answered a version query.</summary>
  public static bool IsAvailable
  {
    get
    {
      EnsureProbed();
      return _available;
    }
  }

  /// <summary>Human-readable reason the libraries are unusable, or null when they are fine.</summary>
  public static string FailureMessage
  {
    get
    {
      EnsureProbed();
      return _failureMessage;
    }
  }

  public static string ResolvedDirectory
  {
    get
    {
      EnsureProbed();
      return _resolvedDirectory;
    }
  }

  public static string DescribeVersion()
  {
    EnsureProbed();
    if (!_available)
      return "unavailable";
    return (_avcodecVersion >> 16) + "." + ((_avcodecVersion >> 8) & 0xFF) + "." + (_avcodecVersion & 0xFF);
  }

  /// <summary>
  /// Forgets a failed probe so the next query retries the directory scan. Called after the
  /// background installer drops fresh libraries into <c>native/&lt;rid&gt;</c>. A successful probe
  /// is never repeated: FFmpeg.AutoGen's RootPath must not change once native calls have begun.
  /// </summary>
  public static void Reprobe()
  {
    lock (Gate)
    {
      if (_available)
        return;
      _probed = false;
      _failureMessage = null;
      _resolvedDirectory = null;
    }
    EnsureProbed();
  }

  public static void EnsureProbed()
  {
    lock (Gate)
    {
      if (_probed)
        return;
      _probed = true;

      List<string> candidates = BuildCandidateDirectories();
      List<string> attempts = new List<string>();

      foreach (string candidate in candidates)
      {
        if (TryUseDirectory(candidate, out string failure))
        {
          _available = true;
          _resolvedDirectory = candidate ?? "(system default)";
          _failureMessage = null;
          RenderLog.Info(
            "FFmpeg native libraries loaded. avcodec=" + DescribeVersion() + ", directory=" + _resolvedDirectory
          );
          return;
        }

        attempts.Add((candidate ?? "(system default)") + " -> " + failure);
      }

      _available = false;
      _failureMessage =
        "FFmpeg shared libraries could not be loaded. Install them next to TUFReplay or set "
        + DirectoryOverrideVariable
        + ". Attempts: "
        + string.Join(" | ", attempts.ToArray());
      RenderLog.Error(_failureMessage);
    }
  }

  private static bool TryUseDirectory(string directory, out string failure)
  {
    failure = null;
    if (directory != null && !Directory.Exists(directory))
    {
      failure = "directory does not exist";
      return false;
    }

    try
    {
      ffmpeg.RootPath = directory ?? string.Empty;

      // FFmpeg.AutoGen 5+ binds every function lazily through DynamicallyLoadedBindings; until
      // Initialize() installs the resolving delegates, every call throws NotSupportedException no
      // matter what is on disk. Re-initializing per attempt resets the delegates so each candidate
      // directory gets a fresh resolution against the RootPath set above.
      DynamicallyLoadedBindings.ThrowErrorIfFunctionNotFound = true;
      DynamicallyLoadedBindings.Initialize();

      _avcodecVersion = ffmpeg.avcodec_version();
      if (_avcodecVersion == 0)
      {
        failure = "avcodec_version returned 0";
        return false;
      }

      // Touch the other libraries the renderer needs so a partial install fails here, with a
      // directory attached, instead of mid-render.
      ffmpeg.avformat_version();
      ffmpeg.avutil_version();
      ffmpeg.swscale_version();
      ffmpeg.swresample_version();
      return true;
    }
    catch (Exception exception)
    {
      // DllNotFoundException is the common miss. NotSupportedException means the AutoGen binding
      // major version does not match the shared libraries present in that directory.
      failure = exception.GetType().Name + ": " + exception.Message;
      return false;
    }
  }

  private static List<string> BuildCandidateDirectories()
  {
    List<string> candidates = new List<string>();
    string runtimeIdentifier = RuntimeIdentifier();

    void Add(string path)
    {
      if (string.IsNullOrEmpty(path))
        return;
      if (!candidates.Contains(path))
        candidates.Add(path);
    }

    Add(Environment.GetEnvironmentVariable(DirectoryOverrideVariable));

    Main main = Main.Instance;
    if (main != null)
    {
      // Shipped payload first: the versioned runtime directory is what the auto-updater swaps.
      Add(Combine(main.PayloadPath, "native", runtimeIdentifier));
      Add(Combine(main.PayloadPath, "native"));
      Add(Combine(main.InstallPath, "native", runtimeIdentifier));
      Add(Combine(main.InstallPath, "native"));
    }

    // ADOFAIRenderer installs the same FFmpeg 8 shared libraries into the game's UserLibs
    // directory. Reusing them keeps a machine that already has that mod working without a
    // second copy, and costs nothing when it is absent.
    string gameRoot = RenderOutputDirectory.GameRoot();
    Add(Combine(gameRoot, "UserLibs"));

    // Finally let the OS loader search its own paths.
    candidates.Add(null);
    return candidates;
  }

  private static string Combine(string root, params string[] parts)
  {
    if (string.IsNullOrEmpty(root))
      return null;
    string path = root;
    foreach (string part in parts)
    {
      if (string.IsNullOrEmpty(part))
        return null;
      path = Path.Combine(path, part);
    }
    return path;
  }

  internal static string RuntimeIdentifier()
  {
    if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
      return "osx";
    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
      return "win-x64";
    return "linux-x64";
  }
}
