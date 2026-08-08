using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using TUFReplayRenderer.Infrastructure.Render;

namespace TUFReplayRenderer.Features.Render;

/// <summary>
/// Applies every render patch individually, each one guarded.
///
/// None of these use <c>[HarmonyPatch]</c> attributes, so <c>Harmony.PatchAll</c> never sees them.
/// That is deliberate: a single failing patch inside PatchAll aborts the entire batch and takes the
/// whole mod down with it. Rendering is one feature among several, and recording, replay and the
/// IPC surface must keep working even on a runtime where a render patch cannot be installed —
/// UnityModManager's Mono refuses to patch UnityEngine's InternalCall externs such as
/// <c>Time.get_time</c>, for example.
///
/// Patches are split into two tiers. A <b>required</b> patch failing disables rendering but leaves
/// the rest of the mod healthy. An <b>optional</b> patch failing only degrades one detail of the
/// output, which is recorded and reported.
/// </summary>
public static class RenderPatchInstaller
{
  private static readonly List<string> AppliedPatches = new List<string>();
  private static readonly List<string> FailedPatches = new List<string>();

  private static string _blockingFailure;

  /// <summary>Null when rendering can run; otherwise why the required patches did not apply.</summary>
  public static string BlockingFailure => _blockingFailure;

  public static string Describe() =>
    "applied=["
    + string.Join(", ", AppliedPatches.ToArray())
    + "], unavailable=["
    + string.Join(", ", FailedPatches.ToArray())
    + "]";

  public static void Apply(Harmony harmony)
  {
    AppliedPatches.Clear();
    FailedPatches.Clear();
    _blockingFailure = null;

    if (harmony == null)
    {
      _blockingFailure = "No Harmony instance was available.";
      return;
    }

    // ── Required: without these the render is wrong rather than merely imperfect ──

    // Pins the conductor's DSP clock to the virtual timeline. This is what replaces ADOFAIRenderer's
    // unpatchable AudioSettings.dspTime patch, and it is what makes song position advance exactly
    // one frame step per rendered frame.
    Required(
      harmony,
      "scrConductor.Update",
      () => AccessTools.Method(typeof(scrConductor), "Update"),
      prefix: nameof(RenderGamePatches.ConductorClockPrefix)
    );

    // Registers every sound the game plays with the offline mixer.
    Required(
      harmony,
      "AudioManager._Play",
      () => AccessTools.Method(typeof(AudioManager), "_Play"),
      prefix: nameof(RenderGamePatches.CacheClipPrefix),
      postfix: nameof(RenderGamePatches.RegisterPlayedSoundPostfix)
    );

    Required(
      harmony,
      "scrConductor.PlayWithEndTime",
      () => AccessTools.Method(typeof(scrConductor), "PlayWithEndTime"),
      prefix: nameof(RenderGamePatches.RegisterConductorSoundPrefix)
    );

    // One-shot SFX (death sound, explosion, applause, clear jingle) go through PlayOneShot, not
    // AudioManager._Play, so they need their own hook to reach the offline mixer.
    Required(
      harmony,
      "scrSfx.PlaySfx (clip)",
      () =>
        AccessTools.Method(
          typeof(scrSfx),
          "PlaySfx",
          new[] { typeof(UnityEngine.AudioClip), typeof(MixerGroup), typeof(float), typeof(float), typeof(float) }
        ),
      postfix: nameof(RenderGamePatches.RegisterSfxPostfix)
    );

    // Keeps the camera from releasing the very RenderTexture the capture path reads.
    Required(
      harmony,
      "scrCamera.SetCustomFrameRate",
      () => AccessTools.Method(typeof(scrCamera), "SetCustomFrameRate"),
      prefix: nameof(RenderGamePatches.SetCustomFrameRatePrefix)
    );

    // ── Optional: each failure costs one detail of the output ──

    // scrCountdown.Update reads AudioSettings.dspTime directly rather than the conductor's clock,
    // so without this rewrite the "Get Ready / 3 / 2 / 1 / Go" text runs at wall-clock speed inside
    // a render that does not — the countdown races ahead of the level it belongs to.
    Optional(
      harmony,
      "scrCountdown.Update (clock)",
      () => AccessTools.Method(typeof(scrCountdown), "Update"),
      transpiler: nameof(RenderGamePatches.RedirectRenderClockTranspiler)
    );

    // DOTween is deliberately NOT patched here. It derives its unscaled delta from
    // Time.realtimeSinceStartup, so its tweens do animate at wall-clock speed inside a render —
    // the 0.5s DelayedCall that reveals the fail screen's "% complete" being the visible case.
    // But rewriting DOTweenComponent.Update broke the menu-to-editor transition: that wipe
    // completes through a DOTween callback, so when tweens stopped completing the editor simply
    // never opened, with nothing logged. A renderer refinement must never cost the game's own
    // navigation, so this stays out until it can be done without touching DOTween's update loop.

    // Without this the async input offset drifts, because the game damps it assuming real time.
    Optional(
      harmony,
      "AsyncInputUtils.UpdateOffsetTime",
      () => AccessTools.Method(typeof(AsyncInputUtils), "UpdateOffsetTime"),
      prefix: nameof(RenderGamePatches.AsyncInputOffsetPrefix)
    );

    // Without this, portal particles animate on the wall clock instead of the virtual one.
    Optional(
      harmony,
      "scrPortalParticles.Start",
      () => AccessTools.Method(typeof(scrPortalParticles), "Start"),
      postfix: nameof(RenderGamePatches.ForceScaledParticlesPostfix)
    );

    // Diagnostic only: records whether hitsound scheduling landed inside the capture window. If it
    // ran before capture, no hitsound reaches the offline mixer and the output has none.
    Optional(
      harmony,
      "scrConductor.PlayHitTimes",
      () => AccessTools.Method(typeof(scrConductor), "PlayHitTimes"),
      postfix: nameof(RenderGamePatches.PlayHitTimesDiagnosticPostfix)
    );

    RenderOptionalUnityPatches.Apply(harmony, Optional);

    RenderLog.Info("Render patches: " + Describe());
    if (_blockingFailure != null)
      RenderLog.Error("Rendering is disabled: " + _blockingFailure);
  }

  private static void Required(
    Harmony harmony,
    string label,
    Func<MethodBase> resolve,
    string prefix = null,
    string postfix = null
  )
  {
    if (!TryPatch(harmony, typeof(RenderGamePatches), label, resolve, prefix, postfix, out string failure))
      _blockingFailure ??= label + " could not be patched (" + failure + ").";
  }

  internal static void Optional(
    Harmony harmony,
    Type container,
    string label,
    Func<MethodBase> resolve,
    string prefix,
    string postfix
  ) => TryPatch(harmony, container, label, resolve, prefix, postfix, out _);

  private static void Optional(
    Harmony harmony,
    string label,
    Func<MethodBase> resolve,
    string prefix = null,
    string postfix = null,
    string transpiler = null
  ) => TryPatch(harmony, typeof(RenderGamePatches), label, resolve, prefix, postfix, out _, transpiler);

  private static bool TryPatch(
    Harmony harmony,
    Type container,
    string label,
    Func<MethodBase> resolve,
    string prefix,
    string postfix,
    out string failure,
    string transpiler = null
  )
  {
    failure = null;
    try
    {
      MethodBase target = resolve();
      if (target == null)
      {
        failure = "target not found";
        FailedPatches.Add(label + " (not found)");
        return false;
      }

      harmony.Patch(
        target,
        prefix == null ? null : new HarmonyMethod(AccessTools.Method(container, prefix)),
        postfix == null ? null : new HarmonyMethod(AccessTools.Method(container, postfix)),
        transpiler == null ? null : new HarmonyMethod(AccessTools.Method(container, transpiler))
      );
      AppliedPatches.Add(label);
      return true;
    }
    catch (Exception exception)
    {
      failure = exception.GetType().Name + ": " + exception.Message;
      FailedPatches.Add(label + " (" + exception.GetType().Name + ")");
      return false;
    }
  }
}
