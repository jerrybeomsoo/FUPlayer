# Building from source

Most users should download the [release ZIP](https://github.com/jerrybeomsoo/FUPlayer/releases/latest),
which includes the runtime and neural models. Building is optional.

On Windows, install the .NET 9 SDK or a compatible newer SDK, clone the repository, and run PowerShell:

```powershell
dotnet build .\FUPlayer.sln -c Release
dotnet run --project .\src\FUPlayer.App -c Release
```

To produce the same six files as a release, including self-contained Windows/Linux x64 servers:

```powershell
pwsh .\build\release.ps1 -Version 0.7.2
```

The files appear in `artifacts\release-0.7.2`. The Windows ZIP includes both models and user guides.
FFmpeg and the DirectML redistributable are excluded. The player uses the DirectML library supplied by
Windows. Keep [licences and notices](../THIRD-PARTY-NOTICES.md) with redistributed builds.

For just a processing server, use `pwsh .\build\publish-dsp-server.ps1 -Runtime linux-x64`
or `-Runtime win-x64`. See [Network DSP](network-dsp.md) for deployment. The Linux server can also be
built on Linux with .NET 9 SDK and PowerShell 7. Normal host GPU/OS libraries are still required.
For the portable Windows Forms controller, run `pwsh .\build\publish-dsp-server-windows.ps1`.
The desktop player currently targets Windows; a Linux server does not provide a Linux playback back-end.

The Windows player includes the project's Mozilla-signed streaming add-on as an embedded resource.
It does not include Firefox or a browser profile. The extension source is under
`src/FUPlayer.App/StreamingAddon`; `build/firefox-addon.ps1` copies its readable source into a signing ZIP.
Changing the extension requires a new Mozilla-signed version before replacing the bundled XPI.

The player ZIP also includes `FUPlayer.Updater.exe`, a self-contained helper published by
`build/package.ps1`. `FUPlayer.Update` contains release validation and transactional file replacement;
the helper runs from temporary staging so it can replace the closed player's files. No additional
third-party package or separately installed runtime is required for updates.
