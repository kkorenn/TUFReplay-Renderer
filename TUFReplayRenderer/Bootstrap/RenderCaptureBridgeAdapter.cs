using TUFReplay.Application.Microphone;
using TUFReplay.Application.Replay;
using TUFReplay.Domain.Microphone;
using TUFReplayRenderer.Application.Render;

namespace TUFReplayRenderer.Bootstrap;

/// <summary>
/// Adapts TUFReplay's <see cref="IRenderCaptureBridge"/> callbacks onto the renderer's
/// coordinator. TUFReplay calls these while a replay plays in render-capture mode.
/// </summary>
internal sealed class RenderCaptureBridgeAdapter : IRenderCaptureBridge
{
  public bool IsCapturingActive => ReplayRenderSession.IsCapturingActive;

  public bool ReadyForPlayMode => ReplayRenderCoordinator.ReadyForPlayMode;

  public void OnReplayStarting(string operationId) => ReplayRenderCoordinator.OnReplayStarting(operationId);

  public void OnReplayTerminal(
    string operationId,
    string replayState,
    bool allowTrailingCapture,
    string message,
    string errorCode = null
  ) => ReplayRenderCoordinator.OnReplayTerminal(operationId, replayState, allowTrailingCapture, message, errorCode);

  public void AttachMicrophone(
    string operationId,
    StoredMicrophoneRecording recording,
    Pcm16WaveInfo wave,
    Pcm16LimiterEnvelope limiterEnvelope
  ) => ReplayRenderCoordinator.AttachMicrophone(operationId, recording, wave, limiterEnvelope);
}
