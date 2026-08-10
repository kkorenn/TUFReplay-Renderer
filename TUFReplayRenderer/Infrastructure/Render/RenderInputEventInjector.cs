using System;
using System.Reflection;
using SkyHook;
using TUFReplayRenderer.Application.Render;

namespace TUFReplayRenderer.Infrastructure.Render;

/// <summary>
/// Replays input edges into the real input pipeline during capture. Normal replay emits OS-level
/// key events; a render cannot (wall time and the capture timeline run at different speeds, and a
/// background render must not type into other applications), so this invokes
/// <see cref="SkyHookManager.KeyUpdated"/> directly — the exact event real input arrives on. Every
/// hook consumer (key viewers, overlays) receives the replayed keys indistinguishably from live
/// play.
///
/// The game itself must NOT consume them: render judgments are driven by hit-context playback, and
/// async-input key events on top would double-hit the run. The game's only path from the hook to
/// gameplay is <c>AsyncInputManager.keyQueue</c>, filled by its KeyUpdated listener; the queue is
/// cleared synchronously after each injection, before the game's Update can ever drain it.
/// </summary>
internal static class RenderInputEventInjector
{
  private static readonly DateTime UnixEpoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

  private static FieldInfo _timeSec;
  private static FieldInfo _timeSubsecNano;
  private static FieldInfo _type;
  private static FieldInfo _label;
  private static FieldInfo _key;
  private static bool _fieldsResolved;
  private static bool _fieldsUsable;

  private static double _captureStartUnixSeconds;
  private static double _captureStartVirtualElapsed;
  private static ReplayRenderSession _anchoredSession;

  public static void OnReplayInput(KeyLabel label, bool down)
  {
    ReplayRenderSession session = ReplayRenderSession.Current;
    if (session == null || session.Settings.AudioPreviewOnly)
      return;
    if (!ResolveFields())
      return;

    // Injected timestamps advance with the capture timeline, anchored to the wall clock at the
    // first injected edge — consumers that read the event's own time see virtual spacing, and
    // consumers that stamp on receipt see the edge on the correct captured frame either way.
    double virtualElapsed = session.VirtualDspTime - session.DspTimeBase;
    if (!ReferenceEquals(_anchoredSession, session))
    {
      _anchoredSession = session;
      _captureStartUnixSeconds = (DateTime.UtcNow - UnixEpoch).TotalSeconds;
      _captureStartVirtualElapsed = virtualElapsed;
    }
    double unixSeconds = _captureStartUnixSeconds + (virtualElapsed - _captureStartVirtualElapsed);
    long timeSec = (long)unixSeconds;
    uint timeSubsecNano = (uint)Math.Max(0d, (unixSeconds - timeSec) * 1_000_000_000d);

    ushort nativeKey = 0;
    if (!TryGetWindowsModifierVirtualKey(label, out nativeKey))
    {
      try
      {
        nativeKey = SkyHookKeyMapper.KeyLabelToNativeKeyCode(label);
      }
      catch (Exception exception)
      {
        RenderLog.Warn("Native key code lookup failed for " + label + ": " + exception.Message);
      }
    }

    SkyHookEvent crafted;
    try
    {
      object boxed = default(SkyHookEvent);
      _timeSec.SetValue(boxed, timeSec);
      _timeSubsecNano.SetValue(boxed, timeSubsecNano);
      _type.SetValue(boxed, down ? EventType.KeyPressed : EventType.KeyReleased);
      _label.SetValue(boxed, label);
      _key.SetValue(boxed, nativeKey);
      crafted = (SkyHookEvent)boxed;
    }
    catch (Exception exception)
    {
      // Reflection over initonly fields is runtime-dependent; a throw here must not take the
      // replay tick down with it.
      _fieldsUsable = false;
      RenderLog.Warn("SkyHookEvent could not be crafted; replayed keys disabled. error=" + exception.Message);
      return;
    }

    try
    {
      // Enter the pipeline at HookCallback — the exact method the native hook thread calls — not
      // at KeyUpdated. Consumers that Harmony-patch HookCallback (Quartz's key viewer does) sit
      // UPSTREAM of KeyUpdated and would never see an event injected downstream; invoking the
      // (detoured) method runs their patches first and then flows into KeyUpdated for everyone
      // else, exactly like real input.
      MethodInfo hookCallback = ResolveHookCallback();
      if (hookCallback != null && SkyHookManager.Instance != null)
        hookCallback.Invoke(SkyHookManager.Instance, new object[] { crafted });
      else
        SkyHookManager.KeyUpdated.Invoke(crafted);
      _injectedCount++;
      if (down && IsModifier(label) && _loggedModifiers.Add(label))
        RenderLog.Info("Injected modifier: label=" + label + " vk=0x" + nativeKey.ToString("X2"));
      if (_injectedCount == 1 || _injectedCount % 200 == 0)
        RenderLog.Info(
          "Injected key edge #"
            + _injectedCount
            + ": "
            + label
            + (down ? " down" : " up")
            + " @"
            + virtualElapsed.ToString("F3")
            + "s"
        );
    }
    catch (Exception exception)
    {
      RenderLog.Warn("A KeyUpdated subscriber threw for an injected key edge: " + exception.Message);
    }
    finally
    {
      // Synchronous, same stack as the Invoke: the game's async-input Update can never run in
      // between, so gameplay never sees the injected edge.
      AsyncInputManager.keyQueue.Clear();
    }
  }

  private static long _injectedCount;
  private static readonly System.Collections.Generic.HashSet<KeyLabel> _loggedModifiers =
    new System.Collections.Generic.HashSet<KeyLabel>();

  private static bool IsModifier(KeyLabel label) =>
    label
      is KeyLabel.LAlt
        or KeyLabel.RAlt
        or KeyLabel.LControl
        or KeyLabel.RControl
        or KeyLabel.LShift
        or KeyLabel.RShift;

  /// <summary>
  /// Windows low-level hooks report side-specific virtual keys for modifiers (VK_RMENU and
  /// friends), and consumers map the event's Key before its Label — so the injected VK must be
  /// side-specific too. SkyHook's own label-to-code lookup collapses the sides, which made a
  /// replayed RAlt invisible to key viewers bound to RightAlt.
  /// </summary>
  private static bool TryGetWindowsModifierVirtualKey(KeyLabel label, out ushort virtualKey)
  {
    virtualKey = 0;
    if (System.IO.Path.DirectorySeparatorChar != '\\')
      return false;

    switch (label)
    {
      case KeyLabel.LShift:
        virtualKey = 0xA0;
        return true;
      case KeyLabel.RShift:
        virtualKey = 0xA1;
        return true;
      case KeyLabel.LControl:
        virtualKey = 0xA2;
        return true;
      case KeyLabel.RControl:
        virtualKey = 0xA3;
        return true;
      case KeyLabel.LAlt:
        virtualKey = 0xA4;
        return true;
      case KeyLabel.RAlt:
        virtualKey = 0xA5;
        return true;
      default:
        return false;
    }
  }

  private static MethodInfo _hookCallback;
  private static bool _hookCallbackResolved;

  private static MethodInfo ResolveHookCallback()
  {
    if (_hookCallbackResolved)
      return _hookCallback;
    _hookCallbackResolved = true;
    _hookCallback = typeof(SkyHookManager).GetMethod(
      "HookCallback",
      BindingFlags.Instance | BindingFlags.NonPublic,
      null,
      new[] { typeof(SkyHookEvent) },
      null
    );
    if (_hookCallback == null)
      RenderLog.Warn("SkyHookManager.HookCallback not found; injecting at KeyUpdated instead.");
    return _hookCallback;
  }

  private static bool ResolveFields()
  {
    if (_fieldsResolved)
      return _fieldsUsable;
    _fieldsResolved = true;

    // SkyHookEvent is a readonly struct with no public constructor; reflection on a boxed value is
    // the layout-agnostic way to build one.
    Type eventType = typeof(SkyHookEvent);
    _timeSec = eventType.GetField("TimeSec");
    _timeSubsecNano = eventType.GetField("TimeSubsecNano");
    _type = eventType.GetField("Type");
    _label = eventType.GetField("Label");
    _key = eventType.GetField("Key");

    _fieldsUsable = _timeSec != null && _timeSubsecNano != null && _type != null && _label != null && _key != null;
    if (!_fieldsUsable)
      RenderLog.Warn("SkyHookEvent fields could not be resolved; replayed keys will not reach input consumers.");
    return _fieldsUsable;
  }
}
