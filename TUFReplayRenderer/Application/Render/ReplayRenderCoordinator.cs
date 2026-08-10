#nullable enable
using System;
using TUFReplay;
using TUFReplay.Application.Microphone;
using TUFReplay.Application.Replay;
using TUFReplay.Domain.Microphone;
using TUFReplay.Infrastructure.Settings;
using TUFReplayRenderer.Application.Render.Audio;
using TUFReplayRenderer.Domain.Render;
using TUFReplayRenderer.Features.Render;
using TUFReplayRenderer.Infrastructure.FFmpeg;
using TUFReplayRenderer.Infrastructure.Render;

namespace TUFReplayRenderer.Application.Render;

/// <summary>
/// Public entry point for rendering a saved run to a video file.
///
/// The run is played by the normal replay pipeline — same hit contexts, same judgements, same
/// clear screen — with the renderer attached to it. This type owns the handshake between the two:
/// it starts the replay in capture mode, installs the capture environment right before play mode
/// begins, and stops capture when the run reaches its recorded terminal state.
/// </summary>
public static class ReplayRenderCoordinator
{
  private static readonly object Gate = new object();

  private static RenderStatus _status = RenderStatus.Idle();
  private static ReplayRenderSession? _session;
  private static ReplayRenderSettings? _pendingSettings;
  private static string? _pendingRunId;
  private static string? _operationId;
  private static MicrophoneRenderSource? _pendingMicrophone;
  private static string? _pendingMicrophoneFile;
  private static bool _returnToEditorPending;

  public static bool IsBusy
  {
    get
    {
      lock (Gate)
      {
        if (_session != null && !_session.IsFinished)
          return true;
        return _pendingSettings != null;
      }
    }
  }

  /// <summary>
  /// True when the replay may enter play mode. While a capture session is preparing (song reload,
  /// resolution change) the replay coordinator holds <c>scnEditor.Play()</c> on this.
  /// </summary>
  public static bool ReadyForPlayMode
  {
    get
    {
      lock (Gate)
        return _session == null || _session.ReadyForPlayMode;
    }
  }

  public static RenderStatus GetStatus()
  {
    lock (Gate)
    {
      ReplayRenderSession? session = _session;
      if (session != null)
        return session.GetStatus();
      return _status.Clone();
    }
  }

  /// <summary>
  /// Validates and starts a render. Returns the initial status; callers poll
  /// <see cref="GetStatus"/> for progress.
  /// </summary>
  public static RenderStatus Start(string runId, string levelPath, ReplayRenderSettings settings)
  {
    if (settings == null)
      throw new ArgumentNullException(nameof(settings));

    settings.Normalize();

    lock (Gate)
    {
      if (RenderFeature.Instance is not { Active: true })
        return Fail(null, runId, "render_unavailable", "The renderer is not enabled.");

      if (!RenderFeature.Instance.CanRender && RenderPatchInstaller.BlockingFailure != null)
        return Fail(null, runId, "render_patches_unavailable", RenderPatchInstaller.BlockingFailure);

      if (IsBusyLocked())
        return Fail(null, runId, "render_busy", "Another replay render is already running.");

      if (!FFmpegNativeLibrary.IsAvailable)
        return Fail(
          null,
          runId,
          "ffmpeg_unavailable",
          FFmpegNativeLibrary.FailureMessage ?? "FFmpeg shared libraries are unavailable."
        );

      if (ReplayPlaybackCoordinator.IsBusy)
        return Fail(null, runId, "replay_busy", "A replay is already running.");

      ClearPendingLocked();
      _pendingSettings = settings;
      _pendingRunId = runId;
      _status = new RenderStatus
      {
        RunId = runId,
        State = RenderStates.Preparing,
        Message = "Preparing replay render.",
        Width = settings.Width,
        Height = settings.Height,
        VideoFps = settings.EffectiveVideoFps,
      };
    }

    ReplayPlaybackStatus replayStatus = ReplayPlaybackCoordinator.Play(runId, levelPath, true);

    lock (Gate)
    {
      if (replayStatus.State == ReplayPlaybackStates.Error)
      {
        ClearPendingLocked();
        return Fail(
          replayStatus.OperationId,
          runId,
          replayStatus.ErrorCode ?? "replay_failed",
          replayStatus.Message ?? "The replay could not be started."
        );
      }

      _operationId = replayStatus.OperationId;
      _status.OperationId = replayStatus.OperationId;
      return _status.Clone();
    }
  }

  public static RenderStatus Cancel(string reason = "Render cancelled.")
  {
    lock (Gate)
    {
      if (_session == null && _pendingSettings == null)
        return _status.Clone();
    }

    // Cancelling the replay funnels back into OnReplayTerminal, which tears the session down.
    ReplayPlaybackCoordinator.Cancel(reason);

    lock (Gate)
    {
      _session?.Cancel(reason);
      ClearPendingLocked();
      return GetStatusLocked();
    }
  }

  /// <summary>
  /// Renders ten seconds of audio only, from a random spot in the middle of the run, through the
  /// exact render pipeline — same replay playback, same mixer, same volumes. The resulting WAV is
  /// fetched with <see cref="TakePreviewResult"/> once the status reaches Completed.
  /// </summary>
  public static RenderStatus StartPreview(string runId, ReplayRenderSettings settings)
  {
    settings.AudioPreviewOnly = true;
    settings.RenderAudio = true;

    // A random window between 25% and 75% of the run. The replay timeline is anchored at the
    // run's start, so the terminal time is the run's full length on that clock.
    try
    {
      TUFReplay.Domain.ReplayData.StoredReplayRun? run =
        TUFReplay.Infrastructure.Database.Repositories.RunRepository.GetReplayRun(runId);
      TUFReplay.Domain.ReplayData.ReplayMetadata? meta =
        run == null
          ? null
          : Newtonsoft.Json.JsonConvert.DeserializeObject<TUFReplay.Domain.ReplayData.ReplayMetadata>(
            run.MetaJson ?? "{}"
          );
      double terminalSeconds = (meta?.terminalTimeUs ?? 0L) / 1_000_000d;
      double percent = 0.25d + UnityEngine.Random.value * 0.5d;
      double start = terminalSeconds > 0d ? terminalSeconds * percent : 0d;
      start = Math.Max(0d, Math.Min(start, Math.Max(0d, terminalSeconds - settings.PreviewDurationSeconds)));
      settings.PreviewStartSeconds = start;
      RenderLog.Info(
        "Audio preview window: "
          + start.ToString("F1")
          + "s ("
          + (percent * 100d).ToString("F0")
          + "% of "
          + terminalSeconds.ToString("F1")
          + "s)"
      );
    }
    catch (Exception exception)
    {
      RenderLog.Warn("Could not compute the preview window (" + exception.Message + "); starting from 0s.");
      settings.PreviewStartSeconds = 0d;
    }

    lock (Gate)
      _previewStems = null;

    return Start(runId, null!, settings);
  }

  /// <summary>Cancels a running audio preview. No effect on a full render.</summary>
  public static RenderStatus StopPreview()
  {
    ReplayRenderSession? session;
    lock (Gate)
      session = _session;

    if (session != null && session.Settings.AudioPreviewOnly && !session.IsFinished)
      return Cancel("Audio preview stopped.");

    lock (Gate)
      return GetStatusLocked();
  }

  /// <summary>
  /// Returns the finished preview stems once, then clears them. Null while none are available.
  /// </summary>
  public static ReplayRenderSession.PreviewStemsResult? TakePreviewResult()
  {
    lock (Gate)
    {
      ReplayRenderSession.PreviewStemsResult? stems = _previewStems;
      _previewStems = null;
      return stems;
    }
  }

  private static ReplayRenderSession.PreviewStemsResult? _previewStems;

  public static void Shutdown()
  {
    lock (Gate)
    {
      _session?.Cancel("The mod was disabled.");
      _session = null;
      ClearPendingLocked();
      _status = RenderStatus.Idle();
      _returnToEditorPending = false;
    }
  }

  // ── Replay pipeline callbacks ───────────────────────────────────────────────────────────────

  /// <summary>
  /// Decodes the run's microphone recording for offline mixing and takes ownership of the
  /// temporary WAV. Called on the main thread just before the replay enters play mode.
  /// </summary>
  internal static void AttachMicrophone(
    string operationId,
    StoredMicrophoneRecording recording,
    Pcm16WaveInfo wave,
    Pcm16LimiterEnvelope limiterEnvelope
  )
  {
    lock (Gate)
    {
      if (!MatchesOperationLocked(operationId))
        return;

      if (_pendingSettings is { IncludeMicrophone: false })
      {
        _pendingMicrophoneFile = recording?.FilePath;
        return;
      }

      TUFReplaySetting settings = TUFReplaySettingStore.Current;
      _pendingMicrophone = MicrophoneRenderSource.TryCreate(
        recording,
        wave,
        limiterEnvelope,
        (settings?.MicrophoneOffsetMs ?? 0) + (_pendingSettings?.MicrophoneTimingMs ?? 0),
        settings?.MicrophoneVolumeDb ?? 0
      );
      _pendingMicrophoneFile = recording?.FilePath;
    }
  }

  /// <summary>
  /// Installs the capture environment immediately before <c>scnEditor.Play()</c>.
  /// </summary>
  internal static void OnReplayStarting(string operationId)
  {
    lock (Gate)
    {
      if (_pendingSettings == null)
        return;
      if (_operationId != null && !string.Equals(_operationId, operationId, StringComparison.Ordinal))
        return;

      _operationId = operationId;
      ReplayRenderSession session = new ReplayRenderSession(
        operationId,
        _pendingRunId ?? string.Empty,
        _pendingSettings
      );
      session.AttachMicrophone(_pendingMicrophone, _pendingMicrophoneFile);
      _pendingMicrophone = null;
      _pendingMicrophoneFile = null;
      _pendingSettings = null;
      _session = session;
      session.BeginEnvironment();
    }
  }

  /// <summary>
  /// Called when the replay reaches a terminal state.
  /// </summary>
  /// <param name="allowTrailingCapture">
  /// True when the game stays in play mode (a clear or fail screen), so the configured trailing
  /// seconds are worth capturing. False when the editor scene is about to replace the view.
  /// </param>
  internal static void OnReplayTerminal(
    string operationId,
    string replayState,
    bool allowTrailingCapture,
    string message,
    string? errorCode = null
  )
  {
    lock (Gate)
    {
      ReplayRenderSession? session = _session;
      if (session == null || !string.Equals(session.OperationId, operationId, StringComparison.Ordinal))
      {
        // The render never got as far as installing a session.
        if (MatchesOperationLocked(operationId) && _pendingSettings != null)
        {
          ClearPendingLocked();
          _status.State = replayState == ReplayPlaybackStates.Error ? RenderStates.Error : RenderStates.Cancelled;
          _status.ErrorCode = errorCode;
          _status.Message = message;
        }
        return;
      }

      switch (replayState)
      {
        case ReplayPlaybackStates.Completed:
          session.RequestStop(true, allowTrailingCapture, message);
          break;
        case ReplayPlaybackStates.Error:
          session.Fail(errorCode ?? "replay_failed", message ?? "The replay failed.");
          break;
        default:
          session.Cancel(message ?? "Replay cancelled.");
          break;
      }
    }
  }

  /// <summary>Per-frame housekeeping, driven from the pre-frame hook.</summary>
  internal static void Tick()
  {
    Infrastructure.Render.RenderFocusIllusion.Tick();

    ReplayRenderSession? session;
    lock (Gate)
      session = _session;

    if (session != null && session.IsFinished)
    {
      lock (Gate)
      {
        if (ReferenceEquals(_session, session))
        {
          _status = session.GetStatus();
          if (session.Settings.AudioPreviewOnly && session.PreviewStems != null)
            _previewStems = session.PreviewStems;
          _session = null;
          _operationId = null;
          _returnToEditorPending = true;
        }
      }
    }

    if (!_returnToEditorPending)
      return;

    // A completed render leaves the game sitting on the clear screen in play mode. Returning to
    // the editor matches what the user sees after any other replay and re-arms recording.
    try
    {
      if (scnEditor.instance != null && scnEditor.instance.playMode)
      {
        scnEditor.instance.SwitchToEditMode();
        return;
      }
      _returnToEditorPending = false;
    }
    catch (Exception exception)
    {
      _returnToEditorPending = false;
      Main.Instance?.LogException(nameof(ReplayRenderCoordinator), exception);
    }
  }

  // ── Internals ───────────────────────────────────────────────────────────────────────────────

  private static bool IsBusyLocked() => (_session != null && !_session.IsFinished) || _pendingSettings != null;

  private static bool MatchesOperationLocked(string operationId) =>
    _operationId == null || string.Equals(_operationId, operationId, StringComparison.Ordinal);

  private static RenderStatus GetStatusLocked() => _session?.GetStatus() ?? _status.Clone();

  private static void ClearPendingLocked()
  {
    _pendingSettings = null;
    _pendingRunId = null;
    _pendingMicrophone = null;

    if (!string.IsNullOrEmpty(_pendingMicrophoneFile))
    {
      TUFReplay.Infrastructure.Unity.ReplayMicrophonePlaybackFiles.Delete(_pendingMicrophoneFile);
      _pendingMicrophoneFile = null;
    }
  }

  private static RenderStatus Fail(string? operationId, string runId, string code, string message)
  {
    _status = new RenderStatus
    {
      OperationId = operationId,
      RunId = runId,
      State = RenderStates.Error,
      ErrorCode = code,
      Message = message,
    };
    RenderLog.Error("Render request rejected. code=" + code + ", message=" + message);
    return _status.Clone();
  }
}
