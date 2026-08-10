#nullable enable
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using TUFReplayRenderer.Application.Render.Audio.Filter;
using TUFReplayRenderer.Infrastructure.Render;
using UnityEngine;

namespace TUFReplayRenderer.Application.Render.Audio
{
  public class AudioClipCache
  {
    private readonly Dictionary<string, CachedClipData> _cache = new();

    public class CachedClipData
    {
      public float[] Samples = null!; // Pre-resampled to target channels and sample rate
      public double Duration;
    }

    public CachedClipData? GetOrCache(string clipName, int targetChannels, int targetSampleRate, bool allowLoad = true)
    {
      if (string.IsNullOrEmpty(clipName))
        return null;

      lock (_cache)
      {
        if (_cache.TryGetValue(clipName, out var cached))
          return cached;

        if (!allowLoad)
          return null;

        if (!AudioManager.Instance)
          return null;

        var clip = AudioManager.Instance.FindOrLoadAudioClip(clipName);
        if (!clip)
          return null;

        try
        {
          var totalSamples = clip.samples;
          var channels = clip.channels;
          var rawSamples = new float[totalSamples * channels];
          clip.GetData(rawSamples, 0);

          // Auto-resample during cache generation
          var resampled = AudioResampler.Resample(
            rawSamples,
            channels,
            clip.frequency,
            targetChannels,
            targetSampleRate
          );

          var data = new CachedClipData
          {
            Samples = resampled,
            Duration = (double)resampled.Length / (targetChannels * targetSampleRate),
          };

          // Diagnostic: a silent peak here means GetData() returned zeros (compressed or streamed
          // clip), which no amount of downstream mixing can recover from.
          float peak = 0f;
          int scan = Math.Min(resampled.Length, targetSampleRate * targetChannels * 5);
          for (var i = 0; i < scan; i++)
          {
            float magnitude = Math.Abs(resampled[i]);
            if (magnitude > peak)
              peak = magnitude;
          }
          RenderLog.Info(
            $"[AudioClipCache] {clipName}: loadType={clip.loadType}, srcSamples={totalSamples}, duration={data.Duration:F2}s, peak={peak:F4}"
          );

          _cache[clipName] = data;
          return data;
        }
        catch (Exception e)
        {
          RenderLog.Error($"[AudioClipCache] Failed to cache and resample clip {clipName}: {e}");
          return null;
        }
      }
    }

    /// <summary>
    /// Caches a clip supplied directly, keyed by instance id. SFX (death sound, explosion,
    /// applause) arrive as an AudioClip that AudioManager.FindOrLoadAudioClip cannot resolve by
    /// name, so they cannot go through GetOrCache.
    /// </summary>
    public CachedClipData? GetOrCacheDirect(AudioClip clip, int targetChannels, int targetSampleRate)
    {
      if (clip == null)
        return null;

      string key = "instance:" + clip.GetInstanceID();
      lock (_cache)
      {
        if (_cache.TryGetValue(key, out var cached))
          return cached;

        try
        {
          var totalSamples = clip.samples;
          var channels = clip.channels;
          if (totalSamples <= 0 || channels <= 0)
            return null;

          var rawSamples = new float[totalSamples * channels];
          clip.GetData(rawSamples, 0);
          var resampled = AudioResampler.Resample(
            rawSamples,
            channels,
            clip.frequency,
            targetChannels,
            targetSampleRate
          );

          var data = new CachedClipData
          {
            Samples = resampled,
            Duration = (double)resampled.Length / (targetChannels * targetSampleRate),
          };
          _cache[key] = data;
          RenderLog.Info(
            $"[AudioClipCache] direct {clip.name}: loadType={clip.loadType}, duration={data.Duration:F2}s"
          );
          return data;
        }
        catch (Exception e)
        {
          RenderLog.Error($"[AudioClipCache] Failed to cache direct clip {clip.name}: {e.Message}");
          return null;
        }
      }
    }

    public void Clear()
    {
      lock (_cache)
      {
        _cache.Clear();
      }
    }
  }

  public class OfflineAudioMixer
  {
    public class SoundEvent
    {
      public string ClipName = null!;
      public double StartTime; // In seconds relative to dspTimeBase
      public double EndTime; // In seconds relative to dspTimeBase (-1 if one-shot)
      public float Volume;
      public float Pitch;

      // Pre-resolved samples for SFX supplied as an AudioClip (death sound, explosion, applause),
      // which have no cacheable name. When set, MixEffectSounds uses this instead of a name lookup.
      public AudioClipCache.CachedClipData? DirectClip;
    }

    private readonly List<SoundEvent> _activeEvents = new();
    private int _totalRegisteredEvents;
    private int _totalMixedEvents;
    private int _totalDroppedEvents;
    private double _minEventStart = double.MaxValue;
    private double _maxEventStart = double.MinValue;
    private ConditionalWeakTable<AudioSource, SoundEvent> _sourceMappings = new();
    private readonly object _lock = new();

    // Target Audio Settings
    private readonly int _targetChannels;
    private readonly int _targetSampleRate;
    private const float ClipVolume = .5f;

    // Audio Filter Settings
    private readonly LinkedList<IAudioFilter> _filters = new();

    // BGM Cached Samples (Pre-resampled)
    private float[]? _bgmSamples;
    private double _bgmDuration;

    // Clip Cache
    private readonly AudioClipCache _clipCache = new();
    private bool _isPrimed;

    // Microphone track (TUFReplay addition). Mixed pre-filter so the master compressor and
    // limiter see the combined signal, exactly like the game's own sounds.
    private MicrophoneRenderSource? _microphone;
    private const float MicrophoneVolume = .8f;

    // Per-source trims, 1.0 = the level this mixer would use on its own.
    private float _musicLevel = 1f;
    private float _hitsoundLevel = 1f;
    private float _microphoneLevel = 1f;

    /// <summary>Applies the render's per-source volume trims.</summary>
    public void SetLevels(float music, float hitsound, float microphone)
    {
      lock (_lock)
      {
        _musicLevel = Math.Max(0f, music);
        _hitsoundLevel = Math.Max(0f, hitsound);
        _microphoneLevel = Math.Max(0f, microphone);
      }
      RenderLog.Info(
        $"Mixer levels applied: music={_musicLevel:P0}, hitsound={_hitsoundLevel:P0}, microphone={_microphoneLevel:P0}"
      );
    }

    /// <summary>Attaches the run's microphone recording, or null to render without one.</summary>
    public void SetMicrophoneSource(MicrophoneRenderSource? microphone)
    {
      lock (_lock)
      {
        _microphone = microphone;
      }
    }

    public bool HasMicrophone
    {
      get
      {
        lock (_lock)
          return _microphone != null;
      }
    }

    /// <summary>
    /// Builds the master chain applied to the final mix. Shared with the balance preview so what
    /// the user auditions is mastered exactly like the render.
    /// </summary>
    public static LinkedList<IAudioFilter> CreateMasterChain(int targetSampleRate, int targetChannels)
    {
      var filters = new LinkedList<IAudioFilter>();

      var rmsCompressor = new RMSCompressor
      {
        ThresholdDb = -10,
        Ratio = 2,
        AttackTime = 0.1f,
        ReleaseTime = 0.1f,
        KneeDb = 10,
        RmsWindowTime = 0.02f,
      };
      rmsCompressor.Initialize(targetSampleRate, targetChannels);
      filters.AddLast(rmsCompressor);

      var limiter = new RMSCompressor
      {
        ThresholdDb = 0,
        Ratio = float.PositiveInfinity,
        AttackTime = 0f,
        ReleaseTime = 0.05f,
        KneeDb = 0,
        RmsWindowTime = 0.02f,
        LookAheadTime = 0.01f,
      };
      limiter.Initialize(targetSampleRate, targetChannels);
      filters.AddLast(limiter);

      var hardClip = new HardClip();
      hardClip.Initialize(targetSampleRate, targetChannels);
      filters.AddLast(hardClip);

      var truePeakSafetyGain = new Gain { Level = -1f };
      truePeakSafetyGain.Initialize(targetSampleRate, targetChannels);
      filters.AddLast(truePeakSafetyGain);

      return filters;
    }

    public OfflineAudioMixer(int targetChannels, int targetSampleRate)
    {
      _targetChannels = targetChannels;
      _targetSampleRate = targetSampleRate;

      var rmsCompressor = new RMSCompressor
      {
        ThresholdDb = -10,
        Ratio = 2,
        AttackTime = 0.1f,
        ReleaseTime = 0.1f,
        KneeDb = 10,
        RmsWindowTime = 0.02f,
      };
      rmsCompressor.Initialize(_targetSampleRate, _targetChannels);
      _filters.AddLast(rmsCompressor);

      var limiter = new RMSCompressor
      {
        ThresholdDb = 0,
        Ratio = float.PositiveInfinity,
        AttackTime = 0f,
        ReleaseTime = 0.05f,
        KneeDb = 0,
        RmsWindowTime = 0.02f,
        LookAheadTime = 0.01f,
      };
      limiter.Initialize(_targetSampleRate, _targetChannels);
      _filters.AddLast(limiter);

      var hardClip = new HardClip();
      hardClip.Initialize(_targetSampleRate, _targetChannels);
      _filters.AddLast(hardClip);

      var truePeakSafetyGain = new Gain { Level = -1f };
      truePeakSafetyGain.Initialize(_targetSampleRate, _targetChannels);
      _filters.AddLast(truePeakSafetyGain);
    }

    public void InitializeBGM(AudioClip? clip)
    {
      lock (_lock)
      {
        if (!clip)
        {
          _bgmSamples = null;
          _bgmDuration = 0;
          return;
        }

        try
        {
          int originalSamples = clip.samples;
          int originalChannels = clip.channels;
          float[] rawSamples = new float[originalSamples * originalChannels];
          clip.GetData(rawSamples, 0);

          // Pre-resample the BGM to the target format
          _bgmSamples = AudioResampler.Resample(
            rawSamples,
            originalChannels,
            clip.frequency,
            _targetChannels,
            _targetSampleRate
          );
          _bgmDuration = (double)_bgmSamples.Length / (_targetChannels * _targetSampleRate);

          float bgmPeak = 0f;
          int bgmScan = Math.Min(_bgmSamples.Length, _targetSampleRate * _targetChannels * 10);
          for (var i = 0; i < bgmScan; i++)
          {
            float magnitude = Math.Abs(_bgmSamples[i]);
            if (magnitude > bgmPeak)
              bgmPeak = magnitude;
          }
          RenderLog.Info(
            $"[OfflineAudioMixer] Initialized resampled BGM: {clip.name}, Samples: {_bgmSamples.Length / _targetChannels}, TargetFreq: {_targetSampleRate}Hz, TargetChannels: {_targetChannels}, Peak: {bgmPeak:F4}"
          );
        }
        catch (Exception e)
        {
          RenderLog.Error($"[OfflineAudioMixer] Failed to initialize and resample BGM: {e}");
          _bgmSamples = null;
          _bgmDuration = 0;
        }
      }
    }

    /// <summary>
    /// Mirrors AudioManager.StopAllSounds on the offline timeline: the game kills every scheduled
    /// hitsound source on death, on quit, and right before PlayHitTimes re-schedules. Events that
    /// have not started by <paramref name="now"/> are removed outright; events still sounding are
    /// truncated to <paramref name="now"/>. One-shots that already finished are left untouched —
    /// giving them an end time would turn them into loops.
    /// </summary>
    public void CancelPendingSoundEvents(double now)
    {
      lock (_lock)
      {
        int removed = 0;
        int truncated = 0;
        for (var i = _activeEvents.Count - 1; i >= 0; i--)
        {
          var ev = _activeEvents[i];
          if (ev.StartTime >= now)
          {
            _activeEvents.RemoveAt(i);
            removed++;
            continue;
          }

          if (ev.EndTime >= 0)
          {
            if (ev.EndTime > now)
            {
              ev.EndTime = now;
              truncated++;
            }
            continue;
          }

          var clipData =
            ev.DirectClip ?? _clipCache.GetOrCache(ev.ClipName, _targetChannels, _targetSampleRate, allowLoad: false);
          var effectivePitch = ev.Pitch > 0 ? ev.Pitch : 1f;
          bool stillSounding = clipData == null || now < ev.StartTime + clipData.Duration / effectivePitch;
          if (stillSounding)
          {
            ev.EndTime = now;
            truncated++;
          }
        }
        _sourceMappings.Clear();
        if (removed > 0 || truncated > 0)
          RenderLog.Info(
            $"[Mixer] StopAllSounds at {now:F3}s: removed {removed} pending, truncated {truncated} events."
          );
      }
    }

    private double _bgmCutTime = -1.0;

    /// <summary>Silences the music from <paramref name="time"/> on (the song stopping on death).</summary>
    public void CutBGM(double time)
    {
      lock (_lock)
      {
        if (_bgmCutTime < 0 || time < _bgmCutTime)
        {
          _bgmCutTime = time;
          RenderLog.Info($"[Mixer] BGM cut at {time:F3}s.");
        }
      }
    }

    public void CacheClip(string clipName)
    {
      _clipCache.GetOrCache(clipName, _targetChannels, _targetSampleRate, allowLoad: true);
    }

    public void RegisterSoundEvent(
      AudioSource? source,
      string clipName,
      double startTime,
      double endTime,
      float volume,
      float pitch
    )
    {
      lock (_lock)
      {
        var ev = new SoundEvent
        {
          ClipName = clipName,
          StartTime = startTime,
          EndTime = endTime,
          Volume = volume,
          Pitch = pitch,
        };
        _activeEvents.Add(ev);
        _totalRegisteredEvents++;
        if (_totalRegisteredEvents <= 5)
          RenderLog.Info(
            $"[Mixer] event #{_totalRegisteredEvents}: {clipName} start={startTime:F3}s end={endTime:F3} vol={volume:F2} pitch={pitch:F2}"
          );
        if (startTime < _minEventStart)
          _minEventStart = startTime;
        if (startTime > _maxEventStart)
          _maxEventStart = startTime;

        if (!source)
          return;
        _sourceMappings.Remove(source);
        _sourceMappings.Add(source, ev);
      }
    }

    /// <summary>
    /// Registers a one-shot SFX supplied directly as a clip (played via AudioSource.PlayOneShot,
    /// e.g. scrSfx.PlaySfx). Cached and mixed on the effects path, so it lands in the Hitsounds
    /// stem and the master mix.
    /// </summary>
    public void RegisterSfxClip(AudioClip clip, double startTime, float volume, float pitch)
    {
      if (clip == null)
        return;

      var clipData = _clipCache.GetOrCacheDirect(clip, _targetChannels, _targetSampleRate);
      if (clipData == null)
        return;

      lock (_lock)
      {
        var ev = new SoundEvent
        {
          ClipName = "sfx:" + clip.name,
          StartTime = startTime,
          EndTime = -1.0,
          Volume = volume,
          Pitch = pitch > 0f ? pitch : 1f,
          DirectClip = clipData,
        };
        _activeEvents.Add(ev);
        _totalRegisteredEvents++;
        if (startTime < _minEventStart)
          _minEventStart = startTime;
        if (startTime > _maxEventStart)
          _maxEventStart = startTime;
      }
    }

    public void RegisterSoundEvent(string clipName, double startTime, double endTime, float volume, float pitch)
    {
      RegisterSoundEvent(null, clipName, startTime, endTime, volume, pitch);
    }

    public bool TryGetSoundEvent(AudioSource? source, out SoundEvent ev)
    {
      if (source == null)
      {
        ev = null!;
        return false;
      }
      lock (_lock)
      {
        return _sourceMappings.TryGetValue(source, out ev);
      }
    }

    /// <summary>
    /// AudioSource 피치 변경을 매핑된 이벤트에 반영합니다.
    /// 풀링된 소스의 매핑이 이미 재생이 끝난 이전 이벤트를 가리키는 경우
    /// (피치 설정 → _Play 순서로 호출될 때) 소급 적용을 막기 위해 만료 여부를 판정합니다.
    /// </summary>
    public void UpdateSourcePitch(AudioSource source, float pitch, double now)
    {
      if (!TryGetSoundEvent(source, out var ev))
        return;

      var effectivePitch = ev.Pitch > 0 ? ev.Pitch : 1f;
      bool ended;
      if (ev.EndTime >= 0)
      {
        ended = now >= ev.EndTime;
      }
      else
      {
        var clipData = _clipCache.GetOrCache(ev.ClipName, _targetChannels, _targetSampleRate, allowLoad: false);
        ended = clipData != null && now >= ev.StartTime + clipData.Duration / effectivePitch;
      }

      if (ended)
      {
        // 끝난 이벤트: 매핑만 제거하고 피치는 건드리지 않습니다.
        RemoveSourceMapping(source);
        return;
      }

      if (pitch > 0)
        ev.Pitch = pitch;
    }

    public bool RemoveSourceMapping(AudioSource source)
    {
      lock (_lock)
      {
        return _sourceMappings.Remove(source);
      }
    }

    public void Reset()
    {
      lock (_lock)
      {
        _activeEvents.Clear();
        _sourceMappings = new ConditionalWeakTable<AudioSource, SoundEvent>();
        _bgmSamples = null;
        _bgmDuration = 0;
        _clipCache.Clear();
        _microphone = null;
        _isPrimed = false;
        _reportedClipping = false;
        foreach (var filter in _filters)
        {
          filter.Reset();
        }
      }
    }

    private void PrimeFilters(double totalLatency, double dspTimeBase)
    {
      int latencySamples = (int)Math.Round(totalLatency * _targetSampleRate);
      if (latencySamples <= 0)
        return;

      // Rent temporary buffer to prime filters with initial audio
      float[] tempBuffer = System.Buffers.ArrayPool<float>.Shared.Rent(latencySamples * _targetChannels);
      try
      {
        Array.Clear(tempBuffer, 0, latencySamples * _targetChannels);

        // Mix starting from -totalLatency to 0
        MixBGM(tempBuffer, latencySamples, -totalLatency, dspTimeBase, totalLatency);
        MixEffectSounds(tempBuffer, latencySamples, -totalLatency, totalLatency);

        // Feed the buffer into filters to prime circular delay buffers
        foreach (var filter in _filters)
        {
          filter.ProcessBlock(tempBuffer.AsSpan(0, latencySamples * _targetChannels));
        }
      }
      finally
      {
        System.Buffers.ArrayPool<float>.Shared.Return(tempBuffer);
      }
    }

    /// <summary>
    /// Timeline position of one rendered video frame on the replay clock, captured on the main
    /// thread alongside the frame itself so the encoder thread never has to guess.
    /// </summary>
    public readonly struct ReplayFrameTiming
    {
      public readonly bool HasReplayTime;
      public readonly long ReplayTimeUs;
      public readonly double GameplayRate;
      public readonly long? WonTimeUs;

      public ReplayFrameTiming(bool hasReplayTime, long replayTimeUs, double gameplayRate, long? wonTimeUs)
      {
        HasReplayTime = hasReplayTime;
        ReplayTimeUs = replayTimeUs;
        GameplayRate = gameplayRate;
        WonTimeUs = wonTimeUs;
      }
    }

    public void GenerateAudioForFrame(float[] mixedBuffer, int samplesNeeded, double frameStartTime, double dspTimeBase)
    {
      GenerateAudioForFrame(mixedBuffer, samplesNeeded, frameStartTime, dspTimeBase, default);
    }

    /// <summary>
    /// Produces one frame of audio as separate stems plus the mastered mix.
    ///
    /// The stems (music, hit sounds, microphone) carry their volume trims but no master chain, so
    /// they stay dry for rebalancing in an editor. The mix is their sum through the master chain —
    /// identical to what <see cref="GenerateAudioForFrame(float[],int,double,double,ReplayFrameTiming)"/>
    /// produces, so the default playback track sounds the same whether or not stems are written.
    /// </summary>
    public void GenerateAudioStems(
      float[] mixBuffer,
      float[] musicBuffer,
      float[] hitsoundBuffer,
      float[]? microphoneBuffer,
      int samplesNeeded,
      double frameStartTime,
      double dspTimeBase,
      ReplayFrameTiming timing
    )
    {
      int sampleCount = samplesNeeded * _targetChannels;
      Array.Clear(mixBuffer, 0, sampleCount);
      Array.Clear(musicBuffer, 0, sampleCount);
      Array.Clear(hitsoundBuffer, 0, sampleCount);
      if (microphoneBuffer != null)
        Array.Clear(microphoneBuffer, 0, sampleCount);

      lock (_lock)
      {
        double totalLatency = 0;
        foreach (var filter in _filters)
          totalLatency += filter.Latency;

        if (!_isPrimed && totalLatency > 0)
        {
          PrimeFilters(totalLatency, dspTimeBase);
          _isPrimed = true;
        }

        MixBGM(musicBuffer, samplesNeeded, frameStartTime, dspTimeBase, totalLatency);
        MixEffectSounds(hitsoundBuffer, samplesNeeded, frameStartTime, totalLatency);

        if (_microphone != null && microphoneBuffer != null && timing.HasReplayTime)
        {
          var latencyUs = (long)(totalLatency * 1_000_000d * NormalizeRate(timing.GameplayRate));
          _microphone.Mix(
            microphoneBuffer,
            samplesNeeded,
            _targetChannels,
            _targetSampleRate,
            timing.ReplayTimeUs + latencyUs,
            timing.GameplayRate,
            timing.WonTimeUs,
            MicrophoneVolume * _microphoneLevel
          );
        }

        for (var i = 0; i < sampleCount; i++)
        {
          float sum = musicBuffer[i] + hitsoundBuffer[i];
          if (microphoneBuffer != null)
            sum += microphoneBuffer[i];
          mixBuffer[i] = sum;
        }

        foreach (var filter in _filters)
          filter.ProcessBlock(mixBuffer.AsSpan(0, sampleCount));
      }
    }

    public void GenerateAudioForFrame(
      float[] mixedBuffer,
      int samplesNeeded,
      double frameStartTime,
      double dspTimeBase,
      ReplayFrameTiming timing
    )
    {
      // mixedBuffer size must be at least samplesNeeded * _targetChannels
      Array.Clear(mixedBuffer, 0, samplesNeeded * _targetChannels);

      lock (_lock)
      {
        double totalLatency = 0;
        foreach (var filter in _filters)
        {
          totalLatency += filter.Latency;
        }

        // Prime filters on first frame execution if latency exists
        if (!_isPrimed && totalLatency > 0)
        {
          PrimeFilters(totalLatency, dspTimeBase);
          _isPrimed = true;
        }

        // 1. Mix BGM
        MixBGM(mixedBuffer, samplesNeeded, frameStartTime, dspTimeBase, totalLatency);

        // 2. Mix Hitsounds/Effect Sounds
        MixEffectSounds(mixedBuffer, samplesNeeded, frameStartTime, totalLatency);

        // 3. Mix the run's microphone recording, if one was captured.
        // The lookahead latency shift that BGM and hitsounds get is applied here through
        // the replay clock instead: the timing snapshot already carries the frame's replay
        // position, so the microphone is delayed by the same amount in replay time.
        if (_microphone != null && timing.HasReplayTime)
        {
          var latencyUs = (long)(totalLatency * 1_000_000d * NormalizeRate(timing.GameplayRate));
          _microphone.Mix(
            mixedBuffer,
            samplesNeeded,
            _targetChannels,
            _targetSampleRate,
            timing.ReplayTimeUs + latencyUs,
            timing.GameplayRate,
            timing.WonTimeUs,
            MicrophoneVolume * _microphoneLevel
          );
        }

        foreach (var filter in _filters)
          filter.ProcessBlock(mixedBuffer.AsSpan(0, samplesNeeded * _targetChannels));

        // Clipping check. Logging per sample would emit tens of thousands of lines per
        // frame and stall the encoder thread, so it is aggregated, and then reported only
        // once per session rather than once per frame.
        var clippedCount = 0;
        for (var i = 0; i < samplesNeeded * _targetChannels; i++)
        {
          if (Math.Abs(mixedBuffer[i]) > 1)
            clippedCount++;
        }
        if (clippedCount > 0 && !_reportedClipping)
        {
          _reportedClipping = true;
          RenderLog.Warn(
            $"Audio clipping detected ({clippedCount} samples past 1.0 in one frame). Later occurrences are not logged."
          );
        }
      }
    }

    private bool _reportedClipping;

    /// <summary>One-line health summary for the render log, read at session end.</summary>
    public string DescribeState()
    {
      lock (_lock)
      {
        long bgmSampleFrames = _bgmSamples == null ? 0 : _bgmSamples.Length / Math.Max(1, _targetChannels);
        return "bgmFrames="
          + bgmSampleFrames
          + ", bgmDuration="
          + _bgmDuration.ToString("F2")
          + "s, eventsRegistered="
          + _totalRegisteredEvents
          + ", eventsMixed="
          + _totalMixedEvents
          + ", eventsDroppedNoClip="
          + _totalDroppedEvents
          + ", pendingEvents="
          + _activeEvents.Count
          + ", eventStartRange="
          + (_totalRegisteredEvents > 0 ? _minEventStart.ToString("F2") + ".." + _maxEventStart.ToString("F2") : "none")
          + ", hasSnapshot="
          + _hasSnapshot
          + ", microphone="
          + (_microphone != null);
      }
    }

    // ── 메인 스레드 스냅샷 ──
    // 인코더 스레드가 Unity 객체(scrConductor, scnGame)를 직접 읽으면 파괴된 객체 접근/
    // 메인 스레드 전용 바인딩 예외가 발생할 수 있으므로, 메인 스레드가 매 프레임 갱신합니다.
    // (64비트에서 정렬된 double 쓰기는 원자적이므로 개별 필드 티어링 위험은 무시 가능한 수준)
    private volatile bool _hasSnapshot;
    private double _snapPitch = 1.0;
    private double _snapDspTimeSong;
    private double _snapCountdownOffset;
    private float _snapLevelVolume = 100f;

    /// <summary>메인 스레드(렌더 프레임마다)에서 호출하여 인코더 스레드용 상태를 갱신합니다.</summary>
    public void UpdateSnapshotFromMainThread()
    {
      var conductor = scrConductor.instance;
      if (!conductor || !conductor.song)
        return;

      double pitch = conductor.song.pitch;
      if (pitch <= 0)
        pitch = 1.0;
      _snapPitch = pitch;
      _snapDspTimeSong = conductor.dspTimeSong;
      _snapCountdownOffset = conductor.separateCountdownTime
        ? conductor.crotchetAtStart / pitch * conductor.adjustedCountdownTicks
        : 0.0;
      // Replay renders run inside the editor's play mode, where scnGame.instance is null and
      // the level volume lives on the editor's levelData instead.
      _snapLevelVolume = ResolveLevelVolume();
      _hasSnapshot = true;
    }

    private static float ResolveLevelVolume()
    {
      try
      {
        if (scnGame.instance && scnGame.instance.levelData != null)
          return scnGame.instance.levelData.volume;
        if (scnEditor.instance && scnEditor.instance.levelData != null)
          return scnEditor.instance.levelData.volume;
      }
      catch
      {
        // Fall through to the neutral default rather than killing the snapshot.
      }
      return 100f;
    }

    private static double NormalizeRate(double rate) =>
      rate > 0d && !double.IsNaN(rate) && !double.IsInfinity(rate) ? rate : 1d;

    private void MixBGM(
      float[] mixedBuffer,
      int samplesNeeded,
      double frameStartTime,
      double dspTimeBase,
      double totalLatency
    )
    {
      if (_bgmSamples == null || _bgmSamples.Length == 0)
        return;

      // 인코더 스레드에서 Unity 객체(conductor/scnGame)를 직접 읽으면 안 되므로
      // 메인 스레드가 UpdateSnapshotFromMainThread()로 갱신한 스냅샷 값을 사용합니다.
      if (!_hasSnapshot)
        return;

      double pitch = _snapPitch;
      var dspTimeSong = _snapDspTimeSong;
      var separateCountdownOffset = _snapCountdownOffset;

      // BGM Playback start time relative to dspTimeBase
      var time1 = dspTimeSong + separateCountdownOffset;
      var relativeTime1 = time1 - dspTimeBase;
      var adjustedRelativeTime1 = relativeTime1 - totalLatency;

      var totalSamples = _bgmSamples.Length / _targetChannels;
      double adjustedCutTime = _bgmCutTime >= 0 ? _bgmCutTime - totalLatency : double.MaxValue;

      for (int s = 0; s < samplesNeeded; s++)
      {
        double sampleTime = frameStartTime + (double)s / _targetSampleRate;
        if (sampleTime >= adjustedCutTime)
          break;
        double clipTime = (sampleTime - adjustedRelativeTime1) * pitch;

        if (clipTime >= 0 && clipTime < _bgmDuration)
        {
          double sampleIdxDouble = clipTime * _targetSampleRate;
          long idxLow = (long)Math.Floor(sampleIdxDouble);
          long idxHigh = idxLow + 1;
          float t = (float)(sampleIdxDouble - idxLow);

          if (idxHigh >= totalSamples)
            idxHigh = totalSamples - 1;
          if (idxLow >= totalSamples)
            idxLow = totalSamples - 1;

          for (var c = 0; c < _targetChannels; c++)
          {
            float valLow = _bgmSamples[idxLow * _targetChannels + c];
            float valHigh = _bgmSamples[idxHigh * _targetChannels + c];
            mixedBuffer[s * _targetChannels + c] +=
              (valLow + t * (valHigh - valLow)) * (_snapLevelVolume * 0.01f) * ClipVolume * _musicLevel;
          }
        }
      }
    }

    private void MixEffectSounds(float[] mixedBuffer, int samplesNeeded, double frameStartTime, double totalLatency)
    {
      for (var i = _activeEvents.Count - 1; i >= 0; i--)
      {
        var ev = _activeEvents[i];
        var clipData =
          ev.DirectClip ?? _clipCache.GetOrCache(ev.ClipName, _targetChannels, _targetSampleRate, allowLoad: false);
        if (clipData == null)
        {
          // The clip never made it into the cache on the main thread; the event cannot be mixed.
          _totalDroppedEvents++;
          _activeEvents.RemoveAt(i);
          continue;
        }
        _totalMixedEvents++;

        var isSoundFinished = false;
        var totalSamples = clipData.Samples.Length / _targetChannels;
        double adjustedStartTime = ev.StartTime - totalLatency;
        double adjustedEndTime = ev.EndTime >= 0 ? ev.EndTime - totalLatency : -1.0;

        for (var s = 0; s < samplesNeeded; s++)
        {
          var sampleTime = frameStartTime + (double)s / _targetSampleRate;

          // If it is a looping sound and has reached or exceeded its end time, stop playing.
          if (adjustedEndTime >= 0 && sampleTime >= adjustedEndTime)
          {
            isSoundFinished = true;
            break;
          }

          var elapsed = sampleTime - adjustedStartTime;
          if (elapsed < 0)
            continue; // Sound hasn't started yet

          var soundTime = elapsed * ev.Pitch;

          if (soundTime >= clipData.Duration)
          {
            if (ev.EndTime < 0) // One-shot
            {
              isSoundFinished = true;
              break;
            }

            // Looping
            soundTime %= clipData.Duration;
          }

          // Linear interpolation for pitch shifting in the mixer loop
          double sampleIdxDouble = soundTime * _targetSampleRate;
          long idxLow = (long)Math.Floor(sampleIdxDouble);
          long idxHigh = idxLow + 1;
          float t = (float)(sampleIdxDouble - idxLow);

          if (idxHigh >= totalSamples)
            idxHigh = totalSamples - 1;
          if (idxLow >= totalSamples)
            idxLow = totalSamples - 1;

          for (var c = 0; c < _targetChannels; c++)
          {
            float valLow = clipData.Samples[idxLow * _targetChannels + c];
            float valHigh = clipData.Samples[idxHigh * _targetChannels + c];
            mixedBuffer[s * _targetChannels + c] +=
              (valLow + t * (valHigh - valLow)) * ev.Volume * ClipVolume * 2 / 3 * _hitsoundLevel;
          }
        }

        // Clean up finished sound events (both one-shot and looping)
        if (isSoundFinished)
        {
          _activeEvents.RemoveAt(i);
        }
      }
    }
  }
}
