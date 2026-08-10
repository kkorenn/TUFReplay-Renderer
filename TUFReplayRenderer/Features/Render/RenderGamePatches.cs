using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using TUFReplayRenderer.Application.Render;
using TUFReplayRenderer.Application.Render.Audio;
using TUFReplayRenderer.Infrastructure.Render;
using UnityEngine;
using UnityEngine.Audio;

namespace TUFReplayRenderer.Features.Render;

/// <summary>
/// Patch bodies that target the game's own assembly, applied by <see cref="RenderPatchInstaller"/>.
///
/// These carry no <c>[HarmonyPatch]</c> attributes on purpose — see the installer for why. Every
/// body is inert unless a render session is live, so normal play and normal replays are untouched.
/// </summary>
public static class RenderGamePatches
{
  internal static OfflineAudioMixer Mixer =>
    ReplayRenderSession.IsCapturingActive && ReplayRenderSession.Current.Settings.RenderAudio
      ? ReplayRenderSession.Current.Mixer
      : null;

  internal static double DspTimeBase => ReplayRenderSession.Current?.DspTimeBase ?? 0d;

  // ── Virtual clock ───────────────────────────────────────────────────────────────────────────

  /// <summary>
  /// The virtual DSP clock while capturing, the real one otherwise.
  ///
  /// ADOFAIRenderer patches <c>AudioSettings.dspTime</c> itself so every reader sees virtual time.
  /// That property is an InternalCall extern which this runtime refuses to patch, so instead the
  /// call sites inside the game's own assembly are rewritten to call this
  /// (see <see cref="RedirectDspTimeTranspiler"/>). The observable result is identical.
  /// </summary>
  public static double VirtualOrRealDspTime()
  {
    ReplayRenderSession session = ReplayRenderSession.Current;
    if (session != null && ReplayRenderSession.IsCapturingActive)
      return session.VirtualDspTime;
    return AudioSettings.dspTime;
  }

  /// <summary>Virtual replacement for <c>Time.realtimeSinceStartup</c> while capturing.</summary>
  public static float VirtualOrRealRealtime()
  {
    ReplayRenderSession session = ReplayRenderSession.Current;
    if (session != null && ReplayRenderSession.IsCapturingActive)
      return (float)session.VirtualRealtime;
    return Time.realtimeSinceStartup;
  }

  /// <summary>Virtual replacement for <c>Time.realtimeSinceStartupAsDouble</c> while capturing.</summary>
  public static double VirtualOrRealRealtimeAsDouble()
  {
    ReplayRenderSession session = ReplayRenderSession.Current;
    if (session != null && ReplayRenderSession.IsCapturingActive)
      return session.VirtualRealtime;
    return Time.realtimeSinceStartupAsDouble;
  }

  /// <summary>Virtual replacement for <c>Time.unscaledDeltaTime</c> while capturing.</summary>
  public static float VirtualOrRealUnscaledDeltaTime()
  {
    ReplayRenderSession session = ReplayRenderSession.Current;
    if (session != null && ReplayRenderSession.IsCapturingActive)
      return (float)session.VirtualFrameStep;
    return Time.unscaledDeltaTime;
  }

  /// <summary>Virtual replacement for <c>Time.unscaledTime</c> while capturing.</summary>
  public static float VirtualOrRealUnscaledTime()
  {
    ReplayRenderSession session = ReplayRenderSession.Current;
    if (session != null && ReplayRenderSession.IsCapturingActive)
      return (float)(session.UnscaledTimeBase + session.FrameCount * session.VirtualFrameStep);
    return Time.unscaledTime;
  }

  /// <summary>
  /// Rewrites every real-clock read in the patched method to its virtual counterpart.
  ///
  /// ADOFAIRenderer patches the Unity properties themselves so every reader sees virtual time.
  /// Those are InternalCall externs this runtime refuses to patch, so the call sites inside
  /// patchable managed methods are rewritten instead. Same observable result, one method at a time.
  ///
  /// Two symptoms this cures: scrCountdown.Update compares raw <c>AudioSettings.dspTime</c> against
  /// countdown times derived from the virtual song clock, and DOTween derives its unscaled delta
  /// from <c>Time.realtimeSinceStartup</c> — so the countdown text and every tween (including the
  /// delayed call that reveals the fail screen's "% complete") ran at wall-clock speed inside a
  /// render that does not.
  /// </summary>
  public static IEnumerable<CodeInstruction> RedirectRenderClockTranspiler(IEnumerable<CodeInstruction> instructions)
  {
    var redirects = new List<KeyValuePair<MethodInfo, MethodInfo>>();

    void Map(MethodInfo from, string to)
    {
      MethodInfo target = AccessTools.Method(typeof(RenderGamePatches), to);
      if (from != null && target != null)
        redirects.Add(new KeyValuePair<MethodInfo, MethodInfo>(from, target));
    }

    Map(AccessTools.PropertyGetter(typeof(AudioSettings), nameof(AudioSettings.dspTime)), nameof(VirtualOrRealDspTime));
    Map(AccessTools.PropertyGetter(typeof(Time), nameof(Time.realtimeSinceStartup)), nameof(VirtualOrRealRealtime));
    Map(
      AccessTools.PropertyGetter(typeof(Time), nameof(Time.realtimeSinceStartupAsDouble)),
      nameof(VirtualOrRealRealtimeAsDouble)
    );
    Map(
      AccessTools.PropertyGetter(typeof(Time), nameof(Time.unscaledDeltaTime)),
      nameof(VirtualOrRealUnscaledDeltaTime)
    );
    Map(AccessTools.PropertyGetter(typeof(Time), nameof(Time.unscaledTime)), nameof(VirtualOrRealUnscaledTime));

    foreach (CodeInstruction instruction in instructions)
    {
      foreach (KeyValuePair<MethodInfo, MethodInfo> redirect in redirects)
      {
        if (!instruction.Calls(redirect.Key))
          continue;
        // Mutated in place rather than replaced, so any branch labels and exception blocks
        // attached to this instruction stay attached.
        instruction.opcode = OpCodes.Call;
        instruction.operand = redirect.Value;
        break;
      }
      yield return instruction;
    }
  }

  private static AccessTools.FieldRef<scrConductor, double> _lastReportedPlayheadPosition;
  private static AccessTools.FieldRef<scrConductor, double> _previousFrameTime;
  private static bool _conductorFieldsResolved;
  private static bool _conductorFieldsUsable;

  /// <summary>
  /// Pins the conductor's DSP clock to the render's virtual timeline.
  ///
  /// scrConductor.Update advances <c>dspTime</c> one of two ways: from the real audio playhead when
  /// it moved, or by the unscaled frame delta when it did not. Making the playhead look unchanged
  /// and the frame delta look like zero neutralizes both, leaving the value assigned here intact
  /// for the rest of the method — which is what derives <c>songposition_minusi</c>, schedules hit
  /// sounds and fires beat events. Song position therefore advances exactly one frame step per
  /// rendered frame, independent of the real audio clock, of window focus, and of how long the
  /// frame actually took.
  /// </summary>
  public static void ConductorClockPrefix(scrConductor __instance)
  {
    ReplayRenderSession session = ReplayRenderSession.Current;
    if (session == null || !ReplayRenderSession.IsCapturingActive || __instance == null)
      return;

    if (!EnsureConductorFields())
      return;

    // Make the "playhead moved" branch see no movement.
    _lastReportedPlayheadPosition(__instance) = AudioSettings.dspTime;
    // Make the "advance by unscaled frame delta" branch add exactly zero.
    _previousFrameTime(__instance) = Time.unscaledTimeAsDouble;

    __instance.dspTime = session.VirtualDspTime;
  }

  /// <summary>
  /// Resolves the conductor's private clock fields lazily. Doing this in a static initializer would
  /// turn a renamed field in a future game build into a TypeInitializationException during patching,
  /// which is exactly the kind of failure that must not reach the user as a dead mod.
  /// </summary>
  private static bool EnsureConductorFields()
  {
    if (_conductorFieldsResolved)
      return _conductorFieldsUsable;

    _conductorFieldsResolved = true;
    try
    {
      _lastReportedPlayheadPosition = AccessTools.FieldRefAccess<scrConductor, double>("lastReportedPlayheadPosition");
      _previousFrameTime = AccessTools.FieldRefAccess<scrConductor, double>("previousFrameTime");
      _conductorFieldsUsable = _lastReportedPlayheadPosition != null && _previousFrameTime != null;
    }
    catch (Exception exception)
    {
      _conductorFieldsUsable = false;
      RenderLog.Error(
        "The conductor clock fields could not be resolved, so the render will follow the real audio "
          + "clock and desync. error="
          + exception.Message
      );
    }
    return _conductorFieldsUsable;
  }

  // ── Audio capture ───────────────────────────────────────────────────────────────────────────

  public static void CacheClipPrefix(string snd)
  {
    // Decode on the main thread: the encoder thread must never touch AudioClip.GetData().
    Mixer?.CacheClip(snd);
  }

  public static void RegisterPlayedSoundPostfix(
    AudioSource __result,
    string snd,
    double time,
    AudioMixerGroup group,
    float volume
  )
  {
    OfflineAudioMixer mixer = Mixer;
    if (mixer == null || __result == null)
      return;

    // Register the source's real pitch: pinning it to 1.0 would replay pitched hitsounds at the
    // wrong speed in the output.
    float sourcePitch = __result.pitch > 0f ? __result.pitch : 1f;
    mixer.RegisterSoundEvent(__result, snd, time - DspTimeBase, -1.0, volume, sourcePitch);
  }

  /// <summary>
  /// Registers one-shot SFX played through scrSfx.PlaySfx — the death sound, planet explosion,
  /// applause, clear jingle. These use AudioSource.PlayOneShot directly and never touch
  /// AudioManager._Play, so without this hook they are missing from the render (and are muted live
  /// by the render's listener mute).
  /// </summary>
  public static void RegisterSfxPostfix(UnityEngine.AudioClip clip, float volume, float pitch)
  {
    ReplayRenderSession session = ReplayRenderSession.Current;
    OfflineAudioMixer mixer = Mixer;
    if (mixer == null || clip == null || session == null)
      return;

    // The mixer's timeline is virtual seconds since capture start, not the real audio clock — a
    // render runs slower than realtime, so a real-clock timestamp would land the SFX far too late.
    // VirtualDspTime - DspTimeBase is exactly the current virtual elapsed the mixer expects.
    double now = session.VirtualDspTime - session.DspTimeBase;
    mixer.RegisterSfxClip(clip, now, volume > 0f ? volume : 1f, pitch > 0f ? pitch : 1f);
  }

  public static void RegisterConductorSoundPrefix(string snd, double time, double endTime, float volume)
  {
    OfflineAudioMixer mixer = Mixer;
    if (mixer == null)
      return;

    mixer.CacheClip(snd);

    float conductorPitch = 1f;
    if (scrConductor.instance != null && scrConductor.instance.song != null && scrConductor.instance.song.pitch > 0f)
      conductorPitch = scrConductor.instance.song.pitch;

    mixer.RegisterSoundEvent(snd, time - DspTimeBase, endTime - DspTimeBase, volume, conductorPitch);
  }

  /// <summary>
  /// The game silences every scheduled hitsound with StopAllSounds — on death, on quit, and right
  /// before PlayHitTimes re-schedules a batch. Under capture the pooled sources are long recycled,
  /// so destroying them tells the mixer nothing; mirror the stop on the offline timeline instead.
  /// Without this, hitsounds for tiles past a death keep playing in the output, and re-scheduled
  /// batches double up.
  /// </summary>
  public static void StopAllSoundsPostfix()
  {
    ReplayRenderSession session = ReplayRenderSession.Current;
    OfflineAudioMixer mixer = Mixer;
    if (mixer == null || session == null || !ReplayRenderSession.IsCapturingActive)
      return;
    mixer.CancelPendingSoundEvents(session.VirtualDspTime - session.DspTimeBase);
  }

  /// <summary>Diagnostic: logs whether hitsound scheduling happened while capture was active.</summary>
  public static void PlayHitTimesDiagnosticPostfix()
  {
    if (ReplayRenderSession.Current == null)
      return;
    RenderLog.Info(
      "PlayHitTimes fired. capturing=" + ReplayRenderSession.IsCapturingActive + ", mixer=" + (Mixer != null)
    );
  }

  // ── Environment ─────────────────────────────────────────────────────────────────────────────

  /// <summary>
  /// Disabling the custom frame rate releases the camera's RenderTexture even when that texture is
  /// the very camRT the capture path is reading. Apply the state change without the release while a
  /// session is active.
  /// </summary>
  public static bool SetCustomFrameRatePrefix(scrCamera __instance, bool enable, float frame)
  {
    if (!ReplayRenderSession.IsActive)
      return true;
    if (enable || __instance.enableCustomFPS != enable)
      return true;

    __instance.enableCustomFPS = enable;
    __instance.frameRate = frame;
    return false;
  }

  /// <summary>
  /// The game damps its async input offset assuming real time, which drifts under a virtual clock.
  /// </summary>
  public static bool AsyncInputOffsetPrefix()
  {
    if (!ReplayRenderSession.IsActive)
      return true;
    if (scrConductor.instance == null)
      return true;

    // ulong subtraction wraps to an enormous value when negative, so compute in long and clamp.
    long diff = (long)AsyncInputManager.currFrameTick - (long)(scrConductor.instance.dspTime * 10000000.0);
    AsyncInputManager.offsetTick = diff > 0 ? (ulong)diff : 0UL;
    AsyncInputManager.offsetTickUpdated = true;
    return false;
  }

  /// <summary>
  /// Portal particles opt out of scaled time, so they animate on the wall clock and ignore
  /// captureDeltaTime.
  /// </summary>
  public static void ForceScaledParticlesPostfix(scrPortalParticles __instance)
  {
    if (!ReplayRenderSession.IsActive || __instance == null)
      return;

    foreach (ParticleSystem particleSystem in __instance.GetComponentsInChildren<ParticleSystem>(true))
    {
      if (particleSystem == null)
        continue;
      ParticleSystem.MainModule main = particleSystem.main;
      main.useUnscaledTime = false;
    }
  }
}
