using AdofaiIpc.Core;
using TUFReplayRenderer.Ipc.Dtos;

namespace TUFReplayRenderer.Features.Ipc;

public static class RendererHealthIpcHandlers
{
  public static object Get(IpcRequest request) =>
    new RendererHealthResponseDto
    {
      Ok = true,
      Mod = "TUFReplay-Renderer",
      ModVersion = Main.Instance?.Version,
      ProtocolVersion = RendererHealthResponseDto.CurrentProtocolVersion,
    };
}
