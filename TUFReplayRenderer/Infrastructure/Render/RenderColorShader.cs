#nullable enable
using System;
using System.IO;
using UnityEngine;

namespace TUFReplayRenderer.Infrastructure.Render;

/// <summary>
/// Loads ADOFAIRenderer's RGBA→NV12/YUV420P colour-conversion compute shader (MIT) from the
/// platform AssetBundle shipped under Assets/render/shaders.
///
/// The GPU path matters for more than throughput: the shader samples the camera texture through
/// the texture unit, which yields correct channel order regardless of whether the RenderTexture is
/// stored RGBA or BGRA. The CPU fallback reads raw bytes and has to know the layout instead.
/// </summary>
public static class RenderColorShader
{
  private static bool _loaded;

  public static ComputeShader? Shader { get; private set; }

  public static void Load(string payloadPath)
  {
    if (_loaded)
      return;
    _loaded = true;

    try
    {
      string platform = UnityEngine.Application.platform switch
      {
        RuntimePlatform.WindowsPlayer => "win64",
        RuntimePlatform.OSXPlayer => "osx",
        _ => "linux",
      };
      string bundlePath = Path.Combine(payloadPath, "Assets", "render", "shaders", platform);
      if (!File.Exists(bundlePath))
      {
        RenderLog.Warn("Colour-conversion shader bundle is missing (" + bundlePath + "); using the CPU path.");
        return;
      }

      AssetBundle? bundle = AssetBundle.LoadFromFile(bundlePath);
      if (bundle == null)
      {
        RenderLog.Warn("The colour-conversion shader bundle failed to load; using the CPU path.");
        return;
      }

      Shader = bundle.LoadAsset<ComputeShader>("ColorConversion");
      if (Shader == null)
      {
        RenderLog.Warn("The ColorConversion compute shader was not found in the bundle; using the CPU path.");
        bundle.Unload(true);
        return;
      }

      RenderLog.Info("GPU colour conversion enabled (platform=" + platform + ").");
    }
    catch (Exception exception)
    {
      // Never fatal: the CPU path produces identical frames, just slower.
      Shader = null;
      RenderLog.Warn("Colour-conversion shader unavailable (" + exception.Message + "); using the CPU path.");
    }
  }
}
