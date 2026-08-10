#!/usr/bin/env bash
#
# Build/install/package entry point for TUFReplay-Renderer.
#
#   ./scripts/run.sh build                 Build the mod.
#   ./scripts/run.sh install               Build and install into the game's Mods directory.
#   ./scripts/run.sh package               Stage and zip a distributable package.
#   ./scripts/run.sh prepare-lgpl-ffmpeg   Produce the LGPL FFmpeg libraries to bundle.
#
# Environment (a .env file next to this repo's root is sourced when present):
#   ADOFAI_DIR                  Game directory (defaults to the Steam macOS path).
#   ADOFAI_MODS_DIR             Mods directory (defaults to "$ADOFAI_DIR/Mods").
#   TUFREPLAY_DLL               TUFReplay.dll to compile against (defaults to the sibling
#                               TUFReplay repository's build output).
#   TUFREPLAY_FFMPEG_OSX_DIR    Directory with the macOS FFmpeg 8 dylibs to bundle (optional;
#                               falls back to "$ADOFAI_DIR/UserLibs" when present).
#   TUFREPLAY_FFMPEG_WIN_X64_DIR  Directory with the Windows x64 FFmpeg 8 DLLs to bundle (optional).
#   TUFREPLAY_FFMPEG_LINUX_X64_DIR  Directory with the Linux x64 FFmpeg 8 shared objects (optional).
#
# `package` always bundles FFmpeg for osx, win-x64, and linux-x64: missing platforms are produced
# by prepare-lgpl-ffmpeg automatically and staging failures are hard errors.
#
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJECT="$ROOT/TUFReplayRenderer"
MOD_ID="TUFReplay-Renderer"

if [ -f "$ROOT/.env" ]; then
  # shellcheck disable=SC1091
  set -a; source "$ROOT/.env"; set +a
fi

ADOFAI_DIR="${ADOFAI_DIR:-$HOME/Library/Application Support/Steam/steamapps/common/A Dance of Fire and Ice}"
ADOFAI_MODS_DIR="${ADOFAI_MODS_DIR:-$ADOFAI_DIR/Mods}"

FFMPEG_OSX_LIBS=(
  libavcodec.62.dylib
  libavformat.62.dylib
  libavutil.60.dylib
  libswresample.6.dylib
  libswscale.9.dylib
)
FFMPEG_WIN_LIBS=(
  avcodec-62.dll
  avformat-62.dll
  avutil-60.dll
  swresample-6.dll
  swscale-9.dll
)
FFMPEG_LINUX_LIBS=(
  libavcodec.so.62
  libavformat.so.62
  libavutil.so.60
  libswresample.so.6
  libswscale.so.9
)
LGPL_FFMPEG_DIR="$ROOT/build/lgpl-ffmpeg"

log() { printf '==> %s\n' "$1"; }
fail() { printf 'error: %s\n' "$1" >&2; exit 1; }
is_macos() { [ "$(uname -s)" = "Darwin" ]; }

ffmpeg_native_source_dir() {
  local runtime_identifier="$1"
  local candidate=""

  case "$runtime_identifier" in
    osx) candidate="${TUFREPLAY_FFMPEG_OSX_DIR:-}" ;;
    win-x64) candidate="${TUFREPLAY_FFMPEG_WIN_X64_DIR:-}" ;;
    linux-x64) candidate="${TUFREPLAY_FFMPEG_LINUX_X64_DIR:-}" ;;
  esac

  # Default to the LGPL set prepared by `prepare-lgpl-ffmpeg` in this repo.
  if [ -z "$candidate" ] && [ -d "$LGPL_FFMPEG_DIR/$runtime_identifier" ]; then
    candidate="$LGPL_FFMPEG_DIR/$runtime_identifier"
  fi

  if [ -z "$candidate" ] && [ "$runtime_identifier" = "osx" ] && [ -d "$ADOFAI_DIR/UserLibs" ]; then
    candidate="$ADOFAI_DIR/UserLibs"
  fi

  printf '%s\n' "$candidate"
}

# Makes sure the LGPL FFmpeg set exists for every platform, producing any missing ones.
ensure_lgpl_ffmpeg() {
  if [ -f "$LGPL_FFMPEG_DIR/osx/libavcodec.62.dylib" ] \
    && [ -f "$LGPL_FFMPEG_DIR/win-x64/avcodec-62.dll" ] \
    && [ -f "$LGPL_FFMPEG_DIR/linux-x64/libavcodec.so.62" ]; then
    return
  fi
  log "Preparing missing LGPL FFmpeg platforms"
  "$ROOT/scripts/tasks/prepare-lgpl-ffmpeg.sh" "$LGPL_FFMPEG_DIR"
}

# Stages the FFmpeg shared libraries the renderer loads at runtime into <dest>/native/<rid>.
# Without them the mod still loads; rendering reports itself unavailable unless the runtime loader
# finds a system-wide or ADOFAIRenderer-provided install.
copy_ffmpeg_natives() {
  local destination="$1"
  local requirement="${2:-optional}"
  local runtime_identifier source target library
  local -a libraries

  for runtime_identifier in osx win-x64 linux-x64; do
    case "$runtime_identifier" in
      osx) libraries=("${FFMPEG_OSX_LIBS[@]}") ;;
      win-x64) libraries=("${FFMPEG_WIN_LIBS[@]}") ;;
      linux-x64) libraries=("${FFMPEG_LINUX_LIBS[@]}") ;;
    esac

    source="$(ffmpeg_native_source_dir "$runtime_identifier")"
    if [ -z "$source" ] || [ ! -d "$source" ]; then
      if [ "$requirement" = "required" ]; then
        fail "FFmpeg libraries for $runtime_identifier are missing. Set TUFREPLAY_FFMPEG_$(printf '%s' "$runtime_identifier" | tr 'a-z-' 'A-Z_')_DIR."
      fi
      printf 'Skipping FFmpeg natives for %s (no source directory configured).\n' "$runtime_identifier" >&2
      continue
    fi

    for library in "${libraries[@]}"; do
      if [ ! -f "$source/$library" ]; then
        if [ "$requirement" = "required" ]; then
          fail "FFmpeg library $library is missing from $source."
        fi
        printf 'Skipping FFmpeg natives for %s (%s is missing).\n' "$runtime_identifier" "$library" >&2
        continue 2
      fi
    done

    target="$destination/native/$runtime_identifier"
    mkdir -p "$target"
    for library in "${libraries[@]}"; do
      cp "$source/$library" "$target/$library"
      chmod +x "$target/$library" 2>/dev/null || true
    done

    # Copy the transitive encoder/decoder libraries the core set links against. Their exact set
    # varies by FFmpeg build, so take whatever the source directory has rather than pinning names.
    case "$runtime_identifier" in
      osx)
        find "$source" -maxdepth 1 -name '*.dylib' -exec cp {} "$target/" \;
        chmod +x "$target"/*.dylib 2>/dev/null || true
        ;;
      win-x64)
        find "$source" -maxdepth 1 -name '*.dll' -exec cp {} "$target/" \;
        ;;
      linux-x64)
        find "$source" -maxdepth 1 -name '*.so.*' -exec cp -L {} "$target/" \;
        chmod +x "$target"/*.so.* 2>/dev/null || true
        ;;
    esac

    if [ "$runtime_identifier" = "osx" ] && is_macos; then
      # Unsigned dylibs copied out of a downloaded archive carry a quarantine flag that makes
      # dlopen fail. Clear it and ad-hoc sign.
      if command -v xattr >/dev/null 2>&1; then
        xattr -dr com.apple.quarantine "$target" 2>/dev/null || true
        xattr -dr com.apple.provenance "$target" 2>/dev/null || true
      fi
      if command -v codesign >/dev/null 2>&1; then
        for library in "$target"/*.dylib; do
          codesign --force --sign - "$library" >/dev/null 2>&1 || true
        done
      fi
    fi

    printf 'Staged FFmpeg natives for %s from %s\n' "$runtime_identifier" "$source"
  done
}

build() {
  log "Building TUFReplayRenderer"
  dotnet build "$PROJECT/TUFReplayRenderer.csproj" -v minimal
  log "Building TUFReplayRenderer.Loader"
  dotnet build "$ROOT/TUFReplayRenderer.Loader/TUFReplayRenderer.Loader.csproj" -v minimal
}

mod_version() {
  sed -n 's/.*"Version": "\([^"]*\)".*/\1/p' "$PROJECT/Info.json"
}

# Where the runtime downloader fetches FFmpeg from. Point this at wherever the ffmpeg-<rid>.zip
# release assets are hosted.
FFMPEG_RELEASE_BASE_URL="${TUFREPLAY_FFMPEG_RELEASE_BASE_URL:-https://github.com/kkorenn/TUFReplay-Renderer/releases/download/ffmpeg-n8.1}"

# Zips each platform's LGPL FFmpeg set into build/ffmpeg-<rid>.zip (flat archives, one per
# platform) — these are uploaded as release assets and downloaded by the mod at runtime.
make_ffmpeg_archives() {
  ensure_lgpl_ffmpeg
  local rid
  for rid in osx win-x64 linux-x64; do
    local archive="$ROOT/build/ffmpeg-$rid.zip"
    rm -f "$archive"
    (cd "$LGPL_FFMPEG_DIR/$rid" && zip -qX "$archive" ./*)
    log "Archived $archive"
  done
}

# Writes ffmpeg-manifest.json into <dest>: one entry per platform with URL filename, SHA-256, and
# size. The mod reads this at runtime to background-download the current platform's set.
write_ffmpeg_manifest() {
  local destination="$1"
  local rid archive sha size
  {
    printf '{\n'
    printf '  "Version": "n8.1",\n'
    printf '  "BaseUrl": "%s",\n' "$FFMPEG_RELEASE_BASE_URL"
    printf '  "Files": {\n'
    local first=1
    for rid in osx win-x64 linux-x64; do
      archive="$ROOT/build/ffmpeg-$rid.zip"
      [ -f "$archive" ] || fail "Missing $archive; run make_ffmpeg_archives first."
      sha="$(shasum -a 256 "$archive" | awk '{print $1}')"
      size="$(stat -f%z "$archive" 2>/dev/null || stat -c%s "$archive")"
      [ "$first" = 1 ] || printf ',\n'
      first=0
      printf '    "%s": { "FileName": "ffmpeg-%s.zip", "Sha256": "%s", "SizeBytes": %s }' "$rid" "$rid" "$sha" "$size"
    done
    printf '\n  }\n}\n'
  } > "$destination/ffmpeg-manifest.json"
  log "Wrote $destination/ffmpeg-manifest.json"
}

# Stages the full auto-updatable install layout into <dest>:
#   Info.json + TUFReplayRenderer.Loader.dll   (never updated by the auto-updater)
#   Runtime/Current.json                        (which payload the loader runs)
#   Runtime/versions/<version>/                 (the updatable payload: dll, deps, assets)
stage_payload() {
  local destination="$1"
  local out="$PROJECT/bin/Debug"
  local loader_out="$ROOT/TUFReplayRenderer.Loader/bin/Debug"
  local version
  version="$(mod_version)"
  [ -n "$version" ] || fail "Could not read the version from Info.json."
  local payload="$destination/Runtime/versions/$version"

  mkdir -p "$payload"
  cp "$PROJECT/Info.json" "$destination/"
  cp "$loader_out/TUFReplayRenderer.Loader.dll" "$destination/"
  printf '{\n  "SchemaVersion": 1,\n  "Current": "%s",\n  "Previous": null\n}\n' "$version" \
    > "$destination/Runtime/Current.json"

  cp "$out/TUFReplayRenderer.dll" "$payload/"
  cp "$out/FFmpeg.AutoGen.dll" "$payload/"
  [ -f "$out/TUFReplayRenderer.pdb" ] && cp "$out/TUFReplayRenderer.pdb" "$payload/" || true

  mkdir -p "$payload/Assets"
  cp -R "$PROJECT/Assets/render" "$payload/Assets/render"
}

# The directory the ffmpeg download manifest belongs in (the payload reads it from PayloadPath).
payload_dir() {
  printf '%s/Runtime/versions/%s\n' "$1" "$(mod_version)"
}

install_mod() {
  build
  local destination="$ADOFAI_MODS_DIR/$MOD_ID"
  log "Installing into $destination"
  rm -rf "$destination"
  stage_payload "$destination"
  # Local installs bundle the natives directly when a source is available (offline, instant), and
  # also carry the manifest so the runtime downloader can self-heal a wiped native directory.
  copy_ffmpeg_natives "$destination" optional
  make_ffmpeg_archives
  write_ffmpeg_manifest "$(payload_dir "$destination")"
  log "Installed $MOD_ID"
}

package() {
  build
  # The distributable ships WITHOUT FFmpeg: the mod downloads only the current platform's set in
  # the background at runtime, guided by ffmpeg-manifest.json. The per-platform ffmpeg-<rid>.zip
  # files produced here must be uploaded as release assets at $FFMPEG_RELEASE_BASE_URL.
  make_ffmpeg_archives
  local stage="$ROOT/build/package/$MOD_ID"
  log "Staging package at $stage"
  rm -rf "$stage"
  stage_payload "$stage"
  write_ffmpeg_manifest "$(payload_dir "$stage")"

  local archive="$ROOT/build/$MOD_ID.zip"
  rm -f "$archive"
  (cd "$ROOT/build/package" && zip -qr "$archive" "$MOD_ID")
  log "Packaged $archive"

  # update.json: the release asset the in-game auto-updater reads. MinBridgeApiVersion guards
  # against auto-updating into a renderer that the installed TUFReplay's bridge cannot serve.
  local sha version
  sha="$(shasum -a 256 "$archive" | awk '{print $1}')"
  version="$(mod_version)"
  printf '{\n  "Version": "%s",\n  "Zip": "%s.zip",\n  "Sha256": "%s",\n  "MinBridgeApiVersion": 1\n}\n' \
    "$version" "$MOD_ID" "$sha" > "$ROOT/build/update.json"
  log "Wrote build/update.json (upload it as a release asset next to the zip)"
  log "Release assets to upload alongside it: build/ffmpeg-{osx,win-x64,linux-x64}.zip -> $FFMPEG_RELEASE_BASE_URL"
}

case "${1:-}" in
  build) build ;;
  install) install_mod ;;
  package) package ;;
  prepare-lgpl-ffmpeg) shift; exec "$ROOT/scripts/tasks/prepare-lgpl-ffmpeg.sh" "$@" ;;
  *) fail "usage: $0 {build|install|package|prepare-lgpl-ffmpeg}" ;;
esac
