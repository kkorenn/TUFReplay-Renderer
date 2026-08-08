#nullable enable
using System;

namespace TUFReplayRenderer.Infrastructure.Render
{
  public static class RegistryUtil
  {
    private static readonly Type? RegistryKeyType;
    private static readonly Type? RegistryHiveType;
    private static readonly Type? RegistryViewType;
    private static readonly System.Reflection.MethodInfo? OpenBaseKeyMethod;
    private static readonly System.Reflection.MethodInfo? OpenSubKeyMethod;
    private static readonly System.Reflection.MethodInfo? GetSubKeyNamesMethod;
    private static readonly System.Reflection.MethodInfo? GetValueMethod;

    static RegistryUtil()
    {
      try
      {
        RegistryKeyType = Type.GetType("Microsoft.Win32.RegistryKey");
        RegistryHiveType = Type.GetType("Microsoft.Win32.RegistryHive");
        RegistryViewType = Type.GetType("Microsoft.Win32.RegistryView");

        if (RegistryKeyType == null || RegistryHiveType == null || RegistryViewType == null)
        {
          var mscorlib = typeof(object).Assembly;
          RegistryKeyType = mscorlib.GetType("Microsoft.Win32.RegistryKey");
          RegistryHiveType = mscorlib.GetType("Microsoft.Win32.RegistryHive");
          RegistryViewType = mscorlib.GetType("Microsoft.Win32.RegistryView");
        }

        // .NET Core 계열 런타임에서는 RegistryKey가 별도 어셈블리에 있습니다.
        if (RegistryKeyType == null || RegistryHiveType == null || RegistryViewType == null)
        {
          try
          {
            var registryAssembly = System.Reflection.Assembly.Load("Microsoft.Win32.Registry");
            RegistryKeyType ??= registryAssembly.GetType("Microsoft.Win32.RegistryKey");
            RegistryHiveType ??= registryAssembly.GetType("Microsoft.Win32.RegistryHive");
            RegistryViewType ??= registryAssembly.GetType("Microsoft.Win32.RegistryView");
          }
          catch
          {
            // ignored — 아래 null 체크에서 처리
          }
        }

        if (RegistryKeyType != null)
        {
          OpenBaseKeyMethod = RegistryKeyType.GetMethod("OpenBaseKey", new[] { RegistryHiveType!, RegistryViewType! });
          OpenSubKeyMethod = RegistryKeyType.GetMethod("OpenSubKey", new[] { typeof(string) });
          GetSubKeyNamesMethod = RegistryKeyType.GetMethod("GetSubKeyNames");
          GetValueMethod = RegistryKeyType.GetMethod("GetValue", new[] { typeof(string) });
        }
      }
      catch
      {
        // ignored
      }
    }

    private static void CloseKey(object? key)
    {
      if (key == null || RegistryKeyType == null)
        return;
      try
      {
        if (key is IDisposable disposable)
        {
          disposable.Dispose();
        }
        else
        {
          var closeMethod = RegistryKeyType.GetMethod("Close");
          closeMethod?.Invoke(key, null);
        }
      }
      catch
      {
        // ignored
      }
    }

    public static string[]? GetHKLMSubKeyNames(string parentKeyPath)
    {
      if (
        RegistryKeyType == null
        || OpenBaseKeyMethod == null
        || OpenSubKeyMethod == null
        || GetSubKeyNamesMethod == null
      )
        return null;

      try
      {
        object hiveLocalMachine = Enum.ToObject(RegistryHiveType!, 0x80000002);
        object viewRegistry64 = Enum.ToObject(RegistryViewType!, 256);

        object? baseKey = OpenBaseKeyMethod.Invoke(null, new[] { hiveLocalMachine, viewRegistry64 });
        if (baseKey == null)
          return null;

        object? key = OpenSubKeyMethod.Invoke(baseKey, new object[] { parentKeyPath });
        if (key == null)
        {
          CloseKey(baseKey);
          return null;
        }

        string[]? subkeyNames = GetSubKeyNamesMethod.Invoke(key, null) as string[];
        CloseKey(key);
        CloseKey(baseKey);
        return subkeyNames;
      }
      catch
      {
        return null;
      }
    }

    public static string? GetHKLMValue(string keyPath, string valueName)
    {
      if (RegistryKeyType == null || OpenBaseKeyMethod == null || OpenSubKeyMethod == null || GetValueMethod == null)
        return null;

      try
      {
        object hiveLocalMachine = Enum.ToObject(RegistryHiveType!, 0x80000002);
        object viewRegistry64 = Enum.ToObject(RegistryViewType!, 256);

        object? baseKey = OpenBaseKeyMethod.Invoke(null, new[] { hiveLocalMachine, viewRegistry64 });
        if (baseKey == null)
          return null;

        object? key = OpenSubKeyMethod.Invoke(baseKey, new object[] { keyPath });
        if (key == null)
        {
          CloseKey(baseKey);
          return null;
        }

        string? value = GetValueMethod.Invoke(key, new object[] { valueName }) as string;
        CloseKey(key);
        CloseKey(baseKey);
        return value;
      }
      catch
      {
        return null;
      }
    }
  }
}
