#nullable enable
using System;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;

namespace TUFReplayRenderer.Infrastructure.Render;

/// <summary>Resolves where rendered replay videos are written.</summary>
public static class RenderOutputDirectory
{
  public const string FolderName = "TUFReplay Renders";

  private static string? _cached;
  private static string? _cachedGameRoot;
  private static bool _gameRootResolved;

  /// <summary>
  /// Resolves and caches the paths on the main thread, so later IPC handlers never have to read
  /// Application.dataPath from a background thread.
  /// </summary>
  public static void Warm()
  {
    GameRoot();
    _ = Default;
  }

  /// <summary>
  /// Default output folder, next to the game so it is easy to find from the desktop. Falls back to
  /// the user's videos folder when the game root cannot be determined.
  /// </summary>
  public static string Default
  {
    get
    {
      if (_cached != null)
        return _cached;

      string root = GameRoot() ?? Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
      if (string.IsNullOrEmpty(root))
        root = Path.GetTempPath();

      _cached = Path.Combine(root, FolderName);
      return _cached;
    }
  }

  /// <summary>
  /// Directory containing the game executable/app bundle, or null when it cannot be resolved.
  /// </summary>
  public static string? GameRoot()
  {
    if (_gameRootResolved)
      return _cachedGameRoot;

    try
    {
      string dataPath = UnityEngine.Application.dataPath;
      if (string.IsNullOrEmpty(dataPath))
        return Resolved(null);

      if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
      {
        // macOS: <root>/<Game>.app/Contents/Resources/Data
        DirectoryInfo? directory = new DirectoryInfo(dataPath);
        for (int depth = 0; depth < 5 && directory != null; depth++)
        {
          if (directory.Name.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
            return Resolved(directory.Parent?.FullName);
          directory = directory.Parent;
        }
        return Resolved(null);
      }

      // Windows/Linux: <root>/<Game>_Data
      return Resolved(Path.GetDirectoryName(dataPath));
    }
    catch
    {
      return Resolved(null);
    }
  }

  private static string? Resolved(string? gameRoot)
  {
    _cachedGameRoot = gameRoot;
    _gameRootResolved = true;
    return gameRoot;
  }
}
