#nullable enable
using System;

namespace TUFReplayRenderer.Application.Render.Audio
{
  public static class AudioResampler
  {
    public static float[] Resample(
      float[] inputSamples,
      int inputChannels,
      int inputFrequency,
      int targetChannels,
      int targetFrequency
    )
    {
      // ── Safety Validation ──
      if (
        inputSamples.Length == 0
        || inputChannels <= 0
        || targetChannels <= 0
        || inputFrequency <= 0
        || targetFrequency <= 0
      )
      {
        return Array.Empty<float>();
      }

      // If format is identical, return original samples
      if (inputFrequency == targetFrequency && inputChannels == targetChannels)
        return inputSamples;

      int inputLength = inputSamples.Length / inputChannels;
      int targetLength = (int)Math.Floor((double)inputLength * targetFrequency / inputFrequency);
      float[] outputSamples = new float[targetLength * targetChannels];

      for (int i = 0; i < targetLength; i++)
      {
        double srcIndex = (double)i * inputFrequency / targetFrequency;
        long idxLow = (long)Math.Floor(srcIndex);
        long idxHigh = idxLow + 1;
        float t = (float)(srcIndex - idxLow);

        if (idxHigh >= inputLength)
          idxHigh = inputLength - 1;
        if (idxLow >= inputLength)
          idxLow = inputLength - 1;

        if (targetChannels == 1) // 1. Mono Output
        {
          float valLow,
            valHigh;
          if (inputChannels == 1) // Mono -> Mono
          {
            valLow = inputSamples[idxLow];
            valHigh = inputSamples[idxHigh];
          }
          else // Multi -> Mono (Downmix: Average all channels)
          {
            float sumLow = 0f;
            float sumHigh = 0f;
            for (int c = 0; c < inputChannels; c++)
            {
              sumLow += inputSamples[idxLow * inputChannels + c];
              sumHigh += inputSamples[idxHigh * inputChannels + c];
            }
            valLow = sumLow / inputChannels;
            valHigh = sumHigh / inputChannels;
          }
          outputSamples[i] = valLow + t * (valHigh - valLow);
        }
        else if (targetChannels == 2) // 2. Stereo Output
        {
          if (inputChannels == 1) // Mono -> Stereo (Duplicate to Left/Right)
          {
            float valLow = inputSamples[idxLow];
            float valHigh = inputSamples[idxHigh];
            float val = valLow + t * (valHigh - valLow);
            outputSamples[i * 2] = val;
            outputSamples[i * 2 + 1] = val;
          }
          else // Stereo/Multi -> Stereo (Map Left/Right, ignore extra channels)
          {
            float valLowL = inputSamples[idxLow * inputChannels];
            float valHighL = inputSamples[idxHigh * inputChannels];
            outputSamples[i * 2] = valLowL + t * (valHighL - valLowL);

            float valLowR = inputSamples[idxLow * inputChannels + 1];
            float valHighR = inputSamples[idxHigh * inputChannels + 1];
            outputSamples[i * 2 + 1] = valLowR + t * (valHighR - valLowR);
          }
        }
        else // 3. Multi-channel Output (e.g., 5.1 Surround)
        {
          for (int c = 0; c < targetChannels; c++)
          {
            float valLow,
              valHigh;
            if (c < inputChannels)
            {
              valLow = inputSamples[idxLow * inputChannels + c];
              valHigh = inputSamples[idxHigh * inputChannels + c];
            }
            else
            {
              valLow = inputSamples[idxLow * inputChannels];
              valHigh = inputSamples[idxHigh * inputChannels];
            }
            outputSamples[i * targetChannels + c] = valLow + t * (valHigh - valLow);
          }
        }
      }

      return outputSamples;
    }
  }
}
