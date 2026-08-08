using System;
using HarmonyLib;
using TUFReplayRenderer.Application.Render;
using TUFReplayRenderer.Infrastructure.FFmpeg;
using TUFReplayRenderer.Infrastructure.Render;
using UnityEngine;

namespace TUFReplayRenderer.Features.Render;

/// <summary>
/// Owns the render feature's lifetime: the always-resident frame hook and the FFmpeg availability
/// probe. The feature stays enabled even when FFmpeg is missing so the IPC surface can explain why
/// rendering is unavailable instead of silently disappearing.
/// </summary>
public sealed class RenderFeature
{
  public static RenderFeature Instance { get; private set; }

  private GameObject _hookObject;
  private RenderPreFrameHook _hook;

  public bool Active { get; private set; }

  /// <summary>True when a render can actually be started on this machine.</summary>
  public bool CanRender => Active && FFmpegNativeLibrary.IsAvailable && RenderPatchInstaller.BlockingFailure == null;

  public string UnavailableReason =>
    RenderPatchInstaller.BlockingFailure
    ?? (FFmpegNativeLibrary.IsAvailable ? null : FFmpegNativeLibrary.FailureMessage);

  public RenderFeature()
  {
    Instance = this;
  }

  public void Enable(Harmony harmony)
  {
    if (Active)
      return;
    Active = true;

    // Applied individually rather than through PatchAll: a throw inside PatchAll aborts the whole
    // batch and would take recording and replay down with rendering.
    RenderPatchInstaller.Apply(harmony);

    _hookObject = new GameObject("TUFReplay Render Hook");
    UnityEngine.Object.DontDestroyOnLoad(_hookObject);
    _hook = _hookObject.AddComponent<RenderPreFrameHook>();

    // Probing here surfaces a missing/mismatched FFmpeg install in the log at startup rather than
    // as a mid-render failure.
    try
    {
      // Resolve on the main thread so later IPC calls never touch Application.dataPath off-thread.
      RenderOutputDirectory.Warm();
      RenderColorShader.Load(Main.Instance?.PayloadPath ?? string.Empty);
      FFmpegNativeLibrary.EnsureProbed();
      if (FFmpegNativeLibrary.IsAvailable)
      {
        RenderLog.Info("Replay rendering is available. ffmpeg=" + FFmpegNativeLibrary.DescribeVersion());
      }
      else
      {
        RenderLog.Warn("Replay rendering is unavailable. " + FFmpegNativeLibrary.FailureMessage);
        // Fetch the platform's FFmpeg in the background while the game keeps booting; rendering
        // unlocks without a restart once it lands.
        FFmpegRuntimeInstaller.BeginInstallIfNeeded();
      }
    }
    catch (Exception exception)
    {
      Main.Instance?.LogException(nameof(RenderFeature), exception);
    }
  }

  /// <summary>Runs a coroutine on the always-resident hook object (e.g. async audio decodes).</summary>
  public UnityEngine.Coroutine RunCoroutine(System.Collections.IEnumerator routine)
  {
    if (_hook == null)
      return null;
    return _hook.StartCoroutine(routine);
  }

  public void Disable()
  {
    if (!Active)
      return;
    Active = false;

    ReplayRenderCoordinator.Shutdown();
    RenderDoTweenClock.Uninstall();

    if (_hookObject != null)
    {
      UnityEngine.Object.Destroy(_hookObject);
      _hookObject = null;
    }
  }
}
