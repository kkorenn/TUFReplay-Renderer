# TUFReplay-Renderer

Companion mod for [TUFReplay](https://github.com/KGH1113/TUFReplay) that renders a saved run to a
video file. It exists as a separate UnityModManager mod so that TUFReplay itself contains no
rendering or FFmpeg code: install this mod and the render controls appear in the TUFReplay web UI;
remove it and they disappear.

## What it does

Rendering plays a saved run through TUFReplay's normal replay pipeline with an offline capture
attached. Because ADOFAI's hit-context playback is angle-gated rather than clock-gated, the run
reproduces itself identically under a virtual clock, so the renderer can take as long as it needs
per frame without dropping or duplicating one. Nothing about the presentation is suppressed, and
the run is never switched to autoplay: the recorded inputs decide every judgment, and cleared,
failed, and aborted runs each end the way they were recorded.

Audio is mixed offline rather than recorded from the live mixer. The song, every hit sound the game
plays, and the run's saved microphone recording are each placed sample-accurately against the same
rational frame clock the video uses, so A/V cannot drift. Microphone alignment reuses the exact
offset, gain, and limiter envelope of live playback, evaluated per sample instead of per frame.

Frames are read back as RGBA and converted to the encoder's pixel format by swscale.
ADOFAIRenderer's GPU colour-conversion path is deliberately not ported, because it requires a
platform-specific compute-shader AssetBundle; it only changes how long a render takes, never what
it contains.

## Requirements

- The TUFReplay mod (this mod registers itself with TUFReplay's replay pipeline at load).
- AdofaiIpc (for the web UI's IPC surface).
- The FFmpeg 8 shared libraries (`libavcodec.62` / `avcodec-62` and friends). They are resolved in
  this order:
  1. The `TUFREPLAY_FFMPEG_DIR` environment variable.
  2. `native/<rid>` inside the installed mod.
  3. The game's `UserLibs` directory, which is where ADOFAIRenderer installs a compatible set.
  4. The operating system's own library search paths.

The distributed package ships without FFmpeg. When no local install is found, the mod downloads
the current platform's LGPL set (only that platform's, ~20-60 MB) in the background while the game
boots, guided by `ffmpeg-manifest.json` (URL + SHA-256 per platform, verified before install), and
rendering unlocks without a restart. Progress and failures surface through
`render.capabilities.get` (`FFmpegDownloadState`, `FFmpegDownloadProgressPercent`, and the
`UnavailableReason` text). Override the download source with `TUFREPLAY_FFMPEG_DOWNLOAD_BASE`.
Until (or if) the download completes, TUFReplay keeps working and `render.capabilities.get`
reports why rendering is unavailable. Videos are written to `TUFReplay Renders` next to the game
directory unless `outputPath` overrides it.

## How the mods connect

- At load, this mod registers an `IRenderCaptureBridge` implementation with TUFReplay. TUFReplay's
  replay pipeline calls the bridge while a replay plays in render-capture mode; without a bridge,
  TUFReplay behaves exactly as if this mod were not installed.
- The mod registers its own AdofaiIpc namespace, `tuf-replay-renderer`. The TUFReplay web UI
  detects the renderer through this namespace and hides all render controls when it is absent.

Registered IPC methods (namespace `tuf-replay-renderer`):

- `health.get`
- `render.capabilities.get` (reports whether rendering is available on this machine, the detected FFmpeg version, the output directory, and the default render settings)
- `render.start` (`runId` plus optional `levelPath` and render overrides: `width`, `height`, `renderFps`, `videoFps`, `codec`, `rateControlMode`, `qualityValue`, `targetBitrateKbps`, `maxBitrateKbps`, `keyframeIntervalSeconds`, `forceSoftwareEncoder`, `renderAudio`, `audioSampleRate`, `audioChannels`, `audioBitrate`, `includeMicrophone`, `trailingSeconds`, `outputPath`)
- `render.status.get`
- `render.cancel`
- `render.preview.start` / `render.preview.stop` / `render.preview.result.get` (audio-only preview mix returned as WAV stems)

## Building

```
./scripts/run.sh build
./scripts/run.sh install    # build + copy into the game's Mods directory
./scripts/run.sh package    # stage + zip a distributable
```

The build references `TUFReplay.dll` from the sibling TUFReplay repository's build output by
default; set `TUFREPLAY_DLL` (or `TUFReplayDll` msbuild property) to override. See `.env.example`
for all knobs.

`package` produces a lean mod zip plus three separate release assets:
`build/ffmpeg-{osx,win-x64,linux-x64}.zip`, the LGPL-clean FFmpeg 8 sets the mod downloads at
runtime (macOS universal dylibs, Windows x64 DLLs, Linux x64 shared objects). Missing platforms
are produced automatically by `prepare-lgpl-ffmpeg` (Windows and Linux download BtbN LGPL shared
builds; macOS builds from source and needs `nasm`). Upload the three ffmpeg zips to the release
URL baked into `ffmpeg-manifest.json` — `TUFREPLAY_FFMPEG_RELEASE_BASE_URL` at package time,
default `https://github.com/kkorenn/TUFReplay-Renderer/releases/download/ffmpeg-n8.1`. The
manifest pins each asset's SHA-256, so re-upload requires re-packaging. `install` still bundles
natives directly for local development.

## Updating

The install is auto-updatable. `Info.json` points at `TUFReplayRenderer.Loader.dll`, a tiny
never-updated loader; the actual mod lives under `Runtime/versions/<version>/`. At runtime the mod
checks this repository's GitHub releases in the background (respecting the in-game auto-update and
beta toggles), downloads a newer release, verifies it against the SHA-256 in the release's
`update.json` asset, and stages the payload under `Runtime/pending/`. The loader re-verifies the
staged files and swaps them in at the next game launch — a running assembly's file is OS-locked,
so the swap can only happen there. If a new payload fails to load, the loader permanently falls
back to the previous version. Releases whose `update.json` requires a newer bridge
(`MinBridgeApiVersion`) than the installed TUFReplay provides are skipped rather than updating
into a disabled state.

Publishing a release: `./scripts/run.sh package` produces the zip and `build/update.json`; upload
BOTH as assets on a `v<version>` release.

## Licensing

See `THIRD_PARTY_NOTICES.md`: portions are derived from ADOFAIRenderer (MIT), FFmpeg bindings are
FFmpeg.AutoGen (LGPL-3.0), and the bundled FFmpeg shared libraries are LGPL-2.1+ builds configured
without GPL components.
