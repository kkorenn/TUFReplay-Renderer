using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using UnityModManagerNet;

namespace TUFReplayRenderer.Loader;

/// <summary>
/// The never-updated entry assembly. Everything that can change ships as a versioned payload under
/// <c>Runtime/versions/&lt;version&gt;/</c>; this loader promotes a staged update (downloaded by
/// the previous session), falls back to the previous version when the current payload fails to
/// load, and then hands UnityModManager's mod entry to the payload's Main.
///
/// A loaded assembly's file is locked by the OS, so a mod can never replace its own running code —
/// the payload downloads and verifies the update, and this loader (whose file never changes) swaps
/// it in at the next launch. Same shape as TUFReplay's updater, minimum viable size.
/// </summary>
public static class Entry
{
  private const string PayloadAssemblyFileName = "TUFReplayRenderer.dll";
  private const string PayloadMainTypeName = "TUFReplayRenderer.Main";

  private static string _payloadDirectory;

  public static bool Load(UnityModManager.ModEntry modEntry)
  {
    try
    {
      string modRoot = modEntry.Path;
      RuntimeState state = RuntimeState.Load(modRoot);

      PromotePendingUpdate(modEntry, modRoot, state);

      // Try current, then previous. A payload that fails to even load is rolled back permanently.
      foreach (string candidate in state.CandidatesNewestFirst())
      {
        string payloadDirectory = RuntimeState.VersionDirectory(modRoot, candidate);
        string payloadAssembly = Path.Combine(payloadDirectory, PayloadAssemblyFileName);
        if (!File.Exists(payloadAssembly))
          continue;

        try
        {
          _payloadDirectory = payloadDirectory;
          AppDomain.CurrentDomain.AssemblyResolve += ResolveFromPayload;
          Assembly assembly = Assembly.LoadFrom(payloadAssembly);
          Type main = assembly.GetType(PayloadMainTypeName, true);
          MethodInfo load = main.GetMethod("Load", BindingFlags.Public | BindingFlags.Static);
          if (load == null)
            throw new MissingMethodException(PayloadMainTypeName, "Load");

          bool ok = (bool)load.Invoke(null, new object[] { modEntry });
          if (ok)
          {
            modEntry.Logger.Log("[Loader] Loaded payload " + candidate + ".");
            if (state.Current != candidate)
            {
              // We fell back: make it permanent so the next launch does not retry the bad payload.
              state.RollBackTo(candidate);
              state.Save(modRoot);
              modEntry.Logger.Warning("[Loader] Rolled back to " + candidate + ".");
            }
            return true;
          }
        }
        catch (Exception exception)
        {
          modEntry.Logger.Error("[Loader] Payload " + candidate + " failed to load: " + exception);
          AppDomain.CurrentDomain.AssemblyResolve -= ResolveFromPayload;
        }
      }

      modEntry.Logger.Error("[Loader] No loadable payload found under Runtime/versions.");
      return false;
    }
    catch (Exception exception)
    {
      modEntry.Logger.Error("[Loader] " + exception);
      return false;
    }
  }

  private static Assembly ResolveFromPayload(object sender, ResolveEventArgs args)
  {
    try
    {
      if (_payloadDirectory == null)
        return null;
      string name = new AssemblyName(args.Name).Name + ".dll";
      string candidate = Path.Combine(_payloadDirectory, name);
      return File.Exists(candidate) ? Assembly.LoadFrom(candidate) : null;
    }
    catch
    {
      return null;
    }
  }

  /// <summary>
  /// Promotes <c>Runtime/pending/&lt;version&gt;/</c> into the versions directory when its
  /// staging manifest verifies. Anything invalid is deleted; the running install is never touched
  /// until the replacement has fully checked out.
  /// </summary>
  private static void PromotePendingUpdate(UnityModManager.ModEntry modEntry, string modRoot, RuntimeState state)
  {
    string pendingRoot = Path.Combine(modRoot, "Runtime", "pending");
    if (!Directory.Exists(pendingRoot))
      return;

    foreach (string pendingDirectory in Directory.GetDirectories(pendingRoot))
    {
      string version = Path.GetFileName(pendingDirectory);
      try
      {
        if (!VerifyStagingManifest(pendingDirectory))
        {
          modEntry.Logger.Warning("[Loader] Staged update " + version + " failed verification; discarded.");
          Directory.Delete(pendingDirectory, true);
          continue;
        }

        string target = RuntimeState.VersionDirectory(modRoot, version);
        if (Directory.Exists(target))
          Directory.Delete(target, true);
        Directory.CreateDirectory(Path.GetDirectoryName(target));
        Directory.Move(pendingDirectory, target);

        state.Advance(version);
        state.Save(modRoot);
        state.DeleteObsoleteVersions(modRoot);
        modEntry.Logger.Log("[Loader] Update " + version + " promoted.");
      }
      catch (Exception exception)
      {
        modEntry.Logger.Error("[Loader] Could not promote staged update " + version + ": " + exception);
      }
    }

    try
    {
      if (Directory.Exists(pendingRoot) && Directory.GetFileSystemEntries(pendingRoot).Length == 0)
        Directory.Delete(pendingRoot);
    }
    catch
    {
      // Best effort; a leftover empty directory is harmless.
    }
  }

  /// <summary>
  /// staging.json lists every staged file with its SHA-256 (written by the updater after it
  /// verified the downloaded archive). Re-verifying here catches partial extraction and any
  /// tampering between download and next launch.
  /// </summary>
  private static bool VerifyStagingManifest(string pendingDirectory)
  {
    string manifestPath = Path.Combine(pendingDirectory, "staging.json");
    if (!File.Exists(manifestPath))
      return false;

    Dictionary<string, string> expected = MiniJson.ParseStringMap(File.ReadAllText(manifestPath), "Files");
    if (expected == null || expected.Count == 0)
      return false;
    if (!expected.ContainsKey(PayloadAssemblyFileName))
      return false;

    using (SHA256 sha = SHA256.Create())
    {
      foreach (KeyValuePair<string, string> entry in expected)
      {
        string filePath = Path.Combine(pendingDirectory, entry.Key.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(filePath))
          return false;
        using (FileStream stream = File.OpenRead(filePath))
        {
          string actual = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty);
          if (!string.Equals(actual, entry.Value, StringComparison.OrdinalIgnoreCase))
            return false;
        }
      }
    }
    return true;
  }

  /// <summary>Runtime/Current.json: which payload runs, and which one to fall back to.</summary>
  private sealed class RuntimeState
  {
    public string Current;
    public string Previous;

    public static string VersionDirectory(string modRoot, string version) =>
      Path.Combine(modRoot, "Runtime", "versions", version);

    private static string FilePath(string modRoot) => Path.Combine(modRoot, "Runtime", "Current.json");

    public static RuntimeState Load(string modRoot)
    {
      RuntimeState state = new RuntimeState();
      try
      {
        string path = FilePath(modRoot);
        if (File.Exists(path))
        {
          Dictionary<string, string> values = MiniJson.ParseFlatStringObject(File.ReadAllText(path));
          values.TryGetValue("Current", out state.Current);
          values.TryGetValue("Previous", out state.Previous);
        }
      }
      catch
      {
        // A corrupt state file falls through to directory discovery below.
      }

      if (string.IsNullOrEmpty(state.Current))
      {
        // Self-heal: pick the newest version directory present.
        string versionsRoot = Path.Combine(modRoot, "Runtime", "versions");
        if (Directory.Exists(versionsRoot))
        {
          string[] directories = Directory.GetDirectories(versionsRoot);
          Array.Sort(directories, StringComparer.OrdinalIgnoreCase);
          if (directories.Length > 0)
            state.Current = Path.GetFileName(directories[directories.Length - 1]);
        }
      }
      return state;
    }

    public void Save(string modRoot)
    {
      Directory.CreateDirectory(Path.Combine(modRoot, "Runtime"));
      string previous = Previous == null ? "null" : "\"" + Previous + "\"";
      File.WriteAllText(
        FilePath(modRoot),
        "{\n  \"SchemaVersion\": 1,\n  \"Current\": \"" + Current + "\",\n  \"Previous\": " + previous + "\n}\n"
      );
    }

    public void Advance(string newVersion)
    {
      if (!string.Equals(Current, newVersion, StringComparison.OrdinalIgnoreCase))
        Previous = Current;
      Current = newVersion;
    }

    public void RollBackTo(string version)
    {
      Current = version;
      Previous = null;
    }

    public IEnumerable<string> CandidatesNewestFirst()
    {
      if (!string.IsNullOrEmpty(Current))
        yield return Current;
      if (!string.IsNullOrEmpty(Previous) && Previous != Current)
        yield return Previous;
    }

    /// <summary>Keeps Current and Previous; anything else is an old payload nobody can load.</summary>
    public void DeleteObsoleteVersions(string modRoot)
    {
      string versionsRoot = Path.Combine(modRoot, "Runtime", "versions");
      if (!Directory.Exists(versionsRoot))
        return;
      foreach (string directory in Directory.GetDirectories(versionsRoot))
      {
        string version = Path.GetFileName(directory);
        if (version == Current || version == Previous)
          continue;
        try
        {
          Directory.Delete(directory, true);
        }
        catch
        {
          // A locked directory stays; it is retried next launch.
        }
      }
    }
  }

  /// <summary>
  /// Tiny JSON reader for the two flat shapes this loader needs. The loader must not depend on
  /// Newtonsoft (it loads before any payload, and its file can never be updated), so it parses
  /// only string values in flat objects — exactly what Current.json and staging.json contain.
  /// </summary>
  private static class MiniJson
  {
    public static Dictionary<string, string> ParseFlatStringObject(string json)
    {
      Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.Ordinal);
      int index = 0;
      while (true)
      {
        int keyStart = json.IndexOf('"', index);
        if (keyStart < 0)
          break;
        int keyEnd = json.IndexOf('"', keyStart + 1);
        if (keyEnd < 0)
          break;
        string key = json.Substring(keyStart + 1, keyEnd - keyStart - 1);
        int colon = json.IndexOf(':', keyEnd);
        if (colon < 0)
          break;
        int valueStart = colon + 1;
        while (valueStart < json.Length && char.IsWhiteSpace(json[valueStart]))
          valueStart++;
        if (valueStart < json.Length && json[valueStart] == '"')
        {
          int valueEnd = json.IndexOf('"', valueStart + 1);
          if (valueEnd < 0)
            break;
          result[key] = json.Substring(valueStart + 1, valueEnd - valueStart - 1);
          index = valueEnd + 1;
        }
        else
        {
          index = valueStart + 1;
        }
      }
      return result;
    }

    /// <summary>Returns the flat string map stored under <paramref name="objectKey"/>.</summary>
    public static Dictionary<string, string> ParseStringMap(string json, string objectKey)
    {
      int keyIndex = json.IndexOf("\"" + objectKey + "\"", StringComparison.Ordinal);
      if (keyIndex < 0)
        return null;
      int braceStart = json.IndexOf('{', keyIndex);
      if (braceStart < 0)
        return null;
      int depth = 0;
      for (int i = braceStart; i < json.Length; i++)
      {
        if (json[i] == '{')
          depth++;
        else if (json[i] == '}' && --depth == 0)
          return ParseFlatStringObject(json.Substring(braceStart, i - braceStart + 1));
      }
      return null;
    }
  }
}
