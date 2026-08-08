using AdofaiIpc;

namespace TUFReplayRenderer.Features.Ipc;

/// <summary>
/// The renderer's own IPC namespace. The companion web UI detects the renderer mod by this
/// namespace existing: when the mod is not installed, `render.capabilities.get` fails and the web
/// UI hides every render control.
/// </summary>
public sealed class RendererIpcFeature
{
  private const string Namespace = "tuf-replay-renderer";

  private bool _active;

  public void Enable()
  {
    if (_active)
      return;
    _active = true;

    AdofaiIpcNamespace ipc = AdofaiIpc.AdofaiIpc.RegisterNamespace(
      Namespace,
      new IpcNamespaceInfo
      {
        DisplayName = "TUFReplay Renderer",
        Version = Main.Instance.Version.ToString(),
        AllowedOrigins = new[]
        {
          "https://tuforums.com",
          "https://tufreplay.impl1113.dev",
          "https://tufreplay.koren.rip",
          "http://localhost",
          "http://127.0.0.1",
        },
      }
    );

    ipc.Register("health.get", RendererHealthIpcHandlers.Get);
    // Status is a pure snapshot read, but starting and cancelling a render touch Unity objects
    // (camera state, play mode), so they must run on the main thread.
    ipc.Register("render.status.get", RenderIpcHandlers.GetStatus);
    ipc.RegisterMainThread("render.capabilities.get", RenderIpcHandlers.GetCapabilities);
    ipc.RegisterMainThread("render.start", RenderIpcHandlers.Start);
    ipc.RegisterMainThread("render.cancel", RenderIpcHandlers.Cancel);
    ipc.RegisterMainThread("render.preview.start", RenderIpcHandlers.StartPreview);
    ipc.RegisterMainThread("render.preview.stop", RenderIpcHandlers.StopPreview);
    ipc.Register("render.preview.result.get", RenderIpcHandlers.GetPreviewResult);

    // Newer AdofaiIpc registers a namespace as "initializing" and gates every call until the owner
    // calls MarkReady(). Older builds have no such method, so it is invoked by reflection.
    MarkNamespaceReady(ipc);

    Main.Instance.Log("[IPC] Registered namespace: " + Namespace);
  }

  private static void MarkNamespaceReady(object ipcNamespace)
  {
    try
    {
      System.Reflection.MethodInfo markReady = ipcNamespace?.GetType().GetMethod("MarkReady", System.Type.EmptyTypes);
      markReady?.Invoke(ipcNamespace, null);
    }
    catch (System.Exception exception)
    {
      Main.Instance?.LogException("MarkNamespaceReady", exception);
    }
  }

  public void Disable()
  {
    if (!_active)
      return;
    _active = false;

    AdofaiIpc.AdofaiIpc.UnregisterNamespace(Namespace);
    Main.Instance.Log("[IPC] Unregistered namespace: " + Namespace);
  }
}
