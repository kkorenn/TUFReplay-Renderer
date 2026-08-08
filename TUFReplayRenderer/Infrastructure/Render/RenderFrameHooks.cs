#nullable enable
using System;
using System.Reflection;
using HarmonyLib;
using TUFReplayRenderer.Application.Render;
using UnityEngine;

namespace TUFReplayRenderer.Infrastructure.Render;

/// <summary>
/// Runs before every game script each frame. This is where the virtual clock advances, so it must
/// win the execution order against everything that reads Time.*.
/// </summary>
[DefaultExecutionOrder(int.MinValue)]
public sealed class RenderPreFrameHook : MonoBehaviour
{
  private void Update()
  {
    try
    {
      ReplayRenderSession.Current?.OnPreFrame();
      ReplayRenderCoordinator.Tick();
    }
    catch (Exception exception)
    {
      Main.Instance?.LogException(nameof(RenderPreFrameHook), exception);
    }
  }

  private void OnApplicationQuit()
  {
    // Native FFmpeg contexts are not GC-managed; leaking them holds the output file handle open
    // past process exit.
    try
    {
      ReplayRenderSession.Current?.AbortForProcessExit();
    }
    catch (Exception exception)
    {
      Main.Instance?.LogException(nameof(RenderPreFrameHook), exception);
    }
  }
}

/// <summary>
/// Runs after every game script each frame and grabs the camera's RenderTexture.
///
/// ADOFAI renders into a RenderTexture displayed on a quad when <c>forceRTCam</c> is set; the
/// texture is read from that quad's material rather than from the camera directly.
/// </summary>
[DefaultExecutionOrder(int.MaxValue)]
public sealed class RenderPostFrameHook : MonoBehaviour
{
  private FieldInfo? _camQuadMeshField;

  private void Awake() => _camQuadMeshField = AccessTools.Field(typeof(scrCamera), "camQuadMesh");

  private void LateUpdate()
  {
    try
    {
      // scrCamera.instance can be destroyed mid scene transition while this hook survives one
      // more frame.
      if (_camQuadMeshField == null || scrCamera.instance == null)
        return;

      MeshRenderer? quad = _camQuadMeshField.GetValue(scrCamera.instance) as MeshRenderer;
      if (quad == null || quad.material == null)
        return;

      ReplayRenderSession.Current?.OnPostFrame(quad.material.mainTexture as RenderTexture);
    }
    catch (Exception exception)
    {
      Main.Instance?.LogException(nameof(RenderPostFrameHook), exception);
    }
  }
}
