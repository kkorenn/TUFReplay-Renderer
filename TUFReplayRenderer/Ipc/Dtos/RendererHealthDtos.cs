namespace TUFReplayRenderer.Ipc.Dtos;

public sealed class RendererHealthResponseDto
{
  public const int CurrentProtocolVersion = 1;

  public bool Ok;
  public string Mod;
  public string ModVersion;
  public int ProtocolVersion;
}
