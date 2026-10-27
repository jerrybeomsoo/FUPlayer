# Updating FUPlayer

In **Settings**, select **Update FUPlayer**. It checks this project's latest stable GitHub release
and downloads a newer Windows x64 package. A progress bar and **Cancel download** are available.
Music can keep playing while the download is prepared. Checks run only when requested; no GitHub
account or background update service is required. Preview releases are not offered automatically.

After verification, choose **Install and restart**. FUPlayer closes normally, a separate helper waits
for it to exit, backs up replaced files, installs the package and restarts on Settings with the same
settings folder and language. Settings, queue, Firefox sign-in and separately installed FFmpeg files
are retained. The signed streaming add-on continues to use Firefox's normal update/permission flow.

The download must match the release's SHA-256 manifest and GitHub asset digest when supplied.
ZIP paths and extraction limits are checked before installation. The helper attempts rollback if file
replacement fails, then restarts the previous player when possible. A brief file-lock retry allows
Firefox's native-messaging process to finish; other applications are never killed for an update.

The installation folder must be writable. The updater does not request administrator elevation;
close other FUPlayer/CLI instances before installing. For a protected or incomplete installation,
download the ZIP manually and extract the complete package into a writable folder instead. If an
update fails, the status or helper dialog identifies a local log under `%LOCALAPPDATA%/FUPlayer/Updates`.
If rollback could not finish, retain the backup mentioned by that log for recovery. Large staging and
backup files are removed after a successful restart; completed helper folders are cleaned on a later check.

**The update button is available from v0.7.2 onward.** Upgrade earlier versions once by extracting
the [v0.7.2 ZIP](https://github.com/jerrybeomsoo/FUPlayer/releases/tag/v0.7.2) while the old player is closed.
Keep the whole extracted folder together, including `FUPlayer.Updater.exe` and the models.

This updates the Windows player installation. Standalone Windows/Linux DSP servers are separate;
use their matching release downloads and the [network guide](network-dsp.md) to update those servers.
