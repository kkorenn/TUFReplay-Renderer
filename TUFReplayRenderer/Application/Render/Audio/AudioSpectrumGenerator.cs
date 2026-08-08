#nullable enable
using System;
using System.Collections.Generic;
using TUFReplayRenderer.Application.Render;
using TUFReplayRenderer.Infrastructure.Render;
using UnityEngine;

namespace TUFReplayRenderer.Application.Render.Audio
{
  public static class AudioSpectrumGenerator
  {
    public static float[]? AudioClipData { get; private set; }
    public static int Channels { get; private set; }
    public static int Frequency { get; private set; }
    public static int TotalSamples { get; private set; }

    private static long _cachedFrame = -1;
    private static readonly Dictionary<int, float[]> CachedSpectrums = new();

    public static void Initialize(AudioClip? clip)
    {
      if (!clip)
      {
        Clear();
        return;
      }

      try
      {
        Channels = clip.channels;
        Frequency = clip.frequency;
        int samplesCount = clip.samples;

        float[] rawSamples = new float[samplesCount * Channels];
        clip.GetData(rawSamples, 0);

        if (Channels == 1)
        {
          AudioClipData = rawSamples;
          TotalSamples = samplesCount;
        }
        else
        {
          AudioClipData = new float[samplesCount];
          TotalSamples = samplesCount;
          for (int i = 0; i < samplesCount; i++)
          {
            float sum = 0f;
            for (int c = 0; c < Channels; c++)
            {
              sum += rawSamples[i * Channels + c];
            }
            AudioClipData[i] = sum / Channels;
          }
        }
        RenderLog.Info(
          $"[AudioSpectrumGenerator] Cached audio clip: {clip.name}, Samples: {TotalSamples}, Frequency: {Frequency}Hz, Channels: {Channels}"
        );
      }
      catch (Exception e)
      {
        RenderLog.Error($"[AudioSpectrumGenerator] Failed to initialize audio cache: {e}");
        Clear();
      }
    }

    public static void Clear()
    {
      AudioClipData = null;
      TotalSamples = 0;
      Channels = 0;
      Frequency = 0;
      lock (CachedSpectrums)
      {
        _cachedFrame = -1;
        CachedSpectrums.Clear();
      }
    }

    public static void GetSpectrumData(float[] samples, int channel, FFTWindow window)
    {
      if (
        AudioClipData == null
        || AudioClipData.Length == 0
        || !scrConductor.instance
        || !scrConductor.instance.hasSongStarted
        || !scrConductor.instance.song
      )
      {
        Array.Clear(samples, 0, samples.Length);
        return;
      }

      var conductor = scrConductor.instance;
      var pitch = conductor.song.pitch;
      var separateCountdownOffset = conductor.separateCountdownTime
        ? conductor.crotchetAtStart * conductor.adjustedCountdownTicks
        : 0.0;

      // Calculate the actual scheduled start time of the song
      var songScheduledStartTime = conductor.skipOffset
        ? conductor.dspTimeSong + conductor.addoffset / pitch
        : conductor.dspTimeSong + separateCountdownOffset / pitch;

      if (conductor.dspTime < songScheduledStartTime)
      {
        Array.Clear(samples, 0, samples.Length);
        return;
      }

      var songTime = (conductor.dspTime - conductor.dspTimeSong) * pitch - separateCountdownOffset;

      var n = samples.Length;
      if (n == 0 || (n & (n - 1)) != 0) // Ensure it's a power of 2
      {
        Array.Clear(samples, 0, samples.Length);
        return;
      }

      var currentFrame = ReplayRenderSession.Current != null ? ReplayRenderSession.Current.FrameCount : -1;

      lock (CachedSpectrums)
      {
        if (currentFrame != _cachedFrame)
        {
          _cachedFrame = currentFrame;
          CachedSpectrums.Clear();
        }

        if (CachedSpectrums.TryGetValue(n, out var cached))
        {
          Array.Copy(cached, samples, n);
          return;
        }
      }

      var fftSize = 2 * n;
      var frequency = Frequency;
      var totalSamples = TotalSamples;
      var currentSampleIndex = (long)(songTime * frequency);

      var windowSamples = new float[fftSize];
      for (var i = 0; i < fftSize; i++)
      {
        var idx = currentSampleIndex - fftSize / 2 + i;
        if (idx >= 0 && idx < totalSamples)
        {
          windowSamples[i] = AudioClipData[idx];
        }
        else
        {
          windowSamples[i] = 0f;
        }
      }

      var spectrum = new float[n];
      CalculateFFT(windowSamples, spectrum);

      lock (CachedSpectrums)
      {
        CachedSpectrums[n] = spectrum;
        Array.Copy(spectrum, samples, n);
      }
    }

    private static void CalculateFFT(float[] samples, float[] spectrum)
    {
      var n = samples.Length;

      // Apply Blackman-Harris window
      const float a0 = 0.35875f;
      const float a1 = 0.48829f;
      const float a2 = 0.14128f;
      const float a3 = 0.01168f;

      var re = new float[n];
      var im = new float[n];

      for (var i = 0; i < n; i++)
      {
        var angle = 2.0 * Math.PI * i / (n - 1);
        var w = (float)(a0 - a1 * Math.Cos(angle) + a2 * Math.Cos(2.0 * angle) - a3 * Math.Cos(3.0 * angle));
        re[i] = samples[i] * w;
        im[i] = 0f;
      }

      // Cooley-Tukey Radix-2 FFT
      var m = (int)Math.Round(Math.Log(n, 2));

      // Bit-reversal
      var j = 0;
      for (var i = 0; i < n - 1; i++)
      {
        if (i < j)
        {
          var temp = re[i];
          re[i] = re[j];
          re[j] = temp;

          temp = im[i];
          im[i] = im[j];
          im[j] = temp;
        }
        var k = n / 2;
        while (k <= j)
        {
          j -= k;
          k /= 2;
        }
        j += k;
      }

      // FFT stages
      for (var stage = 1; stage <= m; stage++)
      {
        var le = 1 << stage;
        var le2 = le / 2;
        var ur = 1f;
        var ui = 0f;
        var wr = (float)Math.Cos(Math.PI / le2);
        var wi = -(float)Math.Sin(Math.PI / le2);

        for (var sub = 0; sub < le2; sub++)
        {
          for (var item = sub; item < n; item += le)
          {
            var ip = item + le2;
            var tr = re[ip] * ur - im[ip] * ui;
            var ti = re[ip] * ui + im[ip] * ur;

            re[ip] = re[item] - tr;
            im[ip] = im[item] - ti;
            re[item] += tr;
            im[item] += ti;
          }
          var tempUr = ur;
          ur = tempUr * wr - ui * wi;
          ui = tempUr * wi + ui * wr;
        }
      }

      // Normalize and fill spectrum
      var factor = 2.0f / n;
      var outLen = Math.Min(spectrum.Length, n / 2);
      for (var i = 0; i < outLen; i++)
      {
        spectrum[i] = (float)Math.Sqrt(re[i] * re[i] + im[i] * im[i]) * factor;
      }
    }
  }
}
