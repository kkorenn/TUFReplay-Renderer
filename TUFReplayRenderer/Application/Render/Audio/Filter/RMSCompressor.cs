#nullable enable
using System;
using System.Runtime.CompilerServices;

namespace TUFReplayRenderer.Application.Render.Audio.Filter
{
  // ReSharper disable once InconsistentNaming
  public class RMSCompressor : IAudioFilter
  {
    private float _thresholdDb = -20f;
    private float _ratio = 4f;
    private float _attackTime = 0.010f; // in seconds
    private float _releaseTime = 0.100f; // in seconds
    private float _kneeDb = 5f; // in dB
    private float _rmsWindowTime = 0.030f; // in seconds
    private float _lookAheadTime; // in seconds
    private int _lookAheadSamples;

    private int _sampleRate = 44100;
    private int _channels = 2;

    private float _attackCoef;
    private float _releaseCoef;
    private float _rmsCoef;

    // Per-channel state
    private float[] _rmsState = Array.Empty<float>(); // running mean-square (MS) state
    private float[] _gainState = Array.Empty<float>(); // smoothed gain reduction in dB
    private float[][] _delayBuffers = Array.Empty<float[]>(); // circular delay buffers
    private int[] _delayWriteIndices = Array.Empty<int>();

    public float LookAheadTime
    {
      get => _lookAheadTime;
      set
      {
        _lookAheadTime = Math.Max(0f, Math.Min(1.0f, value));
        UpdateLookAheadSettings();
      }
    }

    public double Latency => _lookAheadTime;

    public float ThresholdDb
    {
      get => _thresholdDb;
      set => _thresholdDb = value;
    }

    public float Ratio
    {
      get => _ratio;
      set => _ratio = Math.Max(1f, value);
    }

    public float AttackTime
    {
      get => _attackTime;
      set
      {
        _attackTime = Math.Max(0.0001f, value);
        UpdateAttackCoef();
      }
    }

    public float ReleaseTime
    {
      get => _releaseTime;
      set
      {
        _releaseTime = Math.Max(0.0001f, value);
        UpdateReleaseCoef();
      }
    }

    public float KneeDb
    {
      get => _kneeDb;
      set => _kneeDb = Math.Max(0f, value);
    }

    public float RmsWindowTime
    {
      get => _rmsWindowTime;
      set
      {
        _rmsWindowTime = Math.Max(0.0001f, value);
        UpdateRmsCoef();
      }
    }

    public void Initialize(int sampleRate, int channels)
    {
      _sampleRate = sampleRate;
      _channels = channels;

      UpdateAttackCoef();
      UpdateReleaseCoef();
      UpdateRmsCoef();
      UpdateLookAheadSettings();

      _rmsState = new float[channels];
      _gainState = new float[channels];

      Reset();
    }

    private void UpdateLookAheadSettings()
    {
      _lookAheadSamples = (int)Math.Round(_sampleRate * _lookAheadTime);
      ReallocateDelayBuffers();
    }

    private void ReallocateDelayBuffers()
    {
      if (_channels <= 0)
        return;

      _delayBuffers = new float[_channels][];
      _delayWriteIndices = new int[_channels];

      for (int i = 0; i < _channels; i++)
      {
        _delayBuffers[i] = _lookAheadSamples > 0 ? new float[_lookAheadSamples] : Array.Empty<float>();
        _delayWriteIndices[i] = 0;
      }
    }

    private void UpdateAttackCoef()
    {
      _attackCoef = (float)Math.Exp(-1.0 / (_sampleRate * _attackTime));
    }

    private void UpdateReleaseCoef()
    {
      _releaseCoef = (float)Math.Exp(-1.0 / (_sampleRate * _releaseTime));
    }

    private void UpdateRmsCoef()
    {
      _rmsCoef = (float)Math.Exp(-1.0 / (_sampleRate * _rmsWindowTime));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float ProcessSample(float sample, int channel)
    {
      if (channel < 0 || channel >= _channels)
      {
        return sample;
      }

      // Calculate running Mean-Square (MS) level
      // v_c[n] = alpha * v_c[n-1] + (1 - alpha) * x^2[n]
      float sq = sample * sample;
      _rmsState[channel] = _rmsCoef * _rmsState[channel] + (1.0f - _rmsCoef) * sq;

      float rmsSq = Math.Max(0f, _rmsState[channel]);
      // Convert to dB. 10 * log10(rmsSq) is equivalent to 20 * log10(sqrt(rmsSq))
      float rmsDb = 10.0f * (float)Math.Log10(rmsSq + 1e-12f);

      // Calculate static envelope curve (gain reduction in dB)
      float gStatic = 0f;
      if (_kneeDb > 0f)
      {
        if (rmsDb > _thresholdDb + _kneeDb * 0.5f)
        {
          gStatic = (1f / _ratio - 1f) * (rmsDb - _thresholdDb);
        }
        else if (rmsDb >= _thresholdDb - _kneeDb * 0.5f)
        {
          float diff = rmsDb - _thresholdDb + _kneeDb * 0.5f;
          gStatic = (1f / _ratio - 1f) * diff * diff / (2f * _kneeDb);
        }
      }
      else
      {
        if (rmsDb > _thresholdDb)
        {
          gStatic = (1f / _ratio - 1f) * (rmsDb - _thresholdDb);
        }
      }

      // Apply ballistics (attack and release) on the dB gain reduction
      float lastGain = _gainState[channel];
      float targetGain = gStatic;

      // Note: Since targetGain and lastGain are <= 0 (gain reduction in dB),
      // targetGain < lastGain means target gain reduction is larger (more negative),
      // so we are in the attack phase.
      float coef = targetGain < lastGain ? _attackCoef : _releaseCoef;
      float currentGain = coef * lastGain + (1f - coef) * targetGain;
      _gainState[channel] = currentGain;

      // Convert back to linear gain multiplier
      float linearGain = (float)Math.Pow(10.0, currentGain / 20.0);

      if (_lookAheadSamples > 0 && _delayBuffers.Length > channel && _delayBuffers[channel].Length > 0)
      {
        var buffer = _delayBuffers[channel];
        int writeIdx = _delayWriteIndices[channel];
        float delayedSample = buffer[writeIdx];
        buffer[writeIdx] = sample;
        _delayWriteIndices[channel] = (writeIdx + 1) % _lookAheadSamples;
        return delayedSample * linearGain;
      }

      return sample * linearGain;
    }

    public void ProcessBlock(Span<float> buffer)
    {
      for (var i = 0; i < buffer.Length; i++)
      {
        int channel = i % _channels;
        buffer[i] = ProcessSample(buffer[i], channel);
      }
    }

    public void Reset()
    {
      Array.Clear(_rmsState, 0, _rmsState.Length);
      Array.Clear(_gainState, 0, _gainState.Length);

      for (int i = 0; i < _delayBuffers.Length; i++)
      {
        Array.Clear(_delayBuffers[i], 0, _delayBuffers[i].Length);
      }
      Array.Clear(_delayWriteIndices, 0, _delayWriteIndices.Length);
    }
  }
}
