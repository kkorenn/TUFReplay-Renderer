# Third-Party Notices

## ADOFAIRenderer

TUFReplay-Renderer is derived from ADOFAIRenderer, which is licensed under the MIT License.

- Copyright (c) 2026 ADOFAI Editor Forum
- Source: https://github.com/ADOFAI-Editor-Forum/ADOFAIRenderer

Ported components include the native FFmpeg encoder wrapper, the asynchronous GPU frame reader, the
offline audio mixer and its filter chain, the rational frame-rate and audio-sync helpers, the GPU
vendor to encoder mapping, and the time/screen/audio virtualization Harmony patches.

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and
associated documentation files (the "Software"), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute,
sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or
substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT
NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM,
DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT
OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

## FFmpeg.AutoGen

TUFReplay-Renderer uses FFmpeg.AutoGen for its FFmpeg bindings, licensed under the GNU Lesser General Public
License v3.0.

- Copyright: Ruslan Balanukhin and contributors
- Source: https://github.com/Ruslan-B/FFmpeg.AutoGen

## FFmpeg (LGPL)

Replay rendering uses the FFmpeg shared libraries (`libavcodec`/`avcodec`, `libavformat`/`avformat`,
`libavutil`/`avutil`, `libswresample`/`swresample`, `libswscale`/`swscale`). The libraries
distributed with TUFReplay-Renderer are built under the **GNU Lesser General Public License, version 2.1 or
later (LGPL-2.1+)**, configured **without** `--enable-gpl` and **without** `--enable-nonfree`, so no
GPL-only component (such as `libx264`, `libx265`, or `libvvenc`) is included. FFmpeg therefore does
not impose its license on the rest of TUFReplay-Renderer.

- Copyright: the FFmpeg developers
- Source: https://ffmpeg.org/
- License text: https://www.ffmpeg.org/legal.html and the `COPYING.LGPLv2.1` file in the FFmpeg source

TUFReplay-Renderer does not modify FFmpeg. It uses FFmpeg only through the published public API of the
unmodified shared libraries, which are loaded dynamically at runtime as separate files. LGPL section
6 (use of the library) is satisfied because the libraries are separate, replaceable shared objects:
a user may substitute their own compatible FFmpeg 8.x build (matching the SONAME major versions
`avcodec 62`, `avformat 62`, `avutil 60`, `swresample 6`, `swscale 9`) either by replacing the files
in `native/<platform>/` inside the installed mod or by pointing the `TUFREPLAY_FFMPEG_DIR`
environment variable at their own build.

### Written offer for FFmpeg source

The complete corresponding source code for the exact FFmpeg version distributed with this release is
available at https://github.com/FFmpeg/FFmpeg at the tag matching the shipped version (currently
`n8.1`). In addition, for any binary release, the release notes link the exact source tag, and the
project will provide the corresponding source on request for as long as the binaries are
distributed.

Do NOT replace the bundled LGPL build with a GPL build (for example, one that enables `libx264` or
`libx265`) unless you intend to distribute the whole of TUFReplay-Renderer under the GPL. Doing so changes
the licensing obligations of the entire distribution.
