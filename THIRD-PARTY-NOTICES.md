# Third-party notices

FUPlayer's own source code, and the two trained models in `models/`, are licensed under the [MIT License](LICENSE).
The release package also contains the components below, each under its own license, and every license and notice
text they ask to travel with them is in [licenses/](licenses). None of them is licensed under the GPL. The LGPL
components (TagLib#, and the optional FFmpeg libraries) are separately replaceable dynamic libraries / assemblies,
so the MIT code can be combined with them; the LGPL 2.1 text is in [licenses/LGPL-2.1.txt](licenses/LGPL-2.1.txt).

What the Windows package contains:

| Component | Used for | License | Texts in `licenses/` |
|---|---|---|---|
| [.NET](https://github.com/dotnet/runtime) 9 runtime and libraries (the package is self-contained), System.Numerics.Tensors among them | Runtime | MIT, and the licenses of the code it builds in | `dotnet-runtime-LICENSE.txt`, `dotnet-runtime-THIRD-PARTY-NOTICES.txt` |
| [Avalonia UI](https://github.com/AvaloniaUI/Avalonia) 12, with MicroCom.Runtime and Tmds.DBus.Protocol | User interface | MIT | `Avalonia-LICENSE.txt`, `MicroCom-LICENSE.txt`, `Tmds.DBus-LICENSE.txt` |
| [Inter](https://github.com/rsms/inter) typeface, inside Avalonia.Fonts.Inter | Interface font | SIL Open Font License 1.1 | `Inter-OFL-1.1.txt` |
| [ANGLE](https://chromium.googlesource.com/angle/angle) (`av_libglesv2.dll`, Avalonia.Angle.Windows.Natives) | OpenGL ES over Direct3D, for drawing | BSD-3-Clause | `ANGLE-LICENSE.txt` |
| [SkiaSharp](https://github.com/mono/SkiaSharp) 3 and Skia (`libSkiaSharp.dll`) | 2-D graphics | MIT (SkiaSharp), BSD-3-Clause (Skia), and the licenses of the libraries Skia builds in | `SkiaSharp-HarfBuzzSharp-LICENSE.txt`, `SkiaSharp-HarfBuzzSharp-native-THIRD-PARTY-NOTICES.txt` |
| HarfBuzzSharp and [HarfBuzz](https://github.com/harfbuzz/harfbuzz) (`libHarfBuzzSharp.dll`) | Text shaping | MIT (HarfBuzzSharp), Old MIT (HarfBuzz) | the same two files |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) 8 | MVVM helpers | MIT | `CommunityToolkit-License.md`, `CommunityToolkit-ThirdPartyNotices.txt` |
| [TagLib#](https://github.com/mono/taglib-sharp) 2.3 | Tag and cover-art reading | LGPL-2.1-only | `LGPL-2.1.txt` |
| [ONNX Runtime](https://github.com/microsoft/onnxruntime) 1.30 (`Microsoft.ML.OnnxRuntime`, CPU build) | Running the neural restorer and upscaler | MIT, and the licenses of the code it builds in, Eigen's MPL-2.0 among them | `onnxruntime-LICENSE.txt`, `onnxruntime-ThirdPartyNotices.txt` |

Not in the package:

| Component | Used for | License | Source |
|---|---|---|---|
| [FFmpeg](https://ffmpeg.org) 9.0.1 (optional, built by the user) | Decoding of MP3, AAC/ALAC, WavPack, APE, Ogg, Opus, DST and more | LGPL-2.1-or-later (built without GPL/nonfree parts) | https://ffmpeg.org/download.html |
| xUnit.net (tests only) | Unit tests | Apache-2.0 | https://github.com/xunit/xunit |
| The training scripts' Python packages: PyTorch and torchaudio, NumPy, soundfile, ONNX, ONNX Runtime | Training the models, on the user's machine | BSD-3-Clause, BSD-2-Clause, BSD-3-Clause, BSD-3-Clause, Apache-2.0, MIT | installed with pip; see `training/*/requirements.txt` |
| qaac and Apple's CoreAudioToolbox (optional, training only) | Apple AAC copies for the training corpus | qaac: its own terms; CoreAudioToolbox: Apple's | never distributed with FUPlayer |

## The neural models

`models/neural-restorer.onnx` and `models/neural-upscaler.onnx`, and the `.json` description beside each, are this
project's own work: networks designed here, trained with the scripts in [training/](training), and licensed under the
same MIT License as the code. They were trained on commercially released recordings from the maintainer's own
library, used as training material only; the files hold network weights, and no audio from those recordings is in
them or distributed with them. Anyone who would rather use other weights can train their own from their own files
with the same scripts and put them in place of these; the player uses whichever it finds.

## Eigen, inside ONNX Runtime

`onnxruntime.dll` contains [Eigen](https://eigen.tuxfamily.org), a C++ template library under the Mozilla Public
License 2.0, used unmodified as part of ONNX Runtime. Its source code form is available at
https://gitlab.com/libeigen/eigen, and the MPL-2.0 text is in `licenses/onnxruntime-ThirdPartyNotices.txt`.

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

## ONNX Runtime

The neural restorer and upscaler run their networks through Microsoft's ONNX Runtime, referenced as the unmodified
`Microsoft.ML.OnnxRuntime` NuGet package (MIT); its third-party notices are in `licenses/`. Only the CPU execution
provider is used.

## Algorithms and published data

- xoshiro256** random number generator, David Blackman and Sebastiano Vigna, public domain.
- Pink-noise filter coefficients, Paul Kellet's "refined" method, public domain.
- 5-tap psychoacoustic noise-shaping weights, S. P. Lipshitz, J. Vanderkooy, R. A. Wannamaker,
  "Minimally Audible Noise Shaping", J. Audio Eng. Soc. 39(11), 1991.
- Elliptic filter design via Landen transformations, S. J. Orfanidis, "Lecture Notes on Elliptic Filter Design".
- NTF synthesis with Legendre-optimised zeros and H∞-constrained poles follows the approach described in
  R. Schreier and G. C. Temes, "Understanding Delta-Sigma Data Converters" (implemented independently).
- The neural upscaler's design (a Fourier-domain ConvNeXt network with magnitude and phase outputs, multi-resolution
  STFT and anti-wrapping phase losses, multi-period and multi-resolution discriminators) follows published work,
  implemented independently: H. Siuzdak, "Vocos", arXiv:2306.00814; Y.-X. Lu et al., "Towards High-Quality and
  Efficient Speech Bandwidth Extension with Parallel Amplitude and Phase Prediction" (AP-BWE), arXiv:2401.06387;
  Z. Liu et al., "A ConvNet for the 2020s", arXiv:2201.03545; J. Kong et al., "HiFi-GAN", arXiv:2010.05646.
- The neural restorer's masked loss uses the textbook psychoacoustic model, implemented independently: the Bark scale
  (E. Zwicker, E. Terhardt, J. Acoust. Soc. Am. 68, 1980), the spreading function of M. R. Schroeder, B. S. Atal and
  J. L. Hall (J. Acoust. Soc. Am. 66, 1979), J. D. Johnston's tonality offsets (IEEE JSAC 6(2), 1988) and E.
  Terhardt's absolute threshold (Hearing Research 1, 1979). Its evaluation forms the noise-to-mask ratio and the
  share of disturbed frames the way ITU-R BS.1387 (PEAQ) describes them. What it knows of the codecs it undoes comes
  from their published specifications: RFC 6716 (Opus) and ISO/IEC 13818-7 / 14496-3 (AAC).

## Trademarks

ASIO is a trademark of Steinberg Media Technologies GmbH. DSD and Super Audio CD are trademarks of Sony
Corporation and Koninklijke Philips N.V. Apple Music, Spotify, YouTube and Firefox, named where the documentation
describes what a capture carries, are trademarks of their owners. FUPlayer is an independent project and is not
affiliated with or endorsed by any of these companies. FUPlayer's ASIO support implements the driver interface
itself and contains no part of Steinberg's ASIO SDK.
