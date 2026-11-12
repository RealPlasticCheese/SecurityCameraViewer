# Third-party notices

## Self-contained server packages

Installable server packages include the .NET runtime and MediaMTX 1.21.1. Their
notices accompany the binaries as `DotNet-LICENSE.txt`,
`DotNet-THIRD-PARTY-NOTICES.txt`, and `MediaMTX-LICENSE.txt`.
Sources: [dotnet/runtime](https://github.com/dotnet/runtime) and
[MediaMTX](https://github.com/bluenviron/mediamtx/tree/v1.21.1).
FFmpeg is installed separately by the OS package manager or Windows Package
Manager; its native executable is not redistributed in these packages. The browser
decoder's existing sources and notices remain under `wwwroot/vendor/camera-decoder`.

## pyswann 0.0.29

The RaySharp YSF wire permutation and encrypted message/login framing were adapted to C# from `src/swann/protocol.py` in [pyswann 0.0.29](https://pypi.org/project/pyswann/0.0.29/). No Python package or vendor binary is required at runtime.

Source archive SHA-256: `a63be2cbb53b952ab8515c9540db3e1b7e7bb6f09b36783047f9bfef3cff9cd9`.

MIT License

Copyright (c) 2026 Brendan

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

## Lucide playback icons

Selected SVG geometry in `wwwroot/index.html` comes from [Lucide](https://github.com/lucide-icons/lucide/tree/70562c1ee1c4fdcf736fe97bc893fb8511927934/icons), revision `70562c1ee1c4fdcf736fe97bc893fb8511927934`. Icons are embedded locally and require no CDN or package at runtime. Reverse play mirrors the Play icon with CSS. The ISC and Feather MIT notices are distributed in [`wwwroot/vendor/lucide/LICENSE`](src/SecurityCameraViewer.Web/wwwroot/vendor/lucide/LICENSE).
