# Contributing

Thanks for taking a look. Issues and pull requests are both welcome.

## Before you start

- **Bugs**: say what you did, what happened, and what you expected. For playback problems, include the output
  of `fuplayer-cli devices` and the "Now playing" panel contents, which report where the filtering ran and what the
  chain looks like.
- **Features**: open an issue first if it is large, so the shape of the change can be settled before the work.
- **DSP changes**: see [Changing the DSP](#changing-the-dsp) below. Anything that touches the signal path needs
  a measurement, not an argument.

## Building

See [docs/building.md](docs/building.md). In short: .NET 9 SDK, `dotnet build -c Release`.

## Licence constraint on dependencies

Every dependency must be MIT, BSD, Apache-2.0, MS-PL or LGPL. **No GPL.** FFmpeg is used only as an LGPL build,
loaded as separate replaceable libraries, and never configured with `--enable-gpl`, `--enable-nonfree` or
`--enable-version3`. A pull request that adds a GPL dependency cannot be merged.

New dependencies also need an entry in `THIRD-PARTY-NOTICES.md` and a version in `Directory.Packages.props`,
which is central package management: versions live there, not in the project files.

## Code style

The project follows normal .NET conventions, with a few things worth knowing:

- **Nullable reference types are on.** So is `AllowUnsafeBlocks`, which the DSP inner loops use.
- **Comments explain why, not what.** A comment that restates the code is noise. A comment that records a
  measurement, a constraint or a decision that was not obvious is worth several.
- **No em-dashes.** Use a comma, a colon, a semicolon, or a full stop.
- **User-visible strings are short and technical.** A setting description says what the setting selects and the
  numbers that follow from it. It does not explain by analogy.
- **Avoid abbreviations in names.** `partitions`, not `parts`.

## Changing the DSP

The output is the product. Two rules:

**Measure, do not reason.** Performance claims need numbers from `fuplayer-cli bench` on a real file, best of at
least two runs, with the settings stated. The same applies to a change you expect to be neutral.

**Prove the output did not change.** If a change should be transparent, render the same file before and after at
32 bits with dither off and compare sample by sample:

```bash
fuplayer-cli render track.flac --out before --rate 352800 --taps 262144 --staging single --bits 32 --dither none
# apply the change, rebuild
fuplayer-cli render track.flac --out after  --rate 352800 --taps 262144 --staging single --bits 32 --dither none
```

The two files should differ by no more than the 32-bit format's own resolution, about -186 dB below peak.

If a change is *meant* to alter the output, say by how much and where, in dB.

## Tests

The test suite lives outside this repository, alongside the development tree. If you send a DSP change, describe
how you verified it and the numbers you got; that is what a reviewer will reproduce.

## Commit messages

A short imperative subject, then a body that explains why the change was needed and what it measured. The
history is the project's engineering record, so a commit that says what a diff already shows is a wasted
opportunity.

## Platform support

`FUPlayer.Core` contains no Windows-specific code, and it should stay that way. Platform code belongs in a
back-end project implementing `IAudioBackend`. A macOS or Linux back-end would be very welcome.
