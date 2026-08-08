#nullable enable
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text.RegularExpressions;
using TUFReplayRenderer.Domain.Render;

namespace TUFReplayRenderer.Infrastructure.Render
{
  [SuppressMessage("ReSharper", "InconsistentNaming")]
  public enum GPUVendor
  {
    Unknown,
    NVIDIA,
    AMD,
    Intel,
    Apple,
  }

  public static class GPUUtil
  {
    private const string NvidiaPattern =
      @"^NVIDIA GeForce [RG]TX [0-9]+(?> SUPER| Ti(?> SUPER)?)?(?> [0-9]+GB)?(?> Laptop GPU)?(?>/PCIe/SSE2)?$";

    private const string IntelPattern = @"^Intel(?>\(R\))? (?>U?HD Graphics [0-9]+$|Arc\(TM\) [A-C][0-9]+ Graphics)$";

    // ReSharper disable once InconsistentNaming
    private const string AMDPattern = @"^AMD Radeon RX [0-9]+(?> XT)?$";

    private static readonly Dictionary<Codec, List<(GPUVendor, string)>> CodecMap = new()
    {
      [Codec.H264] = new List<(GPUVendor, string)>
      {
        (GPUVendor.NVIDIA, "h264_nvenc"),
        (GPUVendor.Intel, "h264_qsv"),
        (GPUVendor.AMD, "h264_amf"),
        (GPUVendor.Apple, "h264_videotoolbox"),
        (GPUVendor.Unknown, "libx264"),
      },
      [Codec.H265] = new List<(GPUVendor, string)>
      {
        (GPUVendor.NVIDIA, "hevc_nvenc"),
        (GPUVendor.Intel, "hevc_qsv"),
        (GPUVendor.AMD, "hevc_amf"),
        (GPUVendor.Apple, "hevc_videotoolbox"),
        (GPUVendor.Unknown, "libx265"),
      },
      [Codec.VP9] = new List<(GPUVendor, string)>
      {
        (GPUVendor.NVIDIA, "vp9_nvenc"),
        (GPUVendor.Intel, "vp9_qsv"),
        (GPUVendor.Unknown, "libvpx-vp9"),
      },
      [Codec.AV1] = new List<(GPUVendor, string)>
      {
        (GPUVendor.NVIDIA, "av1_nvenc"),
        (GPUVendor.Intel, "av1_qsv"),
        (GPUVendor.AMD, "av1_amf"),
        (GPUVendor.Unknown, "libsvtav1"),
        (GPUVendor.Unknown, "libaom-av1"),
      },
      [Codec.VVC] = new List<(GPUVendor, string)> { (GPUVendor.Unknown, "libvvenc") },
      [Codec.ProRes] = new List<(GPUVendor, string)>
      {
        (GPUVendor.Apple, "prores_videotoolbox"),
        (GPUVendor.Unknown, "prores_ks"),
      },
    };

    private static GPUVendor _cachedVendor = GPUVendor.Unknown;
    private static bool _hasCachedVendor;

    private static GPUVendor GetActiveGPUVendor()
    {
      if (_hasCachedVendor)
        return _cachedVendor;

      var gpuName = UnityEngine.SystemInfo.graphicsDeviceName;
      RenderLog.Info($"Active GPU: {gpuName}");

      // macOS에서는 GPU 벤더와 무관하게 VideoToolbox(OS 제공 하드웨어 인코더)를 사용합니다.
      // Apple Silicon뿐 아니라 Intel Mac에서도 VideoToolbox가 하드웨어 인코딩을 담당합니다.
      if (UnityEngine.Application.platform == UnityEngine.RuntimePlatform.OSXPlayer)
      {
        _cachedVendor = GPUVendor.Apple;
        _hasCachedVendor = true;
        return _cachedVendor;
      }

      _cachedVendor = GPUVendor.Unknown;
      foreach (Match unused in Regex.Matches(gpuName, NvidiaPattern))
      {
        _cachedVendor = GPUVendor.NVIDIA;
        break;
      }

      if (_cachedVendor == GPUVendor.Unknown)
      {
        foreach (Match unused in Regex.Matches(gpuName, IntelPattern))
        {
          _cachedVendor = GPUVendor.Intel;
          break;
        }
      }

      if (_cachedVendor == GPUVendor.Unknown)
      {
        foreach (Match unused in Regex.Matches(gpuName, AMDPattern))
        {
          _cachedVendor = GPUVendor.AMD;
          break;
        }
      }

      _hasCachedVendor = true;
      return _cachedVendor;
    }

    private static double? _cachedNvidiaVersion;
    private static bool _hasCachedNvidiaVersion;

    public static double? GetNvidiaDriverVersion()
    {
      if (_hasCachedNvidiaVersion)
        return _cachedNvidiaVersion;

      _hasCachedNvidiaVersion = true;
      _cachedNvidiaVersion = null;

      if (GetActiveGPUVendor() != GPUVendor.NVIDIA)
        return null;

      // 1. Try parsing from UnityEngine.SystemInfo.graphicsDeviceVersion
      var deviceVersion = UnityEngine.SystemInfo.graphicsDeviceVersion;
      if (!string.IsNullOrEmpty(deviceVersion))
      {
        var match = Regex.Match(deviceVersion, @"\b(\d+)\.(\d+)\.(\d+)\.(\d+)\b");
        if (match.Success)
        {
          if (int.TryParse(match.Groups[3].Value, out int sub) && int.TryParse(match.Groups[4].Value, out int build))
          {
            var nvidiaVerInt = sub % 10 * 10000 + build;
            _cachedNvidiaVersion = nvidiaVerInt / 100.0;
            RenderLog.Info($"Parsed NVIDIA Driver Version from SystemInfo: {_cachedNvidiaVersion:F2}");
            return _cachedNvidiaVersion;
          }
        }
      }

      // 2. Fallback to Windows Registry (only on Windows NT platforms via RegistryUtil)
      if (System.Environment.OSVersion.Platform != System.PlatformID.Win32NT)
        return null;
      const string classKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
      var subkeys = RegistryUtil.GetHKLMSubKeyNames(classKey);
      if (subkeys == null)
        return null;

      foreach (var subkey in subkeys)
      {
        var subkeyPath = classKey + "\\" + subkey;
        var provider = RegistryUtil.GetHKLMValue(subkeyPath, "ProviderName");
        if (provider == null || !provider.Contains("NVIDIA"))
          continue;

        var driverVersionStr = RegistryUtil.GetHKLMValue(subkeyPath, "DriverVersion");
        if (string.IsNullOrEmpty(driverVersionStr))
          continue;

        var regMatch = Regex.Match(driverVersionStr, @"\b(\d+)\.(\d+)\.(\d+)\.(\d+)\b");
        if (!regMatch.Success)
          continue;
        if (
          !int.TryParse(regMatch.Groups[3].Value, out var sub) || !int.TryParse(regMatch.Groups[4].Value, out var build)
        )
          continue;

        var nvidiaVerInt = sub % 10 * 10000 + build;
        _cachedNvidiaVersion = nvidiaVerInt / 100f;
        RenderLog.Info($"Parsed NVIDIA Driver Version from Registry (RegistryUtil): {_cachedNvidiaVersion:F2}");
        return _cachedNvidiaVersion;
      }

      return null;
    }

    public static List<string> GetPrioritizedEncoderCandidates(Codec codec, bool forceSoftware = false)
    {
      if (!CodecMap.TryGetValue(codec, out var list))
        return new List<string>();

      var prioritized = new List<string>();
      if (forceSoftware)
      {
        prioritized.AddRange(from item in list where item.Item1 == GPUVendor.Unknown select item.Item2);
        return prioritized;
      }

      var vendor = GetActiveGPUVendor();
      var others = new List<string>();

      foreach (var item in list)
      {
        if (item.Item1 == vendor)
          prioritized.Add(item.Item2);
        else
          others.Add(item.Item2);
      }

      prioritized.AddRange(others);
      return prioritized;
    }
  }
}
