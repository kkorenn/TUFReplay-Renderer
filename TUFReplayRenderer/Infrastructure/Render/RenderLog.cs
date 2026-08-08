namespace TUFReplayRenderer.Infrastructure.Render;

/// <summary>
/// Logging shim for the renderer sources ported from ADOFAIRenderer (MIT), whose original
/// implementation logged through MelonLoader. TUFReplay runs under UnityModManager, so every
/// message is routed to the mod entry logger instead.
/// </summary>
public static class RenderLog
{
  public static void Info(string message) => Main.Instance?.Log("[Render] " + message);

  public static void Warn(string message) => Main.Instance?.Log("[Render] WARN " + message);

  public static void Error(string message) => Main.Instance?.Log("[Render] ERROR " + message);
}
