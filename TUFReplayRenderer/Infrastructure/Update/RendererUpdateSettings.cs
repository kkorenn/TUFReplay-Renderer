using System;
using System.IO;
using Newtonsoft.Json;

namespace TUFReplayRenderer.Infrastructure.Update;

public sealed class RendererUpdateSettings
{
  public bool AutoUpdate = true;
  public bool ReceiveBetaUpdates = true;

  private static RendererUpdateSettings _current;
  private static string _path;

  public static RendererUpdateSettings Current => _current ??= new RendererUpdateSettings();

  public static void Initialize(string installPath)
  {
    _path = Path.Combine(installPath, "UpdateSettings.json");
    try
    {
      if (File.Exists(_path))
        _current =
          JsonConvert.DeserializeObject<RendererUpdateSettings>(File.ReadAllText(_path))
          ?? new RendererUpdateSettings();
      else
        _current = new RendererUpdateSettings();
    }
    catch (Exception exception)
    {
      Main.Instance?.LogException(nameof(RendererUpdateSettings), exception);
      _current = new RendererUpdateSettings();
    }
  }

  public static void Save()
  {
    if (_path == null)
      return;
    try
    {
      File.WriteAllText(_path, JsonConvert.SerializeObject(Current, Formatting.Indented));
    }
    catch (Exception exception)
    {
      Main.Instance?.LogException(nameof(RendererUpdateSettings), exception);
    }
  }
}
