#nullable enable
using System;

namespace TUFReplayRenderer.Application.Render.Audio.Filter
{
  public interface IAudioFilter
  {
    void Initialize(int sampleRate, int channels);

    float ProcessSample(float sample, int channel);
    void ProcessBlock(Span<float> buffer);

    void Reset();

    double Latency { get; }
  }
}
