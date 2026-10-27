# Apple Music streaming

Play Apple Music inside FUPlayer with the same DSP and output choices as other live inputs.
You need **official Firefox 142 or newer**, an Internet connection and an Apple Music account with
access to the tracks you want to play. Firefox and its DRM components are managed by Firefox and
are not bundled with FUPlayer. Spotify and YouTube Music are not included in this release.

## First use

1. Install official desktop Firefox, then open **Streaming → Apple Music** in FUPlayer.
2. Open it using **Settings → Streaming → Open Apple Music** if it is not already open. FUPlayer includes its Mozilla-signed streaming add-on. On first use,
   confirm the normal add-on installation and permissions in Firefox. Audio, metadata and playback
   state go to FUPlayer on this computer; optional technical diagnostics stay local too.
3. Apple Music fills the Streaming → Apple Music content area. Browser setup and diagnostics are in
   **Settings → Streaming**; **Show inside player** is enabled there by default. Sign in and complete
   any subscription or two-factor prompts directly in Firefox. Do not share credentials with FUPlayer
   developers or include them in bug reports.
4. Start a track using Apple Music's own Play button once. This gives Firefox a normal user gesture
   for audio startup. FUPlayer starts receiving audio automatically.
5. Select your output device and processing in **Output** and **DSP studio**. The Network DSP stage
   checkboxes also apply to this input. **Now playing** shows the active chain, FIFO and DSP load.

Later, **Open Apple Music** loads the installed add-on automatically. There is no temporary manifest
or extension developer page to load each time. FUPlayer uses a dedicated Firefox profile; an add-on
installed in your regular Firefox profile is separate. The dedicated profile keeps its sign-in across
player launches. **Close browser** and player exit close that dedicated browser.
The playback **Stop** button keeps Apple Music open and clears active playback; Play can start it again.
The Streaming section heading only groups pages; select its Apple Music child to open the page.

## Controls and display

FUPlayer's play/pause, previous/next and progress bar control Apple Music. Shuffle and Repeat follow
Apple Music's modes rather than changing the local file queue. Current artwork, track title, artist
and album appear in Now playing and the bottom bar, and update on track changes.

While streaming, the volume knob controls **Apple Music's volume only**. FUPlayer's internal volume
gain stays at **0 dB**, avoiding a second reduction. Changing volume in either interface updates the
other. The saved volume for local files returns when the browser input closes.

The embedded browser has no address or tab bar. Its content fits the available player area when
resized; longer catalogue lists can still scroll. Setup windows are hidden during routine startup where
possible; Firefox installation/permission prompts can still require attention. In Settings → Streaming,
clear **Show inside player** to use a separate
chromeless Firefox window. Authentication windows can appear separately.

## Audio format and latency

Audio enters FUPlayer from Firefox's Web Audio graph as **48 kHz stereo float32**, before Windows
mixer processing. The normal browser speaker path is silent in this dedicated profile. This route
does not use Windows application capture. The selected FUPlayer output backend supplies the DAC.

Web Audio can resample the provider's decoded audio. This feature does not promise source-bit-perfect
playback, a particular Apple Music quality tier or the same formats as Apple's native applications.
Firefox's ordinary media-access and DRM restrictions remain enabled.

The bundled signed add-on is version **0.3.1**. Firefox may ask once to approve an update from an older
version. Audio transport remains continuous across normal track changes and pause/resume; seeks discard
stale queued audio. The displayed track and processing chain remain stable during transitions.

This is a live input: browser packet queues, DSP and FIFO add latency. File preprocessing cannot
remove that delay. Processing must keep up in real time; shorten filters or reduce optional stages
if Now playing reports sustained overload.

## If something fails

| Symptom | What to try |
| --- | --- |
| Firefox is not found | Use **Browse…** to select an official installed `firefox.exe`. |
| Apple Music plays but Direct input stays at zero | Click Play inside Apple Music once. Open **Settings → Streaming → Browser details** for input frames and any error. Optional graph diagnostics require Firefox's technical-data consent. |
| No audio despite increasing input frames | Check FUPlayer's selected output device, volume, active DSP load and any output error. |
| The add-on was removed or disabled | Close the dedicated browser, then use **Install add-on…** with the signed FUPlayer `.xpi`, or reopen its Firefox add-on settings and enable it. |
| Audio stops after a processing error | Check the output device and displayed error first, then Stop and Play. Reopen Apple Music if the browser bridge remains disconnected. Check DSP/FIFO and Network DSP settings before retrying. |
| A prompt blocks closing | Complete Firefox's pending prompt normally, then close again. |

The settings folder contains the dedicated `browser/firefox` profile and private sign-ins. Keep it
private and out of shared archives. Never copy a browser profile into a bug report or a release.
Read [streaming privacy](streaming-privacy.md) for the data exchanged with local FUPlayer.

Embedded Apple Music accepts normal keyboard input from v0.7.2. Click a text field or use Tab;
its caret/selection follows browser focus. Player controls and other applications can take focus
normally. This change does not require a new signed add-on.
