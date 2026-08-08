#nullable enable
using System;
using System.Runtime.CompilerServices;

namespace TUFReplayRenderer.Application.Render.Audio.Filter
{
  public class HardClip : IAudioFilter
  {
    // ReSharper disable once MemberCanBePrivate.Global
    // ReSharper disable once FieldCanBeMadeReadOnly.Global
    // ReSharper disable once ConvertToConstant.Global
    public float Threshold = 1.0f;

    public void Initialize(int sampleRate, int channels) { }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float ProcessSample(float sample, int channel)
    {
      if (sample > Threshold)
        return Threshold;
      if (sample < -Threshold)
        return -Threshold;
      return sample;
    }

    public void ProcessBlock(Span<float> buffer)
    {
      for (var i = 0; i < buffer.Length; i++)
        buffer[i] = ProcessSample(buffer[i], 0);
    }

    public void Reset() { }

    public double Latency => 0.0;
  }
}
