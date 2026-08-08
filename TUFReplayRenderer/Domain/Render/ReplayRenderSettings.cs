using System;

namespace TUFReplayRenderer.Domain.Render;

/// <summary>
/// Everything the renderer needs for one replay capture session. Values are validated and clamped
/// by <see cref="Normalize"/> before a session starts, so the encoder never sees a nonsensical
/// resolution or frame rate.
/// </summary>
public sealed class ReplayRenderSettings
{
  public const int MinDimension = 16;
  public const int MaxDimension = 7680;
  public const double MinFps = 1d;
  public const double MaxFps = 480d;

  /// <summary>Simulation frame rate. Frame N always shows the world at t = N / RenderFps.</summary>
  public double RenderFps { get; set; } = 60d;

  /// <summary>Output frame rate. Never upsampled above <see cref="RenderFps"/>.</summary>
  public double VideoFps { get; set; } = 60d;

  /// <summary>
  /// Output width. Screen.width is faked to this value for the whole session so the game lays out
  /// its UI at output resolution and scrCamera allocates its RenderTexture to match.
  /// </summary>
  public int Width { get; set; } = 1920;

  public int Height { get; set; } = 1080;

  public Codec Codec { get; set; } = Codec.H264;
  public RateControlMode RateControlMode { get; set; } = RateControlMode.CQP;

  /// <summary>Constant-quality value (0-51) used when <see cref="RateControlMode"/> is CQP.</summary>
  public int QualityValue { get; set; } = 18;

  /// <summary>Target bitrate in kbps for CBR/VBR.</summary>
  public int TargetBitrateKbps { get; set; } = 12000;

  /// <summary>Maximum bitrate in kbps for VBR.</summary>
  public int MaxBitrateKbps { get; set; } = 20000;

  /// <summary>Keyframe interval in seconds.</summary>
  public double KeyframeIntervalSeconds { get; set; } = 2d;

  public bool ForceSoftwareEncoder { get; set; }

  public bool RenderAudio { get; set; } = true;
  public int AudioSampleRate { get; set; } = 48000;
  public int AudioChannels { get; set; } = 2;
  public int AudioBitrate { get; set; } = 256000;

  /// <summary>Mix the run's saved microphone recording into the output when one exists.</summary>
  public bool IncludeMicrophone { get; set; } = true;

  /// <summary>Background music level, as a percentage of its normal render level.</summary>
  public int MusicVolumePercent { get; set; } = 100;

  /// <summary>Hit sound level, as a percentage of its normal render level.</summary>
  public int HitsoundVolumePercent { get; set; } = 100;

  /// <summary>Microphone level, as a percentage of its normal render level.</summary>
  public int MicrophoneVolumePercent { get; set; } = 100;

  /// <summary>
  /// Extra microphone timing trim in milliseconds, added on top of the calibration offset.
  /// Positive plays the microphone earlier, matching the calibration offset's convention.
  /// </summary>
  public int MicrophoneTimingMs { get; set; }

  /// <summary>Seconds of extra footage captured after the run reaches its terminal state.</summary>
  public double TrailingSeconds { get; set; } = 3d;

  /// <summary>Absolute output path. Assigned by the coordinator when the caller leaves it empty.</summary>
  public string OutputPath { get; set; }

  /// <summary>
  /// Audio-only preview mode: no video is encoded and no file is written. The replay fast-forwards
  /// to <see cref="PreviewStartSeconds"/>, mixes <see cref="PreviewDurationSeconds"/> of the exact
  /// render soundtrack, and hands the PCM back for the UI to play.
  /// </summary>
  public bool AudioPreviewOnly { get; set; }

  /// <summary>Timeline position (seconds from capture start) where the preview window opens.</summary>
  public double PreviewStartSeconds { get; set; }

  public double PreviewDurationSeconds { get; set; } = 10d;

  /// <summary>Effective output frame rate; capture never upsamples beyond the simulation rate.</summary>
  public double EffectiveVideoFps => Math.Min(VideoFps, RenderFps);

  public void Normalize()
  {
    RenderFps = Clamp(RenderFps, MinFps, MaxFps, 60d);
    VideoFps = Clamp(VideoFps, MinFps, MaxFps, 60d);
    Width = ClampDimension(Width, 1920);
    Height = ClampDimension(Height, 1080);

    // libavcodec's YUV420 chroma subsampling requires even dimensions; an odd size silently
    // produces a sheared final row rather than an open failure.
    Width -= Width % 2;
    Height -= Height % 2;

    QualityValue = Math.Max(0, Math.Min(51, QualityValue));
    TargetBitrateKbps = Math.Max(1, TargetBitrateKbps);
    MaxBitrateKbps = Math.Max(TargetBitrateKbps, MaxBitrateKbps);
    KeyframeIntervalSeconds = Clamp(KeyframeIntervalSeconds, 0.1d, 60d, 2d);
    AudioChannels = AudioChannels == 1 ? 1 : 2;
    AudioSampleRate = AudioSampleRate is 44100 or 48000 ? AudioSampleRate : 48000;
    AudioBitrate = Math.Max(32000, Math.Min(512000, AudioBitrate));
    TrailingSeconds = Clamp(TrailingSeconds, 0d, 30d, 3d);
    MusicVolumePercent = ClampPercent(MusicVolumePercent);
    HitsoundVolumePercent = ClampPercent(HitsoundVolumePercent);
    MicrophoneVolumePercent = ClampPercent(MicrophoneVolumePercent);
    MicrophoneTimingMs = Math.Max(-400, Math.Min(400, MicrophoneTimingMs));
  }

  public ReplayRenderSettings Clone() => (ReplayRenderSettings)MemberwiseClone();

  /// <summary>Volume percentages are clamped to 0-200%: silent through a 2x boost.</summary>
  private static int ClampPercent(int value) => Math.Max(0, Math.Min(200, value));

  private static int ClampDimension(int value, int fallback)
  {
    if (value <= 0)
      return fallback;
    return Math.Max(MinDimension, Math.Min(MaxDimension, value));
  }

  private static double Clamp(double value, double min, double max, double fallback)
  {
    if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0d)
      return fallback;
    return Math.Max(min, Math.Min(max, value));
  }
}
