# Contributing

Bug reports and pull requests are welcome. For playback bugs, include the version, source/output formats,
device and driver, relevant DSP settings, and Now playing's error/load text. See
[troubleshooting](docs/troubleshooting.md) for details to collect.

The [build guide](docs/building.md) covers local builds and packaging. Keep platform-specific audio code
outside the platform-neutral core. Match the existing C# style, add English and short Korean UI text,
and explain non-obvious choices in comments.

DSP changes need reproducible measurements: state the input format and settings, render before/after,
and compare aligned samples or spectra. Throughput claims should include hardware and benchmark settings.
The separate development tree has the automated tests; include your reproduction and checks with a PR.

Keep new dependencies compatible with FUPlayer's MIT distribution. GPL-only dependencies and proprietary
redistributable binaries are excluded; optional FFmpeg uses its replaceable LGPL build. Add licence texts
and a row in [third-party notices](THIRD-PARTY-NOTICES.md) for new dependencies.
