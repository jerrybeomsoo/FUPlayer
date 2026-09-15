# Third-party notices

FUPlayer's own source code is licensed under the [MIT License](LICENSE). It is built on the components below,
which keep their own licenses. None of them is licensed under the full GPL. Libraries under the LGPL (TagLib# and
the optional FFmpeg libraries) are used as separately replaceable dynamic libraries / assemblies, so the MIT code
can be combined with them; the LGPL 2.1 text is included in [licenses/LGPL-2.1.txt](licenses/LGPL-2.1.txt).

| Component | Used for | License | Source |
|---|---|---|---|
| [Avalonia UI](https://avaloniaui.net) 12 | Cross-platform user interface | MIT | https://github.com/AvaloniaUI/Avalonia |
| Avalonia.Fonts.Inter / Inter typeface | UI font | MIT (package) / SIL Open Font License 1.1 (font) | https://github.com/rsms/inter |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) 8 | MVVM helpers | MIT | https://github.com/CommunityToolkit/dotnet |
| [TagLib#](https://github.com/mono/taglib-sharp) 2.3 | Tag and cover-art reading | LGPL-2.1-only | https://github.com/mono/taglib-sharp |
| [FFmpeg](https://ffmpeg.org) 9.0.1 (optional) | Decoding of MP3, AAC/ALAC, WavPack, APE, Ogg, Opus, DST and more | LGPL-2.1-or-later (built without GPL/nonfree parts) | https://ffmpeg.org/download.html |
| xUnit.net (tests only) | Unit tests | Apache-2.0 | https://github.com/xunit/xunit |

## FFmpeg

FUPlayer calls FFmpeg's `libavformat`, `libavcodec` and `libavutil` through P/Invoke, and `libswresample`
travels with them because FFmpeg's Opus decoder needs it. The libraries are not statically linked; users
may replace them with their own builds of the same major versions (avformat 63, avcodec 63, avutil 61,
swresample 7). Build them with `build/ffmpeg/build-ffmpeg.ps1` on Windows or
`build/ffmpeg/build-ffmpeg-lgpl.sh` elsewhere, which refuses to continue unless FFmpeg reports the
"LGPL version 2.1 or later" license. When distributing binaries
that include FFmpeg DLLs, ship this notice, the FFmpeg license text (`COPYING.LGPLv2.1` from the FFmpeg source)
and the corresponding FFmpeg source code (or a written offer for it).

FFmpeg is a trademark of Fabrice Bellard, originator of the FFmpeg project.

## TagLib#

TagLib# is distributed under the GNU Lesser General Public License version 2.1. It is referenced as the
unmodified `TagLibSharp.dll` NuGet assembly, which can be replaced by the user.

## Algorithms and published data

- xoshiro256** random number generator, David Blackman and Sebastiano Vigna, public domain.
- Pink-noise filter coefficients, Paul Kellet's "refined" method, public domain.
- 5-tap psychoacoustic noise-shaping weights, S. P. Lipshitz, J. Vanderkooy, R. A. Wannamaker,
  "Minimally Audible Noise Shaping", J. Audio Eng. Soc. 39(11), 1991.
- Elliptic filter design via Landen transformations, S. J. Orfanidis, "Lecture Notes on Elliptic Filter Design".
- NTF synthesis with Legendre-optimised zeros and H∞-constrained poles follows the approach described in
  R. Schreier and G. C. Temes, "Understanding Delta-Sigma Data Converters" (implemented independently).

## Trademarks

ASIO is a trademark of Steinberg Media Technologies GmbH. DSD and Super Audio CD are trademarks of Sony
Corporation and Koninklijke Philips N.V. FUPlayer is an independent project and is not affiliated with or
endorsed by any of these companies.
