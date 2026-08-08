using System;
using AdofaiIpc.Core;
using TUFReplay.Features.Ipc;
using TUFReplayRenderer.Application.Render;
using TUFReplayRenderer.Domain.Render;
using TUFReplayRenderer.Features.Render;
using TUFReplayRenderer.Infrastructure.FFmpeg;
using TUFReplayRenderer.Infrastructure.Render;
using TUFReplayRenderer.Ipc.Dtos;

namespace TUFReplayRenderer.Features.Ipc;

public static class RenderIpcHandlers
{
  public static object GetCapabilities(IpcRequest request)
  {
    // Runs on the main thread, so a finished background FFmpeg download is promoted here and the
    // response below already reflects the new availability.
    FFmpegRuntimeInstaller.PromoteInstallOnMainThread();

    RenderFeature feature = RenderFeature.Instance;
    bool featureActive = feature is { Active: true };

    return new RenderCapabilitiesDto
    {
      Available = featureActive && FFmpegNativeLibrary.IsAvailable,
      UnavailableReason = !featureActive ? "The renderer feature is disabled." : ComposeUnavailableReason(feature),
      FFmpegDownloadState = FFmpegRuntimeInstaller.State,
      FFmpegDownloadProgressPercent = FFmpegRuntimeInstaller.ProgressPercent,
      FFmpegVersion = FFmpegNativeLibrary.IsAvailable ? FFmpegNativeLibrary.DescribeVersion() : null,
      FFmpegDirectory = FFmpegNativeLibrary.ResolvedDirectory,
      OutputDirectory = RenderOutputDirectory.Default,
      Codecs = Enum.GetNames(typeof(Codec)),
      RateControlModes = Enum.GetNames(typeof(RateControlMode)),
      Defaults = RenderSettingsDto.From(new ReplayRenderSettings()),
    };
  }

  /// <summary>
  /// The feature's own reason first, then the background download's status when it adds signal.
  /// </summary>
  private static string ComposeUnavailableReason(RenderFeature feature)
  {
    string reason = feature.UnavailableReason;
    string download = FFmpegRuntimeInstaller.DescribeForCapabilities();
    if (download == null)
      return reason;
    if (FFmpegNativeLibrary.IsAvailable)
      return reason;
    return download;
  }

  public static object Start(IpcRequest request)
  {
    // A render started right after the download finished should succeed without the UI having to
    // refresh capabilities first.
    FFmpegRuntimeInstaller.PromoteInstallOnMainThread();

    if (!IpcParams.TryRequiredString(request, "runId", out string runId))
      return IpcDomainError.Create("invalid_run_id", "runId must be a non-empty string.");

    string levelPath = IpcParams.OptionalString(request, "levelPath");
    object settingsOrError = ReadSettings(request);
    if (settingsOrError is not ReplayRenderSettings settings)
      return settingsOrError;

    return RenderStatusDto.From(ReplayRenderCoordinator.Start(runId, levelPath, settings));
  }

  /// <summary>Plays a short audition of the render's audio mix at the requested volumes.</summary>
  public static object StartPreview(IpcRequest request)
  {
    if (!IpcParams.TryRequiredString(request, "runId", out string runId))
      return IpcDomainError.Create("invalid_run_id", "runId must be a non-empty string.");

    object settingsOrError = ReadSettings(request);
    if (settingsOrError is not ReplayRenderSettings settings)
      return settingsOrError;

    return RenderStatusDto.From(ReplayRenderCoordinator.StartPreview(runId, settings));
  }

  public static object StopPreview(IpcRequest request) => RenderStatusDto.From(ReplayRenderCoordinator.StopPreview());

  /// <summary>
  /// Returns the finished audio preview as base64 WAV, once. Poll status until Completed first.
  /// </summary>
  public static object GetPreviewResult(IpcRequest request)
  {
    Application.Render.ReplayRenderSession.PreviewStemsResult stems = ReplayRenderCoordinator.TakePreviewResult();
    if (stems == null)
      return IpcDomainError.Create("preview_result_unavailable", "No audio preview is ready.");
    return new
    {
      MusicWavBase64 = Convert.ToBase64String(stems.Music),
      HitsoundsWavBase64 = Convert.ToBase64String(stems.Hitsounds),
      MicrophoneWavBase64 = stems.Microphone != null ? Convert.ToBase64String(stems.Microphone) : null,
      stems.SampleRate,
      stems.Channels,
    };
  }

  /// <summary>Parses render settings, returning an IPC error object when a value is invalid.</summary>
  private static object ReadSettings(IpcRequest request)
  {
    ReplayRenderSettings settings = new ReplayRenderSettings();

    double? renderFps = IpcParams.OptionalDouble(request, "renderFps");
    if (renderFps.HasValue)
      settings.RenderFps = renderFps.Value;

    double? videoFps = IpcParams.OptionalDouble(request, "videoFps");
    if (videoFps.HasValue)
      settings.VideoFps = videoFps.Value;
    else if (renderFps.HasValue)
      settings.VideoFps = renderFps.Value;

    int? width = IpcParams.OptionalInt(request, "width");
    if (width.HasValue)
      settings.Width = width.Value;

    int? height = IpcParams.OptionalInt(request, "height");
    if (height.HasValue)
      settings.Height = height.Value;

    string codec = IpcParams.OptionalString(request, "codec");
    if (!string.IsNullOrWhiteSpace(codec))
    {
      if (!Enum.TryParse(codec, true, out Codec parsedCodec))
        return IpcDomainError.Create(
          "invalid_codec",
          "codec must be one of: " + string.Join(", ", Enum.GetNames(typeof(Codec))) + "."
        );
      settings.Codec = parsedCodec;
    }

    string rateControl = IpcParams.OptionalString(request, "rateControlMode");
    if (!string.IsNullOrWhiteSpace(rateControl))
    {
      if (!Enum.TryParse(rateControl, true, out RateControlMode parsedMode))
        return IpcDomainError.Create(
          "invalid_rate_control_mode",
          "rateControlMode must be one of: " + string.Join(", ", Enum.GetNames(typeof(RateControlMode))) + "."
        );
      settings.RateControlMode = parsedMode;
    }

    int? qualityValue = IpcParams.OptionalInt(request, "qualityValue");
    if (qualityValue.HasValue)
      settings.QualityValue = qualityValue.Value;

    int? targetBitrate = IpcParams.OptionalInt(request, "targetBitrateKbps");
    if (targetBitrate.HasValue)
      settings.TargetBitrateKbps = targetBitrate.Value;

    int? maxBitrate = IpcParams.OptionalInt(request, "maxBitrateKbps");
    if (maxBitrate.HasValue)
      settings.MaxBitrateKbps = maxBitrate.Value;

    double? keyframeInterval = IpcParams.OptionalDouble(request, "keyframeIntervalSeconds");
    if (keyframeInterval.HasValue)
      settings.KeyframeIntervalSeconds = keyframeInterval.Value;

    bool? forceSoftware = IpcParams.OptionalBool(request, "forceSoftwareEncoder");
    if (forceSoftware.HasValue)
      settings.ForceSoftwareEncoder = forceSoftware.Value;

    bool? renderAudio = IpcParams.OptionalBool(request, "renderAudio");
    if (renderAudio.HasValue)
      settings.RenderAudio = renderAudio.Value;

    int? audioSampleRate = IpcParams.OptionalInt(request, "audioSampleRate");
    if (audioSampleRate.HasValue)
      settings.AudioSampleRate = audioSampleRate.Value;

    int? audioChannels = IpcParams.OptionalInt(request, "audioChannels");
    if (audioChannels.HasValue)
      settings.AudioChannels = audioChannels.Value;

    int? audioBitrate = IpcParams.OptionalInt(request, "audioBitrate");
    if (audioBitrate.HasValue)
      settings.AudioBitrate = audioBitrate.Value;

    bool? includeMicrophone = IpcParams.OptionalBool(request, "includeMicrophone");
    if (includeMicrophone.HasValue)
      settings.IncludeMicrophone = includeMicrophone.Value;

    double? trailingSeconds = IpcParams.OptionalDouble(request, "trailingSeconds");
    if (trailingSeconds.HasValue)
      settings.TrailingSeconds = trailingSeconds.Value;

    int? musicVolume = IpcParams.OptionalInt(request, "musicVolumePercent");
    if (musicVolume.HasValue)
      settings.MusicVolumePercent = musicVolume.Value;

    int? hitsoundVolume = IpcParams.OptionalInt(request, "hitsoundVolumePercent");
    if (hitsoundVolume.HasValue)
      settings.HitsoundVolumePercent = hitsoundVolume.Value;

    int? microphoneVolume = IpcParams.OptionalInt(request, "microphoneVolumePercent");
    if (microphoneVolume.HasValue)
      settings.MicrophoneVolumePercent = microphoneVolume.Value;

    int? microphoneTiming = IpcParams.OptionalInt(request, "microphoneTimingMs");
    if (microphoneTiming.HasValue)
      settings.MicrophoneTimingMs = microphoneTiming.Value;

    settings.OutputPath = IpcParams.OptionalString(request, "outputPath");
    return settings;
  }

  public static object GetStatus(IpcRequest request) => RenderStatusDto.From(ReplayRenderCoordinator.GetStatus());

  public static object Cancel(IpcRequest request) =>
    RenderStatusDto.From(ReplayRenderCoordinator.Cancel("Render cancelled by request."));
}
