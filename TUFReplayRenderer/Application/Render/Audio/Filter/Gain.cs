#nullable enable
using System;

namespace TUFReplayRenderer.Application.Render.Audio.Filter
{
  public class Gain : IAudioFilter
  {
    public float Level = 0f;

    public void Initialize(int sampleRate, int channels) { }

    public float ProcessSample(float sample, int channel) => sample * (float)Math.Pow(10, Level / 20);

    public void ProcessBlock(Span<float> buffer)
    {
      for (var i = 0; i < buffer.Length; i++)
        buffer[i] = ProcessSample(buffer[i], 0);
    }

    public void Reset() { }

    public double Latency => 0.0;
  }
}
