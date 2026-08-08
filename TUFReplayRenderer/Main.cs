using System;
using TUFReplayRenderer.Bootstrap;
using UnityModManagerNet;

namespace TUFReplayRenderer;

public sealed class Main
{
  public static Main Instance { get; private set; }

  public UnityModManager.ModEntry ModEntry { get; }
  public string InstallPath => ModEntry.Path;

  /// <summary>Directory the mod assembly runs from; render assets are staged next to it.</summary>
  public string PayloadPath => System.IO.Path.GetDirectoryName(typeof(Main).Assembly.Location) ?? ModEntry.Path;

  public string Version => ModEntry.Info.Version;

  private bool _enabled;

  private Main(UnityModManager.ModEntry modEntry)
  {
    ModEntry = modEntry;
  }

  public static bool Load(UnityModManager.ModEntry modEntry)
  {
    try
    {
      Instance = new Main(modEntry);
      modEntry.OnToggle = OnToggle;
      modEntry.OnUnload = OnUnload;
      Instance.Enable();
      return true;
    }
    catch (Exception exception)
    {
      modEntry.Logger.Error(exception.ToString());
      return false;
    }
  }

  public void Log(string message)
  {
    ModEntry.Logger.Log(message);
  }

  public void LogException(string context, Exception exception)
  {
    ModEntry.Logger.Error("[" + context + "] " + exception);
  }

  private static bool OnToggle(UnityModManager.ModEntry modEntry, bool value)
  {
    try
    {
      if (value)
        Instance.Enable();
      else
        Instance.Disable();
      return true;
    }
    catch (Exception exception)
    {
      modEntry.Logger.Error(exception.ToString());
      return false;
    }
  }

  private static bool OnUnload(UnityModManager.ModEntry modEntry)
  {
    try
    {
      Instance?.Disable();
      return true;
    }
    catch (Exception exception)
    {
      modEntry.Logger.Error(exception.ToString());
      return false;
    }
  }

  private void Enable()
  {
    if (_enabled)
      return;

    RendererRegistry.Initialize();
    _enabled = true;
  }

  private void Disable()
  {
    if (!_enabled)
      return;

    RendererRegistry.Shutdown();
    _enabled = false;
  }
}
