#nullable enable
using System;
using TUFReplayRenderer.Infrastructure.Render;

namespace TUFReplayRenderer.Application.Render.Audio
{
  public class AudioSyncCalculator
  {
    private readonly long _sampleRate;
    private readonly (long Numerator, long Denominator) _fpsRational;

    // ReSharper disable once NotAccessedField.Local
    private long _currentSample;
    private long _remainder;

    public AudioSyncCalculator(long sampleRate, double fps)
    {
      _sampleRate = sampleRate;
      _fpsRational = RationalUtil.Double2MaxPrecisionInt(fps);

      // NTSC
      if (Math.Abs(fps - 29.97) < 0.001)
        _fpsRational = (30000, 1001);
      if (Math.Abs(fps - 59.94) < 0.001)
        _fpsRational = (60000, 1001);
      if (Math.Abs(fps - 23.976) < 0.001 || Math.Abs(fps - 23.98) < 0.001)
        _fpsRational = (24000, 1001);
    }

    public void Reset()
    {
      _currentSample = 0;
      _remainder = 0;
    }

    public int GetNextFrameSamples()
    {
      var numerator = _sampleRate * _fpsRational.Denominator + _remainder;
      var samplesNeeded = numerator / _fpsRational.Numerator;

      _remainder = numerator % _fpsRational.Numerator;
      _currentSample += samplesNeeded;
      return (int)samplesNeeded;
    }
  }
}
