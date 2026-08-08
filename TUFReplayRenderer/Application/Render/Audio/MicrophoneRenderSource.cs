#nullable enable
using System;
using System.IO;
using TUFReplay;
using TUFReplay.Application.Microphone;
using TUFReplay.Application.Replay;
using TUFReplay.Domain.Microphone;
using TUFReplayRenderer.Infrastructure.Render;

namespace TUFReplayRenderer.Application.Render.Audio;

/// <summary>
/// Mixes a run's saved microphone recording into the rendered soundtrack.
///
/// Live replay plays the recording through a streaming Unity AudioClip and nudges the playhead
/// whenever it drifts more than 50 ms (<see cref="Infrastructure.Unity.ReplayMicrophonePlayer"/>).
/// That is unusable offline, because the DSP clock is virtual. Instead the whole recording is
/// decoded once, the exact playback gain envelope is applied sequentially so the result is
/// sample-for-sample what the live limiter would have produced, and every output sample is then
/// placed by <see cref="ReplayMicrophoneClock"/> — the same pure mapping live playback uses, only
/// evaluated per sample rather than per frame. The rendered alignment is therefore at least as
/// accurate as what the user heard.
/// </summary>
public sealed class MicrophoneRenderSource
{
  private readonly float[] _samples;
  private readonly int _sourceChannels;
  private readonly int _sourceSampleRate;
  private readonly long _frameCount;
  private readonly long _captureStartOffsetUs;
  private bool _loggedFirstMix;

  private MicrophoneRenderSource(
    float[] samples,
    int sourceChannels,
    int sourceSampleRate,
    long frameCount,
    long captureStartOffsetUs
  )
  {
    _samples = samples;
    _sourceChannels = sourceChannels;
    _sourceSampleRate = sourceSampleRate;
    _frameCount = frameCount;
    _captureStartOffsetUs = captureStartOffsetUs;
  }

  public long FrameCount => _frameCount;

  public double DurationSeconds => _sourceSampleRate <= 0 ? 0d : (double)_frameCount / _sourceSampleRate;

  /// <summary>
  /// Decodes the recording and bakes in the user's offset and volume plus the limiter envelope.
  /// Returns null when the recording is missing or unusable; a failed microphone track must never
  /// fail the whole render.
  /// </summary>
  internal static MicrophoneRenderSource? TryCreate(
    StoredMicrophoneRecording? recording,
    Pcm16WaveInfo? wave,
    Pcm16LimiterEnvelope? limiterEnvelope,
    int userOffsetMs,
    int volumeDb
  )
  {
    if (recording == null || wave == null || limiterEnvelope == null)
      return null;

    try
    {
      if (wave.Channels <= 0 || wave.SampleRate <= 0 || wave.FrameCount <= 0)
        return null;

      long totalSamplesLong = wave.FrameCount * wave.Channels;
      if (totalSamplesLong > int.MaxValue)
        throw new InvalidDataException("The microphone recording is too long to render.");

      int clampedOffsetMs = Math.Max(
        TUFReplaySetting.MinMicrophoneOffsetMs,
        Math.Min(TUFReplaySetting.MaxMicrophoneOffsetMs, userOffsetMs)
      );
      int clampedVolumeDb = Math.Max(
        TUFReplaySetting.MinMicrophoneVolumeDb,
        Math.Min(TUFReplaySetting.MaxMicrophoneVolumeDb, volumeDb)
      );
      float requestedGain = MicrophoneGain.FromDecibels(clampedVolumeDb);

      float[] samples = new float[(int)totalSamplesLong];
      Pcm16Limiter limiter = new Pcm16Limiter(limiterEnvelope, wave.SampleRate);

      using (
        FileStream stream = new FileStream(recording.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16)
      )
      {
        stream.Seek(wave.DataOffset, SeekOrigin.Begin);

        int channels = wave.Channels;
        byte[] buffer = new byte[1 << 16];
        long remainingBytes = Math.Min(wave.DataLength, totalSamplesLong * 2L);
        int writeIndex = 0;
        long frame = 0;
        int carry = 0;

        while (remainingBytes > 0 && writeIndex < samples.Length)
        {
          int wanted = (int)Math.Min(buffer.Length - carry, remainingBytes);
          int read = stream.Read(buffer, carry, wanted);
          if (read <= 0)
            break;
          remainingBytes -= read;

          int usable = carry + read;
          int wholeFrameBytes = usable - usable % (channels * 2);

          for (int offset = 0; offset < wholeFrameBytes; offset += channels * 2)
          {
            float effectiveGain = limiter.NextEffectiveGain(frame, requestedGain);
            for (int channel = 0; channel < channels; channel++)
            {
              int byteIndex = offset + channel * 2;
              short sample = (short)(buffer[byteIndex] | (buffer[byteIndex + 1] << 8));
              float value = sample / 32768f * effectiveGain;
              samples[writeIndex++] =
                value > 1f ? 1f
                : value < -1f ? -1f
                : value;
            }
            frame++;
          }

          carry = usable - wholeFrameBytes;
          if (carry > 0)
            Array.Copy(buffer, wholeFrameBytes, buffer, 0, carry);
        }

        if (frame < wave.FrameCount)
        {
          RenderLog.Warn(
            "Microphone recording ended early. frames=" + frame + "/" + wave.FrameCount + "; padding with silence."
          );
        }
      }

      // The user offset is a latency correction on the capture start, exactly as in live playback.
      long effectiveCaptureOffsetUs = ReplayMicrophoneClock.ApplyLatencyCorrection(
        recording.CaptureStartOffsetUs,
        clampedOffsetMs * 1000L
      );

      RenderLog.Info(
        "Microphone track ready. frames="
          + wave.FrameCount
          + ", sampleRate="
          + wave.SampleRate
          + ", channels="
          + wave.Channels
          + ", offsetMs="
          + clampedOffsetMs
          + ", volumeDb="
          + clampedVolumeDb
      );

      return new MicrophoneRenderSource(
        samples,
        wave.Channels,
        wave.SampleRate,
        wave.FrameCount,
        effectiveCaptureOffsetUs
      );
    }
    catch (Exception exception)
    {
      RenderLog.Error("Microphone track unavailable; rendering without it. error=" + exception.Message);
      return null;
    }
  }

  /// <summary>
  /// Adds this source into an interleaved output block.
  /// </summary>
  /// <param name="mixedBuffer">Interleaved target buffer at the render sample rate.</param>
  /// <param name="samplesNeeded">Output frames to produce.</param>
  /// <param name="targetChannels">Output channel count.</param>
  /// <param name="targetSampleRate">Output sample rate.</param>
  /// <param name="frameReplayTimeUs">Replay timeline position at the first output sample.</param>
  /// <param name="gameplayRate">Effective run pitch; replay time advances at this rate.</param>
  /// <param name="wonTimeUs">Recorded clear time, after which the timeline runs at 1x.</param>
  /// <param name="volumeScale">Global mix trim applied to keep headroom for the master chain.</param>
  public void Mix(
    float[] mixedBuffer,
    int samplesNeeded,
    int targetChannels,
    int targetSampleRate,
    long frameReplayTimeUs,
    double gameplayRate,
    long? wonTimeUs,
    float volumeScale
  )
  {
    if (_samples.Length == 0 || samplesNeeded <= 0 || targetSampleRate <= 0)
      return;

    double rate =
      gameplayRate > 0d && !double.IsNaN(gameplayRate) && !double.IsInfinity(gameplayRate) ? gameplayRate : 1d;

    if (!_loggedFirstMix)
    {
      _loggedFirstMix = true;
      double firstMicSec =
        ReplayMicrophoneClock.ToMicrophoneTimeUs(frameReplayTimeUs, rate, _captureStartOffsetUs, wonTimeUs)
        / 1_000_000d;
      RenderLog.Info(
        "Mic placement: replayAnchorUs="
          + frameReplayTimeUs
          + ", captureOffsetUs="
          + _captureStartOffsetUs
          + ", rate="
          + rate.ToString("F3")
          + ", firstMicSec="
          + firstMicSec.ToString("F3")
      );
    }

    for (int s = 0; s < samplesNeeded; s++)
    {
      // Replay time is song time: within a frame it advances at the run's pitch. Past the recorded
      // clear the timeline is already 1x, which ReplayMicrophoneClock accounts for.
      double advanceUs =
        (double)s
        / targetSampleRate
        * 1_000_000d
        * (wonTimeUs.HasValue && frameReplayTimeUs >= wonTimeUs.Value ? 1d : rate);
      long replayTimeUs = frameReplayTimeUs + (long)advanceUs;

      double microphoneTimeUs = ReplayMicrophoneClock.ToMicrophoneTimeUs(
        replayTimeUs,
        rate,
        _captureStartOffsetUs,
        wonTimeUs
      );
      if (microphoneTimeUs < 0d)
        continue;

      double sourceFrame = microphoneTimeUs * _sourceSampleRate / 1_000_000d;
      if (sourceFrame >= _frameCount)
        break;

      long indexLow = (long)Math.Floor(sourceFrame);
      long indexHigh = indexLow + 1;
      float t = (float)(sourceFrame - indexLow);
      if (indexHigh >= _frameCount)
        indexHigh = _frameCount - 1;
      if (indexLow >= _frameCount)
        indexLow = _frameCount - 1;
      if (indexLow < 0)
        continue;

      for (int channel = 0; channel < targetChannels; channel++)
      {
        int sourceChannel = channel < _sourceChannels ? channel : 0;
        float low = _samples[indexLow * _sourceChannels + sourceChannel];
        float high = _samples[indexHigh * _sourceChannels + sourceChannel];
        mixedBuffer[s * targetChannels + channel] += (low + t * (high - low)) * volumeScale;
      }
    }
  }
}
