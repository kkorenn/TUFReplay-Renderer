using System;
using HarmonyLib;
using TUFReplayRenderer.Application.Render;
using TUFReplayRenderer.Application.Render.Audio;
using UnityEngine;

namespace TUFReplayRenderer.Features.Render;

/// <summary>
/// Harmony patches that target UnityEngine rather than the game's assembly.
///
/// These are applied one at a time, each guarded, instead of through <c>Harmony.PatchAll</c>. Many
/// UnityEngine members are <c>InternalCall</c> externs, and whether Mono can generate a valid patch
/// wrapper for one varies by member and by runtime — <c>Time.get_time</c> throws
/// <c>InvalidProgramException</c> under UnityModManager's Mono, for example. A single failure
/// inside <c>PatchAll</c> aborts the whole batch and takes the entire mod down with it, which is
/// exactly what must not happen: recording and replay have to keep working even when a
/// render-only refinement cannot be installed.
///
/// Everything here is a refinement, never a requirement. If one fails to apply, rendering still
/// works; the listed detail just degrades.
/// </summary>
public static class RenderOptionalUnityPatches
{
  internal delegate void OptionalPatch(
    Harmony harmony,
    Type container,
    string label,
    Func<System.Reflection.MethodBase> resolve,
    string prefix,
    string postfix
  );

  internal static void Apply(Harmony harmony, OptionalPatch optional)
  {
    // Without this, hit sounds are still audible in real time while the render runs at a different
    // speed. Cosmetic only: the rendered audio comes from the offline mixer either way.
    optional(
      harmony,
      typeof(RenderOptionalUnityPatches),
      "AudioSource.PlayScheduled",
      () => AccessTools.Method(typeof(AudioSource), "PlayScheduled", new[] { typeof(double) }),
      nameof(SuppressScheduledPlaybackPrefix),
      null
    );

    // Without this, spectrum-reactive decorations read the silenced live mixer and sit flat.
    optional(
      harmony,
      typeof(RenderOptionalUnityPatches),
      "AudioSource.GetSpectrumData",
      () =>
        AccessTools.Method(
          typeof(AudioSource),
          "GetSpectrumData",
          new[] { typeof(float[]), typeof(int), typeof(FFTWindow) }
        ),
      nameof(OfflineSpectrumPrefix),
      null
    );

    // Without this, a hit sound whose pitch is changed after it starts renders at its original
    // pitch.
    optional(
      harmony,
      typeof(RenderOptionalUnityPatches),
      "AudioSource.pitch",
      () => AccessTools.PropertySetter(typeof(AudioSource), "pitch"),
      null,
      nameof(PitchChangedPostfix)
    );

    // Without this, a sound given an explicit end time renders to its natural length instead.
    optional(
      harmony,
      typeof(RenderOptionalUnityPatches),
      "AudioSource.SetScheduledEndTime",
      () => AccessTools.Method(typeof(AudioSource), "SetScheduledEndTime", new[] { typeof(double) }),
      null,
      nameof(ScheduledEndTimePostfix)
    );

    // Without this, a looping sound that the game stops keeps playing for the rest of the render.
    optional(
      harmony,
      typeof(RenderOptionalUnityPatches),
      "AudioSource.Stop",
      () => AccessTools.Method(typeof(AudioSource), "Stop", Type.EmptyTypes),
      null,
      nameof(StoppedPostfix)
    );
  }

  // ── Patch bodies ────────────────────────────────────────────────────────────────────────────

  public static bool SuppressScheduledPlaybackPrefix() => !ReplayRenderSession.IsCapturingActive;

  public static bool OfflineSpectrumPrefix(float[] samples, int channel, FFTWindow window)
  {
    if (!ReplayRenderSession.IsCapturingActive || !ReplayRenderSession.Current.Settings.RenderAudio)
      return true;
    AudioSpectrumGenerator.GetSpectrumData(samples, channel, window);
    return false;
  }

  public static void PitchChangedPostfix(AudioSource __instance, float value)
  {
    OfflineAudioMixer mixer = RenderGamePatches.Mixer;
    if (mixer == null)
      return;

    // Pooled AudioSources can still map to a finished event, so the mixer decides whether the
    // change still applies rather than retroactively repitching old audio.
    mixer.UpdateSourcePitch(__instance, value, AudioSettings.dspTime - RenderGamePatches.DspTimeBase);
  }

  public static void ScheduledEndTimePostfix(AudioSource __instance, double time)
  {
    OfflineAudioMixer mixer = RenderGamePatches.Mixer;
    if (mixer != null && mixer.TryGetSoundEvent(__instance, out OfflineAudioMixer.SoundEvent soundEvent))
      soundEvent.EndTime = time - RenderGamePatches.DspTimeBase;
  }

  public static void StoppedPostfix(AudioSource __instance)
  {
    OfflineAudioMixer mixer = RenderGamePatches.Mixer;
    if (mixer == null)
      return;

    // Dropping the mapping alone would leave the event playing — forever, for a looping sound —
    // so a stop that lands while the sound is playing truncates it to now.
    //
    // The guard on StartTime is what ADOFAIRenderer's version of this patch lacks and what this
    // environment requires: PlayScheduled is suppressed during capture, so scheduled sources are
    // never "playing", and AudioManager.Update immediately recycles them with a Stop() call.
    // Without the guard that pooled Stop truncated every scheduled hitsound to its birth time and
    // the render lost all of them.
    double now = AudioSettings.dspTime - RenderGamePatches.DspTimeBase;
    if (
      mixer.TryGetSoundEvent(__instance, out OfflineAudioMixer.SoundEvent soundEvent)
      && now > soundEvent.StartTime
      && (soundEvent.EndTime < 0 || soundEvent.EndTime > now)
    )
    {
      soundEvent.EndTime = now;
    }
    mixer.RemoveSourceMapping(__instance);
  }
}
