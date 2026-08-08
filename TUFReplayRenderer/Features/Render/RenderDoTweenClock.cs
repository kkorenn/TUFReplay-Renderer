#nullable enable
using System;
using System.Reflection;
using HarmonyLib;
using TUFReplayRenderer.Application.Render;
using TUFReplayRenderer.Infrastructure.Render;
using UnityEngine;

namespace TUFReplayRenderer.Features.Render;

/// <summary>
/// Puts DOTween on the render's virtual clock for the duration of a capture.
///
/// DOTween derives its unscaled delta from <c>Time.realtimeSinceStartup</c>:
/// <code>_unscaledDeltaTime = Time.realtimeSinceStartup - _unscaledTime;</code>
/// which <c>Time.captureDeltaTime</c> does not virtualize. Every tween therefore animates at
/// wall-clock speed inside a render that does not — most visibly the 0.5s <c>DelayedCall</c> after
/// the death explosion that reveals the fail screen's "% complete", which fired almost immediately
/// in video time because the render runs far slower than realtime.
///
/// The fix is one field poke: before each Update, backdate <c>_unscaledTime</c> by exactly one
/// render frame step, so the delta DOTween computes for itself is that step. The scaled path needs
/// nothing — it already reads <c>Time.deltaTime</c>, which captureDeltaTime does virtualize.
///
/// Two deliberate safety choices, because an earlier attempt at this broke the game's own
/// menu-to-editor transition (that wipe completes through a DOTween callback, so when tweens
/// stopped completing the editor simply never opened, silently):
/// <list type="number">
/// <item>A <b>prefix</b>, not a transpiler. DOTween's own IL is never rewritten.</item>
/// <item>Installed only while capturing and removed on teardown, so outside a render the method is
/// untouched and the game's navigation provably cannot be affected by it.</item>
/// </list>
/// Any failure here is swallowed and logged: tween timing is a refinement, never a reason to break
/// a render or the game.
/// </summary>
public static class RenderDoTweenClock
{
  private const string HarmonyId = "TUFReplay.RenderDoTweenClock";

  private static Harmony? _harmony;
  private static MethodBase? _patchedMethod;
  private static FieldInfo? _unscaledTimeField;
  private static bool _resolved;

  public static bool IsInstalled => _harmony != null;

  /// <summary>Installs the clock override. Called when frame capture begins.</summary>
  public static void Install()
  {
    if (_harmony != null)
      return;

    try
    {
      if (!Resolve())
        return;

      Harmony harmony = new Harmony(HarmonyId);
      harmony.Patch(
        _patchedMethod,
        new HarmonyMethod(AccessTools.Method(typeof(RenderDoTweenClock), nameof(UpdatePrefix)))
      );
      _harmony = harmony;
      RenderLog.Info("DOTween is on the render clock for this capture.");
    }
    catch (Exception exception)
    {
      _harmony = null;
      RenderLog.Warn(
        "DOTween could not be put on the render clock (" + exception.Message + "); tween timing will follow wall time."
      );
    }
  }

  /// <summary>Removes the clock override. Called on every render teardown path.</summary>
  public static void Uninstall()
  {
    Harmony? harmony = _harmony;
    _harmony = null;
    if (harmony == null)
      return;

    try
    {
      if (_patchedMethod != null)
        harmony.Unpatch(_patchedMethod, HarmonyPatchType.Prefix, HarmonyId);
      else
        harmony.UnpatchAll(HarmonyId);
      RenderLog.Info("DOTween is back on the real clock.");
    }
    catch (Exception exception)
    {
      RenderLog.Warn("DOTween clock override could not be removed: " + exception.Message);
    }
  }

  private static bool Resolve()
  {
    if (_resolved)
      return _patchedMethod != null && _unscaledTimeField != null;

    _resolved = true;
    Type? component = AccessTools.TypeByName("DG.Tweening.Core.DOTweenComponent");
    if (component == null)
    {
      RenderLog.Warn("DOTween was not found; tween timing will follow wall time.");
      return false;
    }

    _patchedMethod = AccessTools.Method(component, "Update");
    _unscaledTimeField = AccessTools.Field(component, "_unscaledTime");
    if (_patchedMethod == null || _unscaledTimeField == null || _unscaledTimeField.FieldType != typeof(float))
    {
      _patchedMethod = null;
      _unscaledTimeField = null;
      RenderLog.Warn("DOTween's clock fields did not match expectations; tween timing will follow wall time.");
      return false;
    }
    return true;
  }

  /// <summary>
  /// Backdates DOTween's unscaled reference so the delta it computes is one render frame step.
  /// Inert unless a capture is active, so an installed patch still behaves exactly like vanilla
  /// between sessions.
  /// </summary>
  public static void UpdatePrefix(object __instance)
  {
    ReplayRenderSession? session = ReplayRenderSession.Current;
    if (session == null || !ReplayRenderSession.IsCapturingActive)
      return;

    FieldInfo? field = _unscaledTimeField;
    if (field == null || __instance == null)
      return;

    try
    {
      field.SetValue(__instance, Time.realtimeSinceStartup - (float)session.VirtualFrameStep);
    }
    catch
    {
      // A single missed frame of tween timing is not worth risking the render over.
    }
  }
}
