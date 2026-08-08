#nullable enable
using System;
using System.Collections;
using System.IO;
using TUFReplayRenderer.Infrastructure.Render;
using UnityEngine;

namespace TUFReplayRenderer.Application.Render.Audio;

/// <summary>
/// Decodes a private, fully loaded copy of the level song for the offline mixer.
///
/// ADOFAIRenderer reads samples from <c>scrConductor.song.clip</c>. That is not available here:
/// in the editor the conductor's AudioSource plays through a non-clip AudioResource, so
/// <c>song.clip</c> is null, and the copy AudioManager caches is a streaming clip whose
/// <c>GetData()</c> returns zeros. Both roads to the game's own clip are dead ends, so the song
/// file named by the level (<c>levelData.songFilename</c>) is decoded directly — ogg/wav/aiff via
/// UnityWebRequestMultimedia with streaming off, mp3 via the game's own AudioClipData decoder.
///
/// The result is deliberately private: it is never added to AudioManager's cache and never
/// assigned to the conductor, so the game's own audio loading is untouched — an earlier version
/// that forced the game's loader to decode caused a duplicate-key crash inside AudioManager when
/// two loads raced.
/// </summary>
public sealed class RenderAudioPreparer
{
  private bool _started;

  /// <summary>True once preparation finished, successfully or not. Never blocks a render.</summary>
  public volatile bool IsDone;

  /// <summary>Fully decoded song clip owned by the render, or null when unavailable.</summary>
  public AudioClip? PreparedClip { get; private set; }

  public void Begin()
  {
    if (_started)
      return;
    _started = true;

    try
    {
      string? songPath = ResolveSongPath();
      if (songPath == null)
      {
        IsDone = true;
        return;
      }

      string extension = Path.GetExtension(songPath).ToLowerInvariant();
      if (extension == ".mp3")
      {
        // The game's own mp3 decoder produces a fully decoded clip synchronously.
        PreparedClip = new AudioClipData(songPath).CreateAudioClip();
        if (PreparedClip != null)
          PreparedClip.name = Path.GetFileName(songPath) + "*render";
        RenderLog.Info("Decoded mp3 song for the mixer: " + Path.GetFileName(songPath));
        IsDone = true;
        return;
      }

      AudioType audioType = extension switch
      {
        ".ogg" => AudioType.OGGVORBIS,
        ".wav" => AudioType.WAV,
        ".aif" or ".aiff" => AudioType.AIFF,
        _ => AudioType.UNKNOWN,
      };
      if (audioType == AudioType.UNKNOWN)
      {
        RenderLog.Warn("Unsupported song format for decoding: " + extension);
        IsDone = true;
        return;
      }

      Features.Render.RenderFeature? feature = Features.Render.RenderFeature.Instance;
      if (feature == null || feature.RunCoroutine(LoadDecoded(songPath, audioType)) == null)
      {
        RenderLog.Warn("No coroutine host for the song decode; background music may be silent.");
        IsDone = true;
      }
    }
    catch (Exception exception)
    {
      RenderLog.Warn("Song decode setup failed (" + exception.Message + "); background music may be silent.");
      IsDone = true;
    }
  }

  /// <summary>Releases the decoded clip. Call once the mixer has copied its samples.</summary>
  public void ReleasePreparedClip()
  {
    AudioClip? clip = PreparedClip;
    PreparedClip = null;
    if (clip == null)
      return;
    try
    {
      UnityEngine.Object.Destroy(clip);
    }
    catch (Exception exception)
    {
      RenderLog.Warn("Could not release the decoded song clip: " + exception.Message);
    }
  }

  private static string? ResolveSongPath()
  {
    string? fileName = null;
    try
    {
      fileName = scnEditor.instance?.levelData?.songFilename;
    }
    catch
    {
      // Missing settings dictionary entries throw; treated as no song.
    }

    if (string.IsNullOrWhiteSpace(fileName))
    {
      RenderLog.Warn("The level names no song file; the render will have no background music.");
      return null;
    }

    string? levelDirectory = null;
    try
    {
      string levelPath = ADOBase.levelPath;
      if (!string.IsNullOrEmpty(levelPath))
        levelDirectory = Path.GetDirectoryName(levelPath);
    }
    catch
    {
      // ADOBase.levelPath can throw during scene transitions; treated as unavailable.
    }

    if (levelDirectory == null)
    {
      RenderLog.Warn("The level directory could not be resolved; background music may be silent.");
      return null;
    }

    string songPath = Path.Combine(levelDirectory, fileName);
    if (!File.Exists(songPath))
    {
      RenderLog.Warn("The song file was not found (" + songPath + "); background music may be silent.");
      return null;
    }
    return songPath;
  }

  private IEnumerator LoadDecoded(string songPath, AudioType audioType)
  {
    RenderLog.Info("Decoding the song for the mixer: " + Path.GetFileName(songPath));

    string uri;
    try
    {
      // The game's own file-URI conversion — correct escaping for spaces and unicode.
      uri = RDUtils.ToFileUri(songPath) ?? new Uri(songPath).AbsoluteUri;
    }
    catch
    {
      uri = "file://" + songPath;
    }

    using (
      UnityEngine.Networking.UnityWebRequest request = UnityEngine.Networking.UnityWebRequestMultimedia.GetAudioClip(
        uri,
        audioType
      )
    )
    {
      ((UnityEngine.Networking.DownloadHandlerAudioClip)request.downloadHandler).streamAudio = false;
      yield return request.SendWebRequest();

      try
      {
        if (request.result != UnityEngine.Networking.UnityWebRequest.Result.Success)
        {
          RenderLog.Warn("Song decode failed (" + request.error + "); background music may be silent.");
        }
        else
        {
          AudioClip clip = UnityEngine.Networking.DownloadHandlerAudioClip.GetContent(request);
          if (clip != null && clip.length > 0f)
          {
            clip.name = Path.GetFileName(songPath) + "*render";
            PreparedClip = clip;
            RenderLog.Info(
              "Decoded song ready: "
                + clip.name
                + " ("
                + clip.loadType
                + ", "
                + clip.length.ToString("F1")
                + "s, "
                + clip.frequency
                + "Hz)"
            );
          }
          else
          {
            RenderLog.Warn("The decoded song clip is empty; background music may be silent.");
          }
        }
      }
      finally
      {
        IsDone = true;
      }
    }
  }
}
