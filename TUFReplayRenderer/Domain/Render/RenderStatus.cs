namespace TUFReplayRenderer.Domain.Render;

public static class RenderStates
{
  public const string Idle = "idle";
  public const string Preparing = "preparing";
  public const string OpeningLevel = "opening_level";
  public const string Capturing = "capturing";
  public const string Finalizing = "finalizing";
  public const string Completed = "completed";
  public const string Cancelled = "cancelled";
  public const string Error = "error";
}

/// <summary>Snapshot of a replay render operation, polled by the companion web UI.</summary>
public sealed class RenderStatus
{
  public string OperationId;
  public string RunId;
  public string State = RenderStates.Idle;
  public string ErrorCode;
  public string Message;

  /// <summary>Video frames handed to the encoder so far.</summary>
  public long FramesEncoded;

  /// <summary>Video frames captured from the GPU so far.</summary>
  public long FramesCaptured;

  /// <summary>Output media seconds produced so far.</summary>
  public double EncodedSeconds;

  /// <summary>Populated once the file is complete and playable.</summary>
  public string OutputPath;

  public int Width;
  public int Height;
  public double VideoFps;
  public string EncoderName;
  public bool MicrophoneIncluded;

  public static RenderStatus Idle() => new RenderStatus { State = RenderStates.Idle };

  public RenderStatus Clone() => (RenderStatus)MemberwiseClone();
}
