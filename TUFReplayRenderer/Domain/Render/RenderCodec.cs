namespace TUFReplayRenderer.Domain.Render;

/// <summary>Video codec families the replay renderer can target.</summary>
public enum Codec
{
  H264,
  H265,
  VP9,
  AV1,
  VVC,
  ProRes,
}

/// <summary>Encoder rate control strategy.</summary>
public enum RateControlMode
{
  CBR,
  VBR,
  CQP,
}
