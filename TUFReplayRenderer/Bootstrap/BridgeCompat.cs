using System;
using System.Reflection;

namespace TUFReplayRenderer.Bootstrap;

/// <summary>
/// Version gate for the TUFReplay render-capture bridge. TUFReplay and this mod update
/// independently, so the bridge contract is checked by reflection BEFORE any typed code that
/// binds it is allowed to run: a mismatch (or a TUFReplay too old to have the bridge at all)
/// downgrades to "rendering unavailable, update X" instead of a MissingMethodException mid-load.
/// </summary>
internal static class BridgeCompat
{
  /// <summary>Base bridge contract this build was compiled against.</summary>
  public const int RequiredApiVersion = 1;

  public const string StatusOk = "ok";
  public const string StatusTufReplayOutdated = "tufreplay_outdated";
  public const string StatusRendererOutdated = "renderer_outdated";

  public static string Status { get; private set; } = StatusTufReplayOutdated;
  public static int DetectedApiVersion { get; private set; }
  public static bool IsOk => Status == StatusOk;

  /// <summary>Human-readable line for capability reports; null when compatible.</summary>
  public static string Describe()
  {
    switch (Status)
    {
      case StatusTufReplayOutdated:
        return "The installed TUFReplay is too old for this renderer (bridge v"
          + DetectedApiVersion
          + ", renderer needs v"
          + RequiredApiVersion
          + "). Update TUFReplay.";
      case StatusRendererOutdated:
        return "The installed TUFReplay uses a newer bridge (v"
          + DetectedApiVersion
          + ") than this renderer understands (v"
          + RequiredApiVersion
          + "). Update TUFReplay-Renderer.";
      default:
        return null;
    }
  }

  /// <summary>
  /// Resolves TUFReplay's bridge contract version without binding any bridge type. A TUFReplay
  /// without the bridge (or without the version property, which shipped with contract v1)
  /// reports as version 0.
  /// </summary>
  public static bool Check()
  {
    DetectedApiVersion = 0;
    try
    {
      Type registration = null;
      foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
      {
        registration = assembly.GetType("TUFReplay.Application.Replay.RenderCaptureBridge", false);
        if (registration != null)
          break;
      }

      PropertyInfo apiVersion = registration?.GetProperty("ApiVersion", BindingFlags.Static | BindingFlags.Public);
      if (apiVersion != null)
        DetectedApiVersion = (int)apiVersion.GetValue(null);
    }
    catch (Exception exception)
    {
      Main.Instance?.LogException(nameof(BridgeCompat), exception);
    }

    if (DetectedApiVersion < RequiredApiVersion)
      Status = StatusTufReplayOutdated;
    else if (DetectedApiVersion > RequiredApiVersion)
      Status = StatusRendererOutdated;
    else
      Status = StatusOk;

    if (!IsOk)
      Main.Instance?.Log("[Bridge] Incompatible: " + Describe());
    return IsOk;
  }
}
