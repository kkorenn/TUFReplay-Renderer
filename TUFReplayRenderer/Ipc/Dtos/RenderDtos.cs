using TUFReplayRenderer.Domain.Render;

namespace TUFReplayRenderer.Ipc.Dtos;

public sealed class RenderStatusDto
{
  public string OperationId;
  public string RunId;
  public string State;
  public string ErrorCode;
  public string Message;
  public long FramesEncoded;
  public long FramesCaptured;
  public double EncodedSeconds;
  public string OutputPath;
  public int Width;
  public int Height;
  public double VideoFps;
  public string EncoderName;
  public bool MicrophoneIncluded;

  public static RenderStatusDto From(RenderStatus status)
  {
    status ??= RenderStatus.Idle();
    return new RenderStatusDto
    {
      OperationId = status.OperationId,
      RunId = status.RunId,
      State = status.State,
      ErrorCode = status.ErrorCode,
      Message = status.Message,
      FramesEncoded = status.FramesEncoded,
      FramesCaptured = status.FramesCaptured,
      EncodedSeconds = status.EncodedSeconds,
      OutputPath = status.OutputPath,
      Width = status.Width,
      Height = status.Height,
      VideoFps = status.VideoFps,
      EncoderName = status.EncoderName,
      MicrophoneIncluded = status.MicrophoneIncluded,
    };
  }
}

/// <summary>Reports whether this machine can render at all, and with which defaults.</summary>
public sealed class RenderCapabilitiesDto
{
  public bool Available;
  public string UnavailableReason;

  public string RendererVersion;
  public string TufReplayVersion;

  /// <summary>Bridge contract compatibility: ok | tufreplay_outdated | renderer_outdated.</summary>
  public string BridgeStatus;
  public int BridgeApiVersionDetected;
  public int BridgeApiVersionRequired;

  /// <summary>Background FFmpeg download: idle | downloading | installed | failed | unnecessary.</summary>
  public string FFmpegDownloadState;
  public int FFmpegDownloadProgressPercent;
  public string FFmpegVersion;
  public string FFmpegDirectory;
  public string OutputDirectory;
  public string[] Codecs;
  public string[] RateControlModes;
  public RenderSettingsDto Defaults;
}

public sealed class RenderSettingsDto
{
  public double RenderFps;
  public double VideoFps;
  public int Width;
  public int Height;
  public string Codec;
  public string RateControlMode;
  public int QualityValue;
  public int TargetBitrateKbps;
  public int MaxBitrateKbps;
  public double KeyframeIntervalSeconds;
  public bool ForceSoftwareEncoder;
  public bool RenderAudio;
  public int AudioSampleRate;
  public int AudioChannels;
  public int AudioBitrate;
  public bool IncludeMicrophone;
  public double TrailingSeconds;
  public int MusicVolumePercent;
  public int HitsoundVolumePercent;
  public int MicrophoneVolumePercent;

  public static RenderSettingsDto From(ReplayRenderSettings settings) =>
    new RenderSettingsDto
    {
      RenderFps = settings.RenderFps,
      VideoFps = settings.VideoFps,
      Width = settings.Width,
      Height = settings.Height,
      Codec = settings.Codec.ToString(),
      RateControlMode = settings.RateControlMode.ToString(),
      QualityValue = settings.QualityValue,
      TargetBitrateKbps = settings.TargetBitrateKbps,
      MaxBitrateKbps = settings.MaxBitrateKbps,
      KeyframeIntervalSeconds = settings.KeyframeIntervalSeconds,
      ForceSoftwareEncoder = settings.ForceSoftwareEncoder,
      RenderAudio = settings.RenderAudio,
      AudioSampleRate = settings.AudioSampleRate,
      AudioChannels = settings.AudioChannels,
      AudioBitrate = settings.AudioBitrate,
      IncludeMicrophone = settings.IncludeMicrophone,
      TrailingSeconds = settings.TrailingSeconds,
      MusicVolumePercent = settings.MusicVolumePercent,
      HitsoundVolumePercent = settings.HitsoundVolumePercent,
      MicrophoneVolumePercent = settings.MicrophoneVolumePercent,
    };
}
