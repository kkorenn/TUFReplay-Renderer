using HarmonyLib;
using TUFReplay.Application.Replay;
using TUFReplayRenderer.Features.Ipc;
using TUFReplayRenderer.Features.Render;

namespace TUFReplayRenderer.Bootstrap;

/// <summary>
/// Owns the renderer mod's lifetime: the render feature itself, the capture bridge that plugs the
/// renderer into TUFReplay's replay pipeline, and the renderer's own IPC namespace.
/// </summary>
public static class RendererRegistry
{
  private const string HarmonyId = "dev.koren.tufreplay.renderer";

  private static Harmony _harmony;
  private static RenderCaptureBridgeAdapter _bridge;

  public static RenderFeature Render { get; private set; }
  public static RendererIpcFeature Ipc { get; private set; }

  public static void Initialize()
  {
    Render = new RenderFeature();
    Ipc = new RendererIpcFeature();
    _harmony = new Harmony(HarmonyId);
    try
    {
      Render.Enable(_harmony);

      // Registering the bridge is what makes TUFReplay's replay pipeline render-aware; without it
      // the base mod behaves exactly as if this mod were not installed. The reflection version
      // check runs FIRST, and the typed bridge code lives in its own method: JIT only binds the
      // bridge types when that method is actually called, so an incompatible TUFReplay degrades
      // to "rendering unavailable" instead of a MissingMethodException.
      if (BridgeCompat.Check())
        RegisterBridge();

      Ipc.Enable();
    }
    catch
    {
      Shutdown();
      throw;
    }
  }

  [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
  private static void RegisterBridge()
  {
    _bridge = new RenderCaptureBridgeAdapter();
    RenderCaptureBridge.Register(_bridge);
  }

  [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
  private static void UnregisterBridge()
  {
    if (_bridge != null)
    {
      RenderCaptureBridge.Unregister(_bridge);
      _bridge = null;
    }
  }

  public static void Shutdown()
  {
    Ipc?.Disable();

    if (_bridge != null)
      UnregisterBridge();

    Render?.Disable();

    _harmony?.UnpatchAll(HarmonyId);
    _harmony = null;

    Render = null;
    Ipc = null;
  }
}
