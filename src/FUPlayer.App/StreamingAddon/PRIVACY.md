# FUPlayer Streaming privacy

This extension works with the local FUPlayer desktop application. It sends browser-permitted Apple
Music audio, current-track title/artist/album/artwork URL, and playback state to FUPlayer on the same
computer. Playback state includes position, duration, volume, shuffle and repeat. These are required
website-content and website-activity transfers, disclosed in Firefox's installation prompt.

Native messaging obtains the running player's local session settings. Audio and playback data use
an authenticated WebSocket bound to IPv4 loopback. Nothing is sent to the developer or an analytics
service. The extension does not collect browsing history, account credentials, payment information,
cookies, licences, DRM keys or provider network responses. It does not bypass browser media access
rules. Ordinary Apple Music authentication and provider traffic remain between Firefox and Apple.
FUPlayer may fetch a cover from the artwork URL published by the service to display it locally.

If optional technical/interaction consent is granted in Firefox, audio-graph state, media-element
counts and error names are sent to the local player for diagnostics. Without this consent, detailed
diagnostics are suppressed and only generic functional failures are shown. There is no telemetry.

The extension package contains no user's connection token, profile, sign-in, settings or music.
FUPlayer creates fresh connection details for each listening session. Closing FUPlayer invalidates
that session and pauses the captured media. The application keeps the dedicated Firefox profile's
sign-in; the extension does not copy or read profile files.
