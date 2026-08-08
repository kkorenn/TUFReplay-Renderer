#nullable enable
using System;
using System.Collections.Generic;
using UnityEngine;

namespace TUFReplayRenderer.Infrastructure.Render;

/// <summary>
/// Makes the game's overlay UI part of the captured frame.
///
/// The capture path reads the camera's RenderTexture, but Screen Space - Overlay canvases are
/// composited by Unity directly onto the display, after and outside every camera — the fail
/// screen's "% complete", the clear screen's judgement text and anything else on
/// <c>scrUIController.canvas</c> never reaches the RenderTexture. ADOFAIRenderer never solved
/// this; it hides that UI (<c>disableCongratsMessage</c>, <c>hideDifficultyUI</c>) and renders
/// with no-fail so the fail screen cannot appear. This renderer wants that UI in the video.
///
/// While a session captures, every active overlay canvas is retargeted to Screen Space - Camera
/// on the capturing camera, pinned to the near plane with maximum sorting order so it stays on
/// top of the world exactly as an overlay would. The camera's culling mask is widened to include
/// the canvas layers. Everything is restored on teardown, and canvases that appear mid-run (the
/// fail and clear screens instantiate late) are caught by the per-frame sweep.
/// </summary>
public sealed class RenderOverlayCanvasCapture
{
  private sealed class ConvertedCanvas
  {
    public Canvas? Canvas;
    public RenderMode OriginalRenderMode;
    public Camera? OriginalWorldCamera;
    public float OriginalPlaneDistance;
    public int OriginalSortingOrder;
  }

  private readonly List<ConvertedCanvas> _converted = new List<ConvertedCanvas>();
  private Camera? _camera;
  private int _originalCullingMask;
  private bool _cameraAdjusted;

  /// <summary>Sweeps for overlay canvases and retargets them into the capture camera.</summary>
  public void Tick()
  {
    scrCamera cameraHost = scrCamera.instance;
    if (cameraHost == null || cameraHost.camobj == null)
      return;

    Camera camera = cameraHost.camobj;
    if (!ReferenceEquals(camera, _camera))
    {
      // First sight of the camera (or the camera changed): snapshot its culling mask.
      RestoreCamera();
      _camera = camera;
      _originalCullingMask = camera.cullingMask;
      _cameraAdjusted = true;
    }

    Canvas[] canvases = UnityEngine.Object.FindObjectsByType<Canvas>(FindObjectsSortMode.None);
    foreach (Canvas canvas in canvases)
    {
      if (canvas == null || !canvas.isActiveAndEnabled)
        continue;
      // Only root canvases own a render mode; nested canvases inherit.
      if (canvas.transform.parent != null && canvas.transform.parent.GetComponentInParent<Canvas>() != null)
        continue;
      if (canvas.renderMode != RenderMode.ScreenSpaceOverlay)
        continue;

      _converted.Add(
        new ConvertedCanvas
        {
          Canvas = canvas,
          OriginalRenderMode = canvas.renderMode,
          OriginalWorldCamera = canvas.worldCamera,
          OriginalPlaneDistance = canvas.planeDistance,
          OriginalSortingOrder = canvas.sortingOrder,
        }
      );

      canvas.renderMode = RenderMode.ScreenSpaceCamera;
      canvas.worldCamera = camera;
      canvas.planeDistance = camera.nearClipPlane + 0.01f;
      canvas.sortingOrder = short.MaxValue;
      camera.cullingMask |= 1 << canvas.gameObject.layer;

      RenderLog.Info(
        "Capturing overlay canvas: " + canvas.gameObject.name + " (layer " + canvas.gameObject.layer + ")"
      );
    }
  }

  /// <summary>Puts every touched canvas and the camera back the way they were.</summary>
  public void Restore()
  {
    foreach (ConvertedCanvas entry in _converted)
    {
      Canvas? canvas = entry.Canvas;
      if (canvas == null)
        continue;
      try
      {
        canvas.renderMode = entry.OriginalRenderMode;
        canvas.worldCamera = entry.OriginalWorldCamera;
        canvas.planeDistance = entry.OriginalPlaneDistance;
        canvas.sortingOrder = entry.OriginalSortingOrder;
      }
      catch (Exception exception)
      {
        RenderLog.Warn("Could not restore an overlay canvas: " + exception.Message);
      }
    }
    _converted.Clear();
    RestoreCamera();
  }

  private void RestoreCamera()
  {
    if (!_cameraAdjusted)
      return;
    try
    {
      if (_camera != null)
        _camera.cullingMask = _originalCullingMask;
    }
    catch (Exception exception)
    {
      RenderLog.Warn("Could not restore the camera culling mask: " + exception.Message);
    }
    _camera = null;
    _cameraAdjusted = false;
  }
}
