#nullable enable
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using FFmpeg.AutoGen;
using TUFReplay.Application.Replay;
using TUFReplayRenderer.Application.Render.Audio;
using TUFReplayRenderer.Domain.Render;
using TUFReplayRenderer.Infrastructure.FFmpeg;
using TUFReplayRenderer.Infrastructure.Render;
using UnityEngine;
using UnityEngine.Rendering;
using ThreadPriority = System.Threading.ThreadPriority;

namespace TUFReplayRenderer.Application.Render;

/// <summary>
/// Offline capture of one replay run into a video file.
///
/// The pipeline is ported from ADOFAIRenderer (MIT) and follows the same shape: a virtual clock
/// drives the simulation, frames are read back asynchronously into an ordered queue, and one
/// background thread owns the encoder and generates the matching audio per video frame. What is
/// different here is that the run is not autoplayed. The recorded run drives itself through
/// TUFReplay's hit-context playback, which is angle-gated rather than clock-gated and therefore
/// already deterministic under a fake clock, and nothing about the presentation is suppressed:
/// judgements, hit text, the error meter, miss indicators and the clear screen all render.
/// </summary>
public sealed class ReplayRenderSession : IFrameSink
{
  public const int MaxQueuedFrames = 64;
  public const int MaxRenderRetry = 64;

  /// <summary>Upper bound on how long the queued backlog may take to encode after capture stops.</summary>
  private const double DrainTimeoutSeconds = 300d;

  /// <summary>Frames to wait for a requested resolution change before settling for what we have.</summary>
  private const int ResolutionSettleFrames = 30;

  private static ReplayRenderSession? _current;

  /// <summary>The session currently capturing, or null. Read by the frame hooks each frame.</summary>
  public static ReplayRenderSession? Current => _current;

  /// <summary>True for the whole session, including setup. Gates the Screen.* patches.</summary>
  public static bool IsActive => _current is { _active: true };

  /// <summary>True only while frames are actually being captured. Gates the Time.* patches.</summary>
  public static bool IsCapturingActive => _current is { _capturing: true };

  private readonly object _statusGate = new object();
  private readonly SortedList<long, CapturedFrame> _imageQueue = new SortedList<long, CapturedFrame>();
  private readonly ConcurrentDictionary<long, OfflineAudioMixer.ReplayFrameTiming> _frameTimings =
    new ConcurrentDictionary<long, OfflineAudioMixer.ReplayFrameTiming>();
  private readonly AutoResetEvent _frameAvailable = new AutoResetEvent(false);

  private readonly ReplayRenderSettings _settings;
  private readonly string _operationId;
  private readonly string _runId;

  private RenderStatus _status;
  private bool _active;
  private bool _capturing;
  private volatile bool _cancelled;
  private volatile bool _encoderThreadFailed;
  private bool _finished;

  private FFmpegNativeEncoder? _encoder;
  private RenderFrameReader? _frameReader;
  private OfflineAudioMixer? _mixer;
  private AudioSyncCalculator? _syncCalculator;
  private MicrophoneRenderSource? _microphone;
  private Thread? _encoderThread;

  // Virtual clock bases, snapshotted the instant before capture starts so patched time is
  // continuous with the pre-capture values.
  public double TimeBase;
  public double UnscaledTimeBase;
  public double DspTimeBase;
  public double RealtimeBase;
  public int FrameCountBase;
  public long FrameCount;

  private long _frameCountForVideo;
  private long _lastVideoFrameIndex = -1;
  private long _lastRequestedVideoFrameIndex = -1;
  private long _passCount;
  private long _framesSent;
  private long _framesCaptured;
  private bool _endRequested;
  private volatile bool _endSuccessful;
  private bool _renderCheck = true;
  private int _skipCount;
  private int _readbackFailures;
  private int _throttleCount;
  private int _resolutionMismatchFrames;

  private long _stopAtFrame = -1;
  private bool _stopSuccess;
  private bool _draining;
  private bool _drainSuccess;
  private double _drainStartedAt;
  private bool _environmentRestored;
  private string? _pendingErrorCode;
  private string? _pendingErrorMessage;

  // Restored on every exit path.
  private bool _cameraForced;
  private bool _resolutionRequested;
  private int _resolutionRequestFrame;
  private int _originalWidth;
  private int _originalHeight;
  private bool _originalFullScreen;
  private bool _restoreCustomFrameRate;
  private float _previousCustomFrameRate;
  private RenderPostFrameHook? _postFrameHook;
  private bool _previousRunInBackground;
  private AudioClip? _suppressedSongClip;
  private float _previousListenerVolume = 1f;
  private bool _listenerMuted;
  private float _suppressedSongTime;
  private bool _songSuppressed;

  private string? _microphonePlaybackFile;
  private bool _hasMicrophoneTrack;

  // Audio-only preview state.
  private readonly List<float> _previewMusic = new List<float>();
  private readonly List<float> _previewHitsounds = new List<float>();
  private readonly List<float> _previewMicrophone = new List<float>();
  private bool _paceOverridden;
  private int _previousVSync;
  private int _previousTargetFrameRate;

  /// <summary>Per-source WAV stems produced by an audio-only preview, available once completed.</summary>
  public sealed class PreviewStemsResult
  {
    public byte[] Music = Array.Empty<byte>();
    public byte[] Hitsounds = Array.Empty<byte>();
    public byte[]? Microphone;
    public int SampleRate;
    public int Channels;
  }

  public PreviewStemsResult? PreviewStems { get; private set; }

  // Reloads the level song through the game's own loader with streaming off, so the conductor's
  // clip has real samples for the mixer — the same clip state ADOFAIRenderer captures under.
  private readonly Audio.RenderAudioPreparer _audioPreparer = new Audio.RenderAudioPreparer();

  // Retargets Screen Space - Overlay canvases (fail percentage, clear-screen judgements) into the
  // capture camera so they appear in the video.
  private readonly RenderOverlayCanvasCapture _overlayCapture = new RenderOverlayCanvasCapture();
  private double _environmentArmedAt;

  public ReplayRenderSettings Settings => _settings;
  public string OperationId => _operationId;
  public string RunId => _runId;

  /// <summary>Offline mixer for this session, or null when audio rendering is off or not yet armed.</summary>
  public OfflineAudioMixer? Mixer => _mixer;

  /// <summary>
  /// The virtual DSP clock the conductor is pinned to: continuous with the real clock at capture
  /// start, then advancing exactly one frame step per rendered frame.
  /// </summary>
  public double VirtualDspTime => DspTimeBase + FrameCount / _settings.RenderFps;

  /// <summary>
  /// Virtual replacement for <c>Time.realtimeSinceStartup</c>: continuous with the real clock at
  /// capture start, then advancing exactly one frame step per rendered frame. DOTween derives its
  /// unscaled delta from the real clock, so without this every tween — including the delayed call
  /// that reveals the fail screen's "% complete" — runs at wall-clock speed inside the render.
  /// </summary>
  public double VirtualRealtime => RealtimeBase + FrameCount / _settings.RenderFps;

  /// <summary>Seconds of virtual time per rendered frame.</summary>
  public double VirtualFrameStep => 1d / _settings.RenderFps;

  public ReplayRenderSession(string operationId, string runId, ReplayRenderSettings settings)
  {
    _operationId = operationId;
    _runId = runId;
    _settings = settings;
    _status = new RenderStatus
    {
      OperationId = operationId,
      RunId = runId,
      State = RenderStates.Preparing,
      Message = "Preparing render.",
      Width = settings.Width,
      Height = settings.Height,
      VideoFps = settings.EffectiveVideoFps,
    };
  }

  public RenderStatus GetStatus()
  {
    lock (_statusGate)
    {
      _status.FramesEncoded = Interlocked.Read(ref _framesSent);
      _status.FramesCaptured = Interlocked.Read(ref _framesCaptured);
      _status.EncodedSeconds =
        _settings.EffectiveVideoFps > 0d ? _status.FramesEncoded / _settings.EffectiveVideoFps : 0d;
      return _status.Clone();
    }
  }

  public bool IsFinished => _finished;

  bool IFrameSink.IsCancelled => _cancelled;

  // ── Lifecycle ───────────────────────────────────────────────────────────────────────────────

  /// <summary>
  /// Installs the render environment immediately before the editor enters play mode.
  /// </summary>
  public void BeginEnvironment()
  {
    _current = this;
    _active = true;

    _previousRunInBackground = UnityEngine.Application.runInBackground;
    UnityEngine.Application.runInBackground = true;

    // The single most important line: Unity now advances its clock by exactly one frame step per
    // Update regardless of how long that frame actually took.
    Time.captureDeltaTime = (float)(1d / _settings.RenderFps);

    _environmentArmedAt = Time.realtimeSinceStartupAsDouble;
    if (_settings.RenderAudio)
      _audioPreparer.Begin();
    if (!_settings.AudioPreviewOnly)
      TryPrepareResolution();

    SetStatus(RenderStates.OpeningLevel, "Entering play mode for capture.");
    RenderLog.Info(
      "Render environment armed. run="
        + _runId
        + ", resolution="
        + _settings.Width
        + "x"
        + _settings.Height
        + ", renderFps="
        + _settings.RenderFps
        + ", videoFps="
        + _settings.EffectiveVideoFps
    );
  }

  /// <summary>Hands the run's prepared microphone recording to the renderer.</summary>
  public void AttachMicrophone(MicrophoneRenderSource? microphone, string? playbackFilePath)
  {
    _microphone = microphone;
    _microphonePlaybackFile = playbackFilePath;
    lock (_statusGate)
      _status.MicrophoneIncluded = microphone != null;
  }

  /// <summary>
  /// Called before every game script each frame. Advances the virtual clock, or freezes it when the
  /// previous frame was never presented.
  /// </summary>
  public void OnPreFrame()
  {
    if (_finished)
      return;

    // Checked before _active, because draining deliberately hands the game environment back first.
    if (_draining)
    {
      TickDrain();
      return;
    }

    if (!_active)
      return;

    if (_encoderThreadFailed)
    {
      Fail("encoder_thread_failed", "The video encoder thread stopped unexpectedly.");
      return;
    }

    if (!_capturing)
    {
      TryStartCapture();
      return;
    }

    if (_settings.AudioPreviewOnly)
    {
      _mixer?.UpdateSnapshotFromMainThread();
      // Mix BEFORE advancing the counter: the conductor's last update ran with the current
      // FrameCount, so media time and the replay-clock anchor describe the same instant.
      PreviewTick();
      FrameCount++;
      return;
    }

    if (_renderCheck)
    {
      if (_skipCount < MaxRenderRetry)
      {
        // The camera never presented last frame. Freeze simulation time rather than advancing it,
        // otherwise the frame is silently dropped and audio desyncs from that point on.
        _skipCount++;
        Time.timeScale = 0f;
        return;
      }

      RenderLog.Error(
        "Frame " + FrameCount + " was never presented after " + MaxRenderRetry + " retries. Emitting a black frame."
      );
      EmitBlackFrameForCurrentIndex();
      _renderCheck = false;
    }

    _mixer?.UpdateSnapshotFromMainThread();
    _overlayCapture.Tick();

    // A retry set timeScale to zero; failing to restore it freezes every Animator, particle system
    // and scaled coroutine for the rest of the render.
    if (Time.timeScale == 0f)
      Time.timeScale = 1f;

    _skipCount = 0;
    _renderCheck = true;
    FrameCount++;

    if (_stopAtFrame >= 0 && FrameCount >= _stopAtFrame)
      BeginDrain(_stopSuccess);
  }

  /// <summary>
  /// Waits for the encoder thread to consume the frames still queued when capture stopped.
  ///
  /// This is polled across frames rather than joined inline: at 4K with a software encoder the
  /// backlog can take a minute to flush, and blocking the main thread for that long would freeze
  /// the game on the clear screen. Normal play resumes immediately while the file finishes.
  /// </summary>
  private void TickDrain()
  {
    if (_encoderThread is { IsAlive: true })
    {
      if (Time.realtimeSinceStartupAsDouble - _drainStartedAt < DrainTimeoutSeconds)
        return;
      RenderLog.Error("The encoder thread did not finish within " + DrainTimeoutSeconds + "s. Continuing cleanup.");
    }

    Finalize(_drainSuccess);
  }

  /// <summary>
  /// Stops capture, restores the game, and lets the encoder thread drain in the background.
  /// </summary>
  private void BeginDrain(bool success)
  {
    if (_finished || _draining)
      return;

    _draining = true;
    _drainSuccess = success;
    _drainStartedAt = Time.realtimeSinceStartupAsDouble;
    _capturing = false;
    _endRequested = true;
    if (!success)
      _cancelled = true;
    _frameAvailable.Set();

    SetStatus(RenderStates.Finalizing, success ? "Finishing the video file." : "Cleaning up the cancelled render.");

    // Hand the game back now. The queued frames and the mixer state the encoder thread still needs
    // are all owned by this session, not by the scene.
    RestoreEnvironment();
  }

  /// <summary>Called after every game script each frame with the camera's RenderTexture.</summary>
  public void OnPostFrame(RenderTexture? texture)
  {
    if (!_capturing || texture == null || _finished || _settings.AudioPreviewOnly)
      return;

    if (texture.width != _settings.Width || texture.height != _settings.Height)
    {
      // The encoder's RGBA path derives its stride from the configured size, so a mismatched
      // texture would shear or overrun. Leave _renderCheck set: OnPreFrame then holds simulation
      // time until scrCamera has rebuilt its RenderTexture at the faked Screen size.
      if (_resolutionMismatchFrames++ == 0)
        RenderLog.Warn(
          "Waiting for the camera RenderTexture to match the output size. texture="
            + texture.width
            + "x"
            + texture.height
            + ", expected="
            + _settings.Width
            + "x"
            + _settings.Height
        );
      return;
    }

    _renderCheck = false;

    // Backpressure. Because game time is virtual, blocking here costs wall-clock time only: the
    // output is identical whether or not the producer had to wait.
    bool throttling = false;
    while (_capturing && _active)
    {
      if (_encoderThreadFailed || _cancelled)
        break;

      int count;
      lock (_imageQueue)
        count = _imageQueue.Count;
      if (count <= MaxQueuedFrames)
        break;

      if (!throttling)
      {
        throttling = true;
        _throttleCount++;
      }

      AsyncGPUReadback.WaitAllRequests();
    }

    long currentFrame = _frameCountForVideo++;
    double effectiveVideoFps = _settings.EffectiveVideoFps;
    long videoFrameIndex = (long)(currentFrame * effectiveVideoFps / _settings.RenderFps);

    if (videoFrameIndex <= _lastVideoFrameIndex)
      return;

    _lastVideoFrameIndex = videoFrameIndex;
    _lastRequestedVideoFrameIndex = videoFrameIndex;
    _frameTimings[videoFrameIndex] = CaptureReplayTiming();
    _frameReader?.RequestFrameCapture(texture, videoFrameIndex);
    Interlocked.Increment(ref _framesCaptured);
  }

  /// <summary>
  /// Reads the replay clock on the main thread and stores it with the frame, so the encoder thread
  /// can place microphone samples without ever touching a Unity object.
  /// </summary>
  /// <summary>
  /// Reads the replay clock for the frame's audio.
  ///
  /// The anchor must correspond to the START of the audio frame being mixed. Where the clock is
  /// read relative to the conductor's update decides the correction: the render capture path reads
  /// it post-frame (the conductor has advanced past the frame, so one frame is subtracted), while
  /// the preview mixes pre-frame against the conductor's previous update (already the frame start,
  /// no correction).
  /// </summary>
  private OfflineAudioMixer.ReplayFrameTiming CaptureReplayTiming(bool readAtFrameEnd = true)
  {
    if (_microphone == null)
      return default;

    if (!ReplaySessionService.TryGetRenderTiming(out long replayTimeUs, out double rate, out long? wonTimeUs))
      return default;

    if (readAtFrameEnd)
    {
      double safeRate = rate > 0d && !double.IsNaN(rate) && !double.IsInfinity(rate) ? rate : 1d;
      long oneFrameUs = (long)(1_000_000d / _settings.EffectiveVideoFps * safeRate);
      replayTimeUs -= oneFrameUs;
    }
    return new OfflineAudioMixer.ReplayFrameTiming(true, replayTimeUs, rate, wonTimeUs);
  }

  /// <summary>
  /// Requests that capture stop. When the terminal state keeps the game in play mode (a clear or
  /// fail screen) the configured trailing seconds are captured first.
  /// </summary>
  public void RequestStop(bool success, bool allowTrailing, string? message = null)
  {
    if (_finished || _draining || _stopAtFrame >= 0)
      return;

    if (!_capturing)
    {
      BeginDrain(success);
      return;
    }

    _stopSuccess = success;
    long trailingFrames =
      allowTrailing && success ? (long)Math.Ceiling(_settings.TrailingSeconds * _settings.RenderFps) : 0L;
    if (trailingFrames <= 0)
    {
      BeginDrain(success);
      return;
    }

    _stopAtFrame = FrameCount + trailingFrames;
    SetStatus(RenderStates.Capturing, message ?? "Capturing the final frames.");
  }

  public void Cancel(string reason)
  {
    if (_finished)
      return;
    _pendingErrorCode = null;
    _pendingErrorMessage = reason;
    _cancelled = true;
    if (_draining)
      return;
    BeginDrain(false);
  }

  public void Fail(string errorCode, string message)
  {
    if (_finished)
      return;
    _pendingErrorCode = errorCode;
    _pendingErrorMessage = message;
    _cancelled = true;
    if (_draining)
      return;
    BeginDrain(false);
  }

  /// <summary>Last-ditch teardown when the process is exiting mid-render.</summary>
  public void AbortForProcessExit()
  {
    if (_finished)
      return;
    _cancelled = true;
    _endRequested = true;
    _frameAvailable.Set();
    if (_encoderThread is { IsAlive: true })
      _encoderThread.Join(200);
    DisposeEncoder();
    RestoreEnvironment();
    Features.Render.RenderDoTweenClock.Uninstall();

    if (_paceOverridden)
    {
      try
      {
        QualitySettings.vSyncCount = _previousVSync;
        UnityEngine.Application.targetFrameRate = _previousTargetFrameRate;
      }
      catch (Exception exception)
      {
        RenderLog.Warn("Could not restore the frame pacing: " + exception.Message);
      }
      _paceOverridden = false;
    }
    _finished = true;
    _draining = false;
    _current = null;
  }

  /// <summary>
  /// True when play mode may begin: the decoded song swap and the resolution change have landed,
  /// or the bounded preparation window has elapsed. The replay coordinator defers
  /// <c>scnEditor.Play()</c> on this, so capture starts on the very first play frame — the
  /// countdown is captured at render pacing and the conductor schedules music and hitsounds while
  /// the mixer is listening.
  /// </summary>
  public bool ReadyForPlayMode
  {
    get
    {
      if (_finished)
        return true;
      if (Time.realtimeSinceStartupAsDouble - _environmentArmedAt > 15d)
      {
        RenderLog.Warn("Render preparation timed out; entering play mode with what is ready.");
        return true;
      }
      if (_settings.RenderAudio && !_audioPreparer.IsDone)
        return false;
      if (
        _resolutionRequested
        && _settings.Width != Screen.width
        && Time.frameCount - _resolutionRequestFrame < ResolutionSettleFrames
      )
        return false;
      return true;
    }
  }

  // ── Capture start ───────────────────────────────────────────────────────────────────────────

  private void TryStartCapture()
  {
    if (_capturing || _finished)
      return;

    scrCamera camera = scrCamera.instance;
    scrConductor conductor = scrConductor.instance;
    if (camera == null || conductor == null || conductor.song == null)
      return;
    if (scrController.instance == null)
      return;

    // Wait for play mode: capturing earlier records the editor UI and the scene wipe, and it would
    // snapshot the DSP base before the run's own timeline exists. ADOFAIRenderer likewise starts
    // capture only once the game scene is running.
    if (scnEditor.instance != null && !scnEditor.instance.playMode)
      return;

    // ADOFAIRenderer fakes Screen.width/height so the camera allocates its RenderTexture at the
    // output size. Those getters are unpatchable here, so the resolution is requested for real and
    // the actual result is adopted below — the output always matches what was captured.
    if (!_settings.AudioPreviewOnly && !TryPrepareResolution())
      return;

    if (_settings.AudioPreviewOnly)
    {
      StartAudioPreviewCapture(conductor);
      return;
    }

    // Force the render-to-texture camera path and hook the presented texture.
    if (!_cameraForced)
    {
      // The frame-rate cap swaps the quad's texture for a separate RenderTexture that is only
      // blitted from camRT every 1/frameRate seconds. Capturing that would duplicate frames, so the
      // cap is turned off for the render and restored afterwards.
      if (camera.enableCustomFPS)
      {
        _restoreCustomFrameRate = true;
        _previousCustomFrameRate = camera.frameRate;
        camera.SetCustomFrameRate(false);
      }

      camera.forceRTCam = true;
      // SetupRTCam only points the cameras at camRT; scrCamera.Update allocates it at the current
      // (faked) Screen size on the next frame, which is why OnPostFrame tolerates one mismatch.
      camera.SetupRTCam(true);
      _cameraForced = true;
    }
    if (_postFrameHook == null)
    {
      _postFrameHook = camera.gameObject.GetComponent<RenderPostFrameHook>();
      if (_postFrameHook == null)
        _postFrameHook = camera.gameObject.AddComponent<RenderPostFrameHook>();
    }

    // ADOFAIRenderer reads BGM samples from conductor.song.clip; in the editor that is null (the
    // song plays through a non-clip AudioResource), so the preparer decoded a private copy of the
    // level's song file instead. Play-mode entry was deferred on that decode, so it is ready here.
    AudioClip? songClip = _audioPreparer.PreparedClip != null ? _audioPreparer.PreparedClip : conductor.song.clip;
    if (_settings.RenderAudio)
    {
      if (songClip == null)
        RenderLog.Warn("The level song clip is unavailable; rendering without background music.");
      else if (songClip.loadType == AudioClipLoadType.Streaming)
        RenderLog.Warn("The song clip is still streaming (" + songClip.name + "); background music may be silent.");
      else
        RenderLog.Info(
          "BGM source: " + songClip.name + " (" + songClip.loadType + ", " + songClip.length.ToString("F1") + "s)"
        );
    }

    // camRT is allocated at Screen size, so this is the size frames will actually arrive in.
    // Adopting it before the encoder opens keeps the encoder's stride and the readback in step.
    if (_settings.Width != Screen.width || _settings.Height != Screen.height)
    {
      RenderLog.Info(
        "Capturing at the game's actual resolution "
          + Screen.width
          + "x"
          + Screen.height
          + " (requested "
          + _settings.Width
          + "x"
          + _settings.Height
          + ")."
      );
      _settings.Width = Screen.width - (Screen.width % 2);
      _settings.Height = Screen.height - (Screen.height % 2);
      lock (_statusGate)
      {
        _status.Width = _settings.Width;
        _status.Height = _settings.Height;
      }
    }

    TimeBase = Time.timeAsDouble;
    UnscaledTimeBase = Time.unscaledTimeAsDouble;
    DspTimeBase = AudioSettings.dspTime;
    RealtimeBase = Time.realtimeSinceStartupAsDouble;
    FrameCountBase = Time.frameCount;
    FrameCount = 0;

    try
    {
      StartEncoderThread();
      // The capture path is built from the encoder's answer: when it opened with NV12/YUV420P the
      // reader converts on the GPU; otherwise it reads raw and lets swscale convert.
      _frameReader = new RenderFrameReader(this, _encoder?.SelectedPixelFormat ?? AVPixelFormat.AV_PIX_FMT_YUV420P);
    }
    catch (Exception exception)
    {
      RenderLog.Error("Encoder initialization failed: " + exception);
      Fail("encoder_init_failed", "The video encoder could not be initialized: " + exception.Message);
      return;
    }

    _capturing = true;
    _cancelled = false;

    if (_settings.RenderAudio)
    {
      AudioSpectrumGenerator.Initialize(songClip);

      _mixer = new OfflineAudioMixer(_settings.AudioChannels, _settings.AudioSampleRate);
      _mixer.InitializeBGM(songClip);
      _mixer.SetMicrophoneSource(_microphone);
      _mixer.SetLevels(
        _settings.MusicVolumePercent / 100f,
        _settings.HitsoundVolumePercent / 100f,
        _settings.MicrophoneVolumePercent / 100f
      );
      _syncCalculator = new AudioSyncCalculator(_settings.AudioSampleRate, _settings.EffectiveVideoFps);

      // Both consumers copied the samples they need, so the decoded copy can be freed now.
      _audioPreparer.ReleasePreparedClip();

      // Silence real playback: the DSP clock is virtual now, so Unity's own audio runs at
      // wall-clock speed against a fake game clock and would desync instantly. The listener mute
      // is the whole mechanism — in the editor the song plays through a non-clip AudioResource,
      // so ADOFAIRenderer's clip-nulling has nothing to null here.
      _previousListenerVolume = AudioListener.volume;
      _listenerMuted = true;
      AudioListener.volume = 0f;

      if (conductor.song.clip != null)
      {
        _suppressedSongClip = conductor.song.clip;
        _suppressedSongTime = conductor.song.time;
        conductor.song.clip = null;
        conductor.song.time = -1000f;
        _songSuppressed = true;
      }
    }

    // Scoped to the capture: DOTween animates on wall time otherwise, and patching it outside a
    // render once cost the game its menu-to-editor transition.
    Features.Render.RenderDoTweenClock.Install();

    SetStatus(RenderStates.Capturing, "Capturing replay.");
    RenderLog.Info("Capture started. dspTimeBase=" + DspTimeBase.ToString("F4"));
  }

  /// <summary>
  /// Starts an audio-only preview capture: the full render audio pipeline — virtual clock, offline
  /// mixer, decoded song, hitsound registration, microphone — with no encoder and no video. The
  /// simulation fast-forwards uncapped to the preview window, mixes it on the main thread, and the
  /// result is handed back as a WAV for the UI to play.
  /// </summary>
  private void StartAudioPreviewCapture(scrConductor conductor)
  {
    AudioClip? songClip = _audioPreparer.PreparedClip != null ? _audioPreparer.PreparedClip : conductor.song.clip;

    TimeBase = Time.timeAsDouble;
    UnscaledTimeBase = Time.unscaledTimeAsDouble;
    DspTimeBase = AudioSettings.dspTime;
    RealtimeBase = Time.realtimeSinceStartupAsDouble;
    FrameCountBase = Time.frameCount;
    FrameCount = 0;

    _mixer = new OfflineAudioMixer(_settings.AudioChannels, _settings.AudioSampleRate);
    _mixer.InitializeBGM(songClip);
    _mixer.SetMicrophoneSource(_microphone);
    // The preview returns raw stems and the browser applies the volume sliders live through its
    // own gain nodes, so the mixer's trims stay at unity here.
    _mixer.SetLevels(1f, 1f, 1f);
    _syncCalculator = new AudioSyncCalculator(_settings.AudioSampleRate, _settings.EffectiveVideoFps);
    _audioPreparer.ReleasePreparedClip();
    _previewMusic.Clear();
    _previewHitsounds.Clear();
    _previewMicrophone.Clear();
    PreviewStems = null;

    _previousListenerVolume = AudioListener.volume;
    _listenerMuted = true;
    AudioListener.volume = 0f;

    if (conductor.song.clip != null)
    {
      _suppressedSongClip = conductor.song.clip;
      _suppressedSongTime = conductor.song.time;
      conductor.song.clip = null;
      conductor.song.time = -1000f;
      _songSuppressed = true;
    }

    // No readback and no encoder means nothing paces the simulation; uncapping the frame rate
    // makes the fast-forward to the window take seconds instead of the window's own real time.
    _previousVSync = QualitySettings.vSyncCount;
    _previousTargetFrameRate = UnityEngine.Application.targetFrameRate;
    QualitySettings.vSyncCount = 0;
    UnityEngine.Application.targetFrameRate = -1;
    _paceOverridden = true;

    _capturing = true;
    _cancelled = false;
    SetStatus(
      RenderStates.Capturing,
      "Fast-forwarding to " + _settings.PreviewStartSeconds.ToString("F0") + "s for the audio preview."
    );
    RenderLog.Info(
      "Audio preview capture started. window="
        + _settings.PreviewStartSeconds.ToString("F1")
        + "s +"
        + _settings.PreviewDurationSeconds.ToString("F0")
        + "s"
    );
  }

  /// <summary>Mixes preview audio for the current frame once the window opens.</summary>
  private void PreviewTick()
  {
    OfflineAudioMixer? mixer = _mixer;
    AudioSyncCalculator? sync = _syncCalculator;
    if (mixer == null || sync == null)
      return;

    double frameTime = FrameCount / _settings.RenderFps;
    double windowStart = _settings.PreviewStartSeconds;
    double windowEnd = windowStart + _settings.PreviewDurationSeconds;

    if (frameTime < windowStart)
      return;

    if (frameTime >= windowEnd)
    {
      FinishAudioPreview();
      return;
    }

    if (_previewMusic.Count == 0)
      SetStatus(RenderStates.Capturing, "Capturing the audio preview window.");

    int samplesNeeded = sync.GetNextFrameSamples();
    int channels = _settings.AudioChannels;
    int sampleCount = samplesNeeded * channels;
    bool microphone = _microphone != null;

    float[] mixBuffer = ArrayPool<float>.Shared.Rent(sampleCount);
    float[] musicBuffer = ArrayPool<float>.Shared.Rent(sampleCount);
    float[] hitsoundBuffer = ArrayPool<float>.Shared.Rent(sampleCount);
    float[]? microphoneBuffer = microphone ? ArrayPool<float>.Shared.Rent(sampleCount) : null;
    try
    {
      mixer.GenerateAudioStems(
        mixBuffer,
        musicBuffer,
        hitsoundBuffer,
        microphoneBuffer,
        samplesNeeded,
        frameTime,
        DspTimeBase,
        CaptureReplayTiming(readAtFrameEnd: false)
      );
      for (int i = 0; i < sampleCount; i++)
      {
        _previewMusic.Add(musicBuffer[i]);
        _previewHitsounds.Add(hitsoundBuffer[i]);
        if (microphoneBuffer != null)
          _previewMicrophone.Add(microphoneBuffer[i]);
      }
    }
    finally
    {
      ArrayPool<float>.Shared.Return(mixBuffer);
      ArrayPool<float>.Shared.Return(musicBuffer);
      ArrayPool<float>.Shared.Return(hitsoundBuffer);
      if (microphoneBuffer != null)
        ArrayPool<float>.Shared.Return(microphoneBuffer);
    }
  }

  private void FinishAudioPreview()
  {
    try
    {
      PreviewStems = new PreviewStemsResult
      {
        Music = EncodeWavPcm16(_previewMusic, _settings.AudioChannels, _settings.AudioSampleRate),
        Hitsounds = EncodeWavPcm16(_previewHitsounds, _settings.AudioChannels, _settings.AudioSampleRate),
        Microphone =
          _previewMicrophone.Count > 0
            ? EncodeWavPcm16(_previewMicrophone, _settings.AudioChannels, _settings.AudioSampleRate)
            : null,
        SampleRate = _settings.AudioSampleRate,
        Channels = _settings.AudioChannels,
      };
      RenderLog.Info(
        "Audio preview stems ready. frames="
          + _previewMusic.Count / Math.Max(1, _settings.AudioChannels)
          + ", microphone="
          + (PreviewStems.Microphone != null)
      );
    }
    catch (Exception exception)
    {
      RenderLog.Error("Audio preview encoding failed: " + exception.Message);
      PreviewStems = null;
    }
    _previewMusic.Clear();
    _previewHitsounds.Clear();
    _previewMicrophone.Clear();
    BeginDrain(true);
  }

  private static byte[] EncodeWavPcm16(List<float> samples, int channels, int sampleRate)
  {
    int dataBytes = samples.Count * 2;
    byte[] wav = new byte[44 + dataBytes];

    void WriteAscii(int offset, string text)
    {
      for (int i = 0; i < text.Length; i++)
        wav[offset + i] = (byte)text[i];
    }
    void WriteInt(int offset, int value)
    {
      wav[offset] = (byte)value;
      wav[offset + 1] = (byte)(value >> 8);
      wav[offset + 2] = (byte)(value >> 16);
      wav[offset + 3] = (byte)(value >> 24);
    }
    void WriteShort(int offset, short value)
    {
      wav[offset] = (byte)value;
      wav[offset + 1] = (byte)(value >> 8);
    }

    WriteAscii(0, "RIFF");
    WriteInt(4, 36 + dataBytes);
    WriteAscii(8, "WAVE");
    WriteAscii(12, "fmt ");
    WriteInt(16, 16);
    WriteShort(20, 1);
    WriteShort(22, (short)channels);
    WriteInt(24, sampleRate);
    WriteInt(28, sampleRate * channels * 2);
    WriteShort(32, (short)(channels * 2));
    WriteShort(34, 16);
    WriteAscii(36, "data");
    WriteInt(40, dataBytes);

    for (int i = 0; i < samples.Count; i++)
    {
      float value = samples[i];
      if (value > 1f)
        value = 1f;
      else if (value < -1f)
        value = -1f;
      short pcm = (short)(value * 32767f);
      wav[44 + i * 2] = (byte)pcm;
      wav[44 + i * 2 + 1] = (byte)(pcm >> 8);
    }
    return wav;
  }

  /// <summary>
  /// Asks the game for the requested output resolution and waits for it to take effect.
  ///
  /// Unity applies a resolution change asynchronously, and it may not honour the request at all
  /// (an unsupported size, or a fullscreen display mode). Rather than fail, this gives up after a
  /// bounded wait and lets the caller capture at whatever the window actually is.
  /// </summary>
  private bool TryPrepareResolution()
  {
    if (_settings.Width == Screen.width && _settings.Height == Screen.height)
      return true;

    if (!_resolutionRequested)
    {
      _resolutionRequested = true;
      _originalWidth = Screen.width;
      _originalHeight = Screen.height;
      _originalFullScreen = Screen.fullScreen;
      _resolutionRequestFrame = Time.frameCount;
      RenderLog.Info("Requesting render resolution " + _settings.Width + "x" + _settings.Height + " for capture.");
      try
      {
        Screen.SetResolution(_settings.Width, _settings.Height, _originalFullScreen);
      }
      catch (Exception exception)
      {
        RenderLog.Warn("The resolution change was rejected: " + exception.Message);
        return true;
      }
      return false;
    }

    // Give the change a bounded number of frames to land before settling for the current size.
    return Time.frameCount - _resolutionRequestFrame >= ResolutionSettleFrames;
  }

  private void EmitBlackFrameForCurrentIndex()
  {
    long currentFrame = _frameCountForVideo++;
    long videoFrameIndex = (long)(currentFrame * _settings.EffectiveVideoFps / _settings.RenderFps);
    if (videoFrameIndex <= _lastVideoFrameIndex)
      return;

    _lastVideoFrameIndex = videoFrameIndex;
    _lastRequestedVideoFrameIndex = videoFrameIndex;
    _frameTimings[videoFrameIndex] = CaptureReplayTiming();

    int byteSize = _settings.Width * _settings.Height * 4;
    byte[] blackFrame = ArrayPool<byte>.Shared.Rent(byteSize);
    Array.Clear(blackFrame, 0, byteSize);
    PushFrame(videoFrameIndex, new CapturedFrame { ManagedBuffer = blackFrame, Length = byteSize });
    Interlocked.Increment(ref _framesCaptured);
  }

  // ── Frame sink ──────────────────────────────────────────────────────────────────────────────

  public void PushFrame(long frameIndex, CapturedFrame frame)
  {
    lock (_imageQueue)
      _imageQueue[frameIndex] = frame;
    _frameAvailable.Set();
  }

  public void OnReadbackFailed(long frameIndex)
  {
    _readbackFailures++;
    RenderLog.Warn("GPU readback failed for frame " + frameIndex + "; substituting a black frame.");
  }

  // ── Encoder thread ──────────────────────────────────────────────────────────────────────────

  private void StartEncoderThread()
  {
    if (_encoderThread is { IsAlive: true })
      throw new InvalidOperationException("A previous render thread is still running.");

    _encoderThreadFailed = false;
    lock (_imageQueue)
      _imageQueue.Clear();
    _frameTimings.Clear();
    _passCount = 0;
    _framesSent = 0;
    _framesCaptured = 0;
    _frameCountForVideo = 0;
    _lastVideoFrameIndex = -1;
    _lastRequestedVideoFrameIndex = -1;
    _renderCheck = true;
    _skipCount = 0;
    _endRequested = false;
    _endSuccessful = false;
    _frameAvailable.Reset();

    string extension = GetOutputExtension(_settings.Codec);
    AVCodecID audioCodecId = GetAudioCodecId(extension);

    // Opus only supports 48 kHz, and the container choice decides the audio codec, so force the
    // rate before anything is allocated.
    if (_settings.RenderAudio && audioCodecId == AVCodecID.AV_CODEC_ID_OPUS)
      _settings.AudioSampleRate = 48000;

    _settings.OutputPath = EnsureOutputPath(_settings.OutputPath, extension);

    // Unity's readback returns bytes in the RenderTexture's native channel order. On Metal the
    // default LDR format is BGRA, and feeding that through an RGBA-configured swscale swaps red and
    // blue across the whole video. Only the raw fallback path cares; the GPU shader samples the
    // texture and is layout-agnostic.
    AVPixelFormat rawSourceFormat = AVPixelFormat.AV_PIX_FMT_RGBA;
    try
    {
      UnityEngine.Experimental.Rendering.GraphicsFormat defaultFormat = SystemInfo.GetGraphicsFormat(
        UnityEngine.Experimental.Rendering.DefaultFormat.LDR
      );
      if (defaultFormat.ToString().StartsWith("B8G8R8A8", StringComparison.Ordinal))
        rawSourceFormat = AVPixelFormat.AV_PIX_FMT_BGRA;
      RenderLog.Info("Raw capture layout: " + defaultFormat + " -> " + rawSourceFormat);
    }
    catch (Exception exception)
    {
      RenderLog.Warn("Could not detect the framebuffer layout; assuming RGBA. " + exception.Message);
    }

    FFmpegNativeEncoder.EncoderSettings encoderSettings = new FFmpegNativeEncoder.EncoderSettings
    {
      RawSourceFormat = rawSourceFormat,
      Width = _settings.Width,
      Height = _settings.Height,
      FPS = _settings.EffectiveVideoFps,
      Codec = _settings.Codec,
      Mode = _settings.RateControlMode,
      QualityValue = _settings.QualityValue,
      TargetBitrate = _settings.TargetBitrateKbps,
      MaxBitrate = _settings.MaxBitrateKbps,
      GopSize = _settings.KeyframeIntervalSeconds,
      ForceSoftware = _settings.ForceSoftwareEncoder,
      Audio = _settings.RenderAudio ? AudioTrack("Mix", audioCodecId, isDefault: true) : null,
      ExtraAudioTracks = _settings.RenderAudio ? BuildStemTracks(audioCodecId) : null!,
    };

    FFmpegNativeEncoder encoder = new FFmpegNativeEncoder(encoderSettings);
    _hasMicrophoneTrack = _settings.RenderAudio && _settings.IncludeMicrophone && _microphone != null;
    try
    {
      encoder.Start(_settings.OutputPath);
    }
    catch
    {
      encoder.Dispose();
      throw;
    }

    _encoder = encoder;
    lock (_statusGate)
    {
      _status.OutputPath = null;
      _status.EncoderName = encoder.SelectedEncoderName;
    }

    _encoderThread = new Thread(() => EncoderLoop(encoder))
    {
      IsBackground = true,
      Priority = ThreadPriority.AboveNormal,
      Name = "TUFReplay Render Encoder",
    };
    _encoderThread.Start();
  }

  private FFmpegNativeEncoder.AudioSettings AudioTrack(string title, AVCodecID audioCodecId, bool isDefault = false) =>
    new FFmpegNativeEncoder.AudioSettings
    {
      Title = title,
      IsDefaultTrack = isDefault,
      SampleRate = _settings.AudioSampleRate,
      Channels = _settings.AudioChannels,
      InputSampleFormat = AVSampleFormat.AV_SAMPLE_FMT_FLT,
      AudioCodecId = audioCodecId,
      Bitrate = _settings.AudioBitrate,
    };

  /// <summary>
  /// Per-source stem tracks written after the default mix: Music, Hitsounds, and — when the run
  /// has a recording — Microphone. Players play the mix; editors get the stems to rebalance.
  /// </summary>
  private FFmpegNativeEncoder.AudioSettings[] BuildStemTracks(AVCodecID audioCodecId)
  {
    bool microphone = _settings.IncludeMicrophone && _microphone != null;
    FFmpegNativeEncoder.AudioSettings[] stems = new FFmpegNativeEncoder.AudioSettings[microphone ? 3 : 2];
    stems[0] = AudioTrack("Music", audioCodecId);
    stems[1] = AudioTrack("Hitsounds", audioCodecId);
    if (microphone)
      stems[2] = AudioTrack("Microphone", audioCodecId);
    return stems;
  }

  private void EncoderLoop(FFmpegNativeEncoder encoder)
  {
    try
    {
      while (true)
      {
        CapturedFrame frame = default;
        bool hasFrame = false;

        lock (_imageQueue)
        {
          if (_imageQueue.Count > 0)
          {
            long firstKey = _imageQueue.Keys[0];

            // Capture is over but an index was permanently lost: skip forward instead of
            // deadlocking on a frame that will never arrive.
            if (firstKey > _passCount && !_capturing)
              _passCount = firstKey;

            if (firstKey == _passCount)
            {
              frame = _imageQueue[firstKey];
              _imageQueue.RemoveAt(0);
              _passCount++;
              hasFrame = true;
            }
          }
        }

        if (hasFrame)
        {
          long frameIndex = _passCount - 1;
          if (frame.ManagedBuffer != null)
          {
            if (frame.ManagedBuffer.Length > 0)
            {
              encoder.SendDataFromBuffer(frame.ManagedBuffer, frame.Length);
              Interlocked.Increment(ref _framesSent);
              ArrayPool<byte>.Shared.Return(frame.ManagedBuffer);
            }
          }
          else if (frame.NativePointer != IntPtr.Zero && frame.Length > 0)
          {
            encoder.SendDataFromNativeBuffer(frame.NativePointer, frame.Length);
            Interlocked.Increment(ref _framesSent);
            if (frame.ReadbackState != null)
              _frameReader?.RecycleState(frame.ReadbackState);
          }

          // Snapshot both references: the main thread may null them during cancellation.
          OfflineAudioMixer? mixer = _mixer;
          AudioSyncCalculator? sync = _syncCalculator;
          if (_settings.RenderAudio && mixer != null && sync != null)
          {
            double frameTime = frameIndex / _settings.EffectiveVideoFps;
            int channels = _settings.AudioChannels;
            int samplesNeeded = sync.GetNextFrameSamples();

            _frameTimings.TryRemove(frameIndex, out OfflineAudioMixer.ReplayFrameTiming timing);

            int sampleCount = samplesNeeded * channels;
            float[] mixBuffer = ArrayPool<float>.Shared.Rent(sampleCount);
            float[] musicBuffer = ArrayPool<float>.Shared.Rent(sampleCount);
            float[] hitsoundBuffer = ArrayPool<float>.Shared.Rent(sampleCount);
            float[]? microphoneBuffer = _hasMicrophoneTrack ? ArrayPool<float>.Shared.Rent(sampleCount) : null;
            try
            {
              mixer.GenerateAudioStems(
                mixBuffer,
                musicBuffer,
                hitsoundBuffer,
                microphoneBuffer,
                samplesNeeded,
                frameTime,
                DspTimeBase,
                timing
              );
              encoder.WriteAudioSamples(0, mixBuffer, samplesNeeded);
              encoder.WriteAudioSamples(1, musicBuffer, samplesNeeded);
              encoder.WriteAudioSamples(2, hitsoundBuffer, samplesNeeded);
              if (microphoneBuffer != null)
                encoder.WriteAudioSamples(3, microphoneBuffer, samplesNeeded);
            }
            finally
            {
              ArrayPool<float>.Shared.Return(mixBuffer);
              ArrayPool<float>.Shared.Return(musicBuffer);
              ArrayPool<float>.Shared.Return(hitsoundBuffer);
              if (microphoneBuffer != null)
                ArrayPool<float>.Shared.Return(microphoneBuffer);
            }
          }
          else
          {
            _frameTimings.TryRemove(frameIndex, out _);
          }
          continue;
        }

        if (_cancelled)
          break;
        if (_endRequested && _passCount > _lastRequestedVideoFrameIndex)
          break;

        // A timeout rather than an infinite wait: a missed Set() costs 50 ms, not a deadlock.
        _frameAvailable.WaitOne(50);
      }

      if (!_cancelled)
        encoder.End();
      encoder.Dispose();
      _endSuccessful = !_cancelled;
    }
    catch (Exception exception)
    {
      RenderLog.Error("Render encoder thread failed: " + exception);
      try
      {
        encoder.Dispose();
      }
      catch
      {
        // Already disposed or native teardown failed; nothing further to do here.
      }
      _endSuccessful = false;
      // The only channel telling the main thread the consumer died. Every main-thread wait checks
      // this flag, otherwise a dead encoder hangs the game.
      _encoderThreadFailed = true;
    }
  }

  // ── Teardown ────────────────────────────────────────────────────────────────────────────────

  private void Finalize(bool success)
  {
    if (_finished)
      return;
    _finished = true;
    _draining = false;

    bool wasCapturing = _capturing;
    _capturing = false;
    _endRequested = true;
    if (!success)
      _cancelled = true;
    _frameAvailable.Set();

    if (_encoderThread is { IsAlive: true })
    {
      // The drain loop already waited across frames; this is the last-resort join before pooled
      // buffers are returned. Returning one while the thread still reads it encodes garbage.
      _encoderThread.Join(2000);
      if (_encoderThread.IsAlive)
        RenderLog.Error("The encoder thread is still running during cleanup; the output may be truncated.");
    }

    if (_mixer != null)
      RenderLog.Info("Mixer summary: " + _mixer.DescribeState());

    bool encoded = _settings.AudioPreviewOnly ? PreviewStems != null : (_endSuccessful && !_encoderThreadFailed);
    DisposeEncoder();
    DrainQueue();

    _frameReader?.Dispose();
    _frameReader = null;
    _mixer?.Reset();
    _mixer = null;
    _syncCalculator?.Reset();
    _syncCalculator = null;
    _microphone = null;
    AudioSpectrumGenerator.Clear();

    RestoreEnvironment();

    if (success && encoded)
    {
      lock (_statusGate)
      {
        _status.OutputPath = _settings.AudioPreviewOnly ? null : _settings.OutputPath;
        _status.State = RenderStates.Completed;
        _status.Message = _settings.AudioPreviewOnly ? "Audio preview ready." : "Render complete.";
        _status.ErrorCode = null;
      }
      RenderLog.Info(
        "Render complete. file="
          + _settings.OutputPath
          + ", frames="
          + Interlocked.Read(ref _framesSent)
          + ", readbackFailures="
          + _readbackFailures
          + ", throttles="
          + _throttleCount
      );
    }
    else
    {
      // A partially written container has no index and will not play; leaving it behind only
      // confuses the user.
      DeleteOutputFile();

      if (
        _pendingErrorCode != null
        || _encoderThreadFailed
        || success
        || (!success && wasCapturing && _pendingErrorMessage == null)
      )
      {
        // `success && !encoded` means the run ended before a single frame was encoded, which is a
        // failure even though nothing asked for a cancel.
        SetError(_pendingErrorCode ?? "render_failed", _pendingErrorMessage ?? "The render did not finish.");
      }
      else
      {
        lock (_statusGate)
        {
          _status.State = RenderStates.Cancelled;
          _status.Message = _pendingErrorMessage ?? "Render cancelled.";
        }
      }
    }

    DeleteMicrophonePlaybackFile();
    _frameAvailable.Dispose();
    if (ReferenceEquals(_current, this))
      _current = null;
  }

  private void DisposeEncoder()
  {
    FFmpegNativeEncoder? encoder = _encoder;
    _encoder = null;
    if (encoder == null)
      return;
    try
    {
      encoder.Dispose();
    }
    catch (Exception exception)
    {
      RenderLog.Error("Encoder disposal failed: " + exception.Message);
    }
  }

  private void DrainQueue()
  {
    lock (_imageQueue)
    {
      foreach (CapturedFrame frame in _imageQueue.Values)
      {
        if (frame.ManagedBuffer is { Length: > 0 })
          ArrayPool<byte>.Shared.Return(frame.ManagedBuffer);
        if (frame.ReadbackState != null)
          _frameReader?.RecycleState(frame.ReadbackState);
      }
      _imageQueue.Clear();
    }
    _frameTimings.Clear();
  }

  private void RestoreEnvironment()
  {
    if (_environmentRestored)
      return;
    _environmentRestored = true;
    _active = false;

    Features.Render.RenderDoTweenClock.Uninstall();

    Time.timeScale = 1f;
    // Leaving captureDeltaTime set means the editor keeps running on a capture timestep after the
    // render: the classic "the game is in slow motion forever" bug.
    Time.captureDeltaTime = 0f;
    UnityEngine.Application.runInBackground = _previousRunInBackground;

    try
    {
      if (_songSuppressed && scrConductor.instance != null && scrConductor.instance.song != null)
      {
        scrConductor.instance.song.clip = _suppressedSongClip;
        scrConductor.instance.song.time = Mathf.Max(0f, _suppressedSongTime);
      }
    }
    catch (Exception exception)
    {
      RenderLog.Warn("Could not restore the conductor song clip: " + exception.Message);
    }
    _songSuppressed = false;
    _suppressedSongClip = null;

    if (_listenerMuted)
    {
      try
      {
        AudioListener.volume = _previousListenerVolume;
      }
      catch (Exception exception)
      {
        RenderLog.Warn("Could not restore the audio listener volume: " + exception.Message);
      }
      _listenerMuted = false;
    }

    try
    {
      _overlayCapture.Restore();
    }
    catch (Exception exception)
    {
      RenderLog.Warn("Overlay canvas restore failed: " + exception.Message);
    }

    try
    {
      if (_resolutionRequested && (_originalWidth != Screen.width || _originalHeight != Screen.height))
        Screen.SetResolution(_originalWidth, _originalHeight, _originalFullScreen);
    }
    catch (Exception exception)
    {
      RenderLog.Warn("Could not restore the window resolution: " + exception.Message);
    }
    _resolutionRequested = false;

    try
    {
      if (_postFrameHook != null)
      {
        UnityEngine.Object.Destroy(_postFrameHook);
        _postFrameHook = null;
      }
      if (_cameraForced && scrCamera.instance != null)
      {
        scrCamera.instance.forceRTCam = false;
        scrCamera.instance.SetupRTCam(false);
        if (_restoreCustomFrameRate)
          scrCamera.instance.SetCustomFrameRate(true, _previousCustomFrameRate);
      }
    }
    catch (Exception exception)
    {
      RenderLog.Warn("Could not restore the camera state: " + exception.Message);
    }
    _cameraForced = false;
  }

  private void DeleteOutputFile()
  {
    string path = _settings.OutputPath;
    if (string.IsNullOrEmpty(path))
      return;
    try
    {
      if (File.Exists(path))
      {
        File.Delete(path);
        RenderLog.Info("Deleted the incomplete render output: " + path);
      }
    }
    catch (Exception exception)
    {
      RenderLog.Warn("Could not delete the incomplete render output: " + exception.Message);
    }
  }

  private void DeleteMicrophonePlaybackFile()
  {
    string? path = _microphonePlaybackFile;
    _microphonePlaybackFile = null;
    if (string.IsNullOrEmpty(path))
      return;
    try
    {
      if (File.Exists(path))
        File.Delete(path);
    }
    catch (Exception exception)
    {
      RenderLog.Warn("Could not delete the temporary microphone file: " + exception.Message);
    }
  }

  // ── Helpers ─────────────────────────────────────────────────────────────────────────────────

  private void SetStatus(string state, string message)
  {
    lock (_statusGate)
    {
      _status.State = state;
      _status.Message = message;
    }
  }

  private void SetError(string code, string message)
  {
    lock (_statusGate)
    {
      _status.State = RenderStates.Error;
      _status.ErrorCode = code;
      _status.Message = message;
    }
    RenderLog.Error("Render failed. code=" + code + ", message=" + message);
  }

  public static string GetOutputExtension(Codec codec) =>
    codec switch
    {
      Codec.AV1 or Codec.VP9 => ".webm",
      Codec.VVC => ".mkv",
      Codec.ProRes => ".mov",
      _ => ".mp4",
    };

  // mp4/mov carry AAC; webm/mkv carry Opus (Opus-in-mov is non-standard).
  public static AVCodecID GetAudioCodecId(string extension) =>
    extension is ".mp4" or ".mov" ? AVCodecID.AV_CODEC_ID_AAC : AVCodecID.AV_CODEC_ID_OPUS;

  private static string EnsureOutputPath(string requested, string extension)
  {
    string directory;
    string fileName;

    if (!string.IsNullOrWhiteSpace(requested))
    {
      directory = Path.GetDirectoryName(requested) ?? RenderOutputDirectory.Default;
      fileName = Path.GetFileName(requested);
      if (string.IsNullOrEmpty(fileName))
        fileName = DefaultFileName(extension);
      else if (!Path.HasExtension(fileName))
        fileName += extension;
    }
    else
    {
      directory = RenderOutputDirectory.Default;
      fileName = DefaultFileName(extension);
    }

    if (string.IsNullOrEmpty(directory))
      directory = RenderOutputDirectory.Default;
    Directory.CreateDirectory(directory);
    return Path.Combine(directory, fileName);
  }

  private static string DefaultFileName(string extension) =>
    "TUFReplay " + DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss") + extension;
}
