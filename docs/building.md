# Building from source

Most users should download the [release ZIP](https://github.com/jerrybeomsoo/FUPlayer/releases/latest),
which includes the runtime and neural models. Building is optional.

On Windows, install the .NET 9 SDK or a compatible newer SDK, clone the repository, and run PowerShell:

```powershell
dotnet build .\FUPlayer.sln -c Release
dotnet run --project .\src\FUPlayer.App -c Release
```

To produce the same five files as a release, including self-contained Windows/Linux x64 servers:

```powershell
pwsh .\build\release.ps1 -Version 0.6.0
```

The files appear in `artifacts\release-0.6.0`. The Windows ZIP includes both models and user guides.
FFmpeg and the DirectML redistributable are excluded. The player uses the DirectML library supplied by
Windows. Keep [licences and notices](../THIRD-PARTY-NOTICES.md) with redistributed builds.

For just a processing server, use `pwsh .\build\publish-dsp-server.ps1 -Runtime linux-x64`
or `-Runtime win-x64`. See [Network DSP](network-dsp.md) for deployment. The Linux server can also be
built on Linux with .NET 9 SDK and PowerShell 7. Normal host GPU/OS libraries are still required.
The desktop player currently targets Windows; a Linux server does not provide a Linux playback back-end.
