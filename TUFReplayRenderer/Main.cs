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
      Infrastructure.Update.RendererUpdateSettings.Initialize(Instance.InstallPath);
      modEntry.OnToggle = OnToggle;
      modEntry.OnUnload = OnUnload;
      modEntry.OnGUI = OnGUI;
      modEntry.OnSaveGUI = OnSaveGUI;
      Instance.Enable();

      // Runs while the game keeps booting; a found update stages itself and the loader applies
      // it at the next launch.
      Infrastructure.Update.RendererAutoUpdater.BeginCheckInBackground();
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

  private static void OnGUI(UnityModManager.ModEntry modEntry)
  {
    Infrastructure.Update.RendererUpdateSettings settings = Infrastructure.Update.RendererUpdateSettings.Current;
    UnityEngine.GUILayout.Label("Updates");
    bool autoUpdate = UnityEngine.GUILayout.Toggle(settings.AutoUpdate, "Automatically download updates");
    bool beta = UnityEngine.GUILayout.Toggle(settings.ReceiveBetaUpdates, "Receive beta updates");
    UnityEngine.GUILayout.Label("A downloaded update applies on the next game launch.");
    if (autoUpdate != settings.AutoUpdate || beta != settings.ReceiveBetaUpdates)
    {
      settings.AutoUpdate = autoUpdate;
      settings.ReceiveBetaUpdates = beta;
      Infrastructure.Update.RendererUpdateSettings.Save();
    }
  }

  private static void OnSaveGUI(UnityModManager.ModEntry modEntry)
  {
    Infrastructure.Update.RendererUpdateSettings.Save();
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
