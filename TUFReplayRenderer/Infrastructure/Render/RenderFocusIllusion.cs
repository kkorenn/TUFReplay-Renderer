using System;
using System.Reflection;
using SkyHook;
using TUFReplayRenderer.Application.Render;
using UnityEngine;

namespace TUFReplayRenderer.Infrastructure.Render;

/// <summary>
/// Makes the game believe it is focused while a capture runs, without touching real window focus
/// or z-order — the render stays in the background and whatever the user is actually doing keeps
/// the keyboard and stays on top. Focus-gated consumers (key viewers dropping unfocused presses,
/// hook focus checks) then behave exactly as they do in watched, focused play.
///
/// Three layers, all no-ops outside capture:
/// 1. An optional Harmony patch makes <c>Application.isFocused</c> report true (see
///    RenderOptionalUnityPatches) for anything that polls it.
/// 2. <see cref="SkyHookManager.IsFocused"/> is pinned true for hook-level focus gating.
/// 3. Components that cache focus from OnApplicationFocus callbacks can't be reached generically;
///    the known one — Quartz's KeyViewerOverlay.Updater — is repinned by reflection, best effort,
///    silently skipped when absent or reshaped.
/// </summary>
internal static class RenderFocusIllusion
{
  private static bool _quartzResolveAttempted;
  private static Type _quartzUpdaterType;
  private static FieldInfo _quartzHasFocus;
  private static FieldInfo _quartzFocusKnown;
  private static UnityEngine.Object _quartzUpdater;
  private static float _nextQuartzLookup;

  /// <summary>Per-frame while a session is active; driven from the render coordinator's tick.</summary>
  public static void Tick()
  {
    if (!ReplayRenderSession.IsCapturingActive)
      return;

    SkyHookManager.IsFocused = true;
    PinQuartzFocus();
    PinQuartzClock();
  }

  // ── Key viewer clock ────────────────────────────────────────────────────────────────────────
  // Old Quartz animates its key viewer on a private Stopwatch clock (KvClock.Now). Under capture
  // that is encode speed, not timeline speed: rain lengths and scroll come out wrong in the
  // video. KvClock.Now is a plain managed getter, so it is Harmony-patched: while capturing it
  // returns wall-now-at-capture-start plus virtual elapsed (continuous with the live value, so
  // timestamps stored before the capture stay sane), and passes through untouched otherwise.
  // Reflection WRITES to its readonly fields are not an option — modern Unity Mono rejects them.

  private static bool _clockResolveAttempted;
  private static FieldInfo _kvClockOriginField;
  private static object _clockSession;
  private static double _clockWallBase;
  private static double _clockVirtualBase;

  private static void PinQuartzClock()
  {
    if (_clockResolveAttempted)
      return;
    _clockResolveAttempted = true;

    try
    {
      Type clock = null;
      foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
      {
        clock = assembly.GetType("Quartz.Features.KeyViewer.KvClock", false);
        if (clock != null)
          break;
      }
      if (clock == null)
        return;

      MethodInfo getter = clock
        .GetProperty("Now", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
        ?.GetGetMethod(true);
      if (getter == null)
      {
        RenderLog.Warn("Quartz KvClock.Now getter not found; key viewer animation stays on the wall clock.");
        return;
      }

      _kvClockOriginField = clock.GetField(
        "Origin",
        BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public
      );

      HarmonyLib.Harmony harmony = new HarmonyLib.Harmony("dev.koren.tufreplay.renderer.kvclock");
      harmony.Patch(
        getter,
        prefix: new HarmonyLib.HarmonyMethod(typeof(RenderFocusIllusion), nameof(KvClockNowPrefix))
      );
      RenderLog.Info("Quartz key viewer clock patched onto the capture timeline.");
    }
    catch (Exception exception)
    {
      RenderLog.Warn("Quartz clock patch failed: " + exception.Message);
    }
  }

  public static bool KvClockNowPrefix(ref float __result)
  {
    ReplayRenderSession session = ReplayRenderSession.Current;
    if (session == null || !ReplayRenderSession.IsCapturingActive)
      return true;

    double virtualNow = session.VirtualDspTime - session.DspTimeBase;
    if (!ReferenceEquals(_clockSession, session))
    {
      _clockSession = session;
      _clockWallBase = KvWallNow();
      _clockVirtualBase = virtualNow;
    }
    __result = (float)(_clockWallBase + (virtualNow - _clockVirtualBase));
    return false;
  }

  /// <summary>KvClock's own wall value, recomputed from its origin (read-only reflection).</summary>
  private static double KvWallNow()
  {
    try
    {
      if (_kvClockOriginField != null)
      {
        long origin = (long)_kvClockOriginField.GetValue(null);
        return (System.Diagnostics.Stopwatch.GetTimestamp() - origin) / (double)System.Diagnostics.Stopwatch.Frequency;
      }
    }
    catch (Exception exception)
    {
      RenderLog.Warn("KvClock origin read failed: " + exception.Message);
    }
    return 0d;
  }

  private static void PinQuartzFocus()
  {
    if (!ResolveQuartzFields())
      return;

    if (_quartzUpdater == null && Time.unscaledTime >= _nextQuartzLookup)
    {
      // The overlay (and its updater) can be created or torn down mid-session; re-look-up on a
      // slow cadence rather than every frame.
      _nextQuartzLookup = Time.unscaledTime + 1f;
      UnityEngine.Object[] found = UnityEngine.Object.FindObjectsByType(
        _quartzUpdaterType,
        FindObjectsInactive.Include,
        FindObjectsSortMode.None
      );
      _quartzUpdater = found.Length > 0 ? found[0] : null;
    }
    if (_quartzUpdater == null)
      return;

    try
    {
      _quartzHasFocus.SetValue(_quartzUpdater, true);
      _quartzFocusKnown.SetValue(_quartzUpdater, true);
    }
    catch (Exception exception)
    {
      RenderLog.Warn("Quartz focus pin failed; disabling. error=" + exception.Message);
      _quartzUpdaterType = null;
    }
  }

  private static bool ResolveQuartzFields()
  {
    if (_quartzResolveAttempted)
      return _quartzUpdaterType != null;
    _quartzResolveAttempted = true;

    try
    {
      foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
      {
        Type overlay = assembly.GetType("Quartz.Features.KeyViewer.KeyViewerOverlay", false);
        if (overlay == null)
          continue;

        Type updater = overlay.GetNestedType("Updater", BindingFlags.Public | BindingFlags.NonPublic);
        if (updater == null || !typeof(UnityEngine.Object).IsAssignableFrom(updater))
          return false;

        FieldInfo hasFocus = updater.GetField(
          "hasFocus",
          BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public
        );
        FieldInfo focusKnown = updater.GetField(
          "focusKnown",
          BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public
        );
        if (hasFocus == null || focusKnown == null)
          return false;

        _quartzUpdaterType = updater;
        _quartzHasFocus = hasFocus;
        _quartzFocusKnown = focusKnown;
        RenderLog.Info("Quartz key viewer focus pinning available.");
        return true;
      }
    }
    catch (Exception exception)
    {
      RenderLog.Warn("Quartz focus pin could not be bound: " + exception.Message);
    }
    return false;
  }
}
