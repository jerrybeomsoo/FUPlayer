# Library, Tracks and queue playback

## Albums

Add music folders on the Library page. The scan reads tags and cover art in the background and caches them.
Within each folder, tracks are grouped by album title and album artist, ignoring case and surrounding whitespace.
The track artist does not split a compilation. Files without an album tag stay together as the folder's album.
Tracks in different folders remain separate albums even if their tags match.

Caches written before 0.5.1 are regrouped when loaded, so a flat music folder can display its separate albums
without a rescan. A folder cover is used when the folder holds one album. When a folder holds several albums,
each uses its embedded cover instead of sharing the folder cover.

## Tracks

The Tracks page under Library lists individual tracks across the library. Every word entered must appear in
the title, artist, album, album artist, genre or file name. Search ignores case, Japanese kana type and character
width, so half-width and full-width Japanese can match. The album page keeps its existing field-scoped query
syntax, such as `+artist:bach album:cello`.

Sort by title, artist, album, rating or recently changed. Each visible row loads a cover through the thumbnail
cache shared with the queue. Ratings are read from the files; this page does not write ratings.

| Field | Interpretation |
| --- | --- |
| FLAC/Ogg `RATING` | Values from 0 to 5 use a five-star scale; values above 5 use a 0-to-100 scale. |
| FLAC/Ogg `FMPS_RATING` | A 0-to-1 value mapped to zero to five stars. |
| ID3v2 `POPM` | Positive values mapped to Windows Media Player's star ranges. |
| ID3v2 TXXX `FMPS_Rating` | A 0-to-1 value mapped to zero to five stars. |

An unrated track has no stars. Old cached tracks are reread on the next update to obtain ratings, even when
their file size and modification time have not changed.

Double-click or Enter replaces the queue with that one track and starts it. The context menu can also play the
search results starting at the selected track, insert the track after the current queue item, append it, or show
its file in Explorer.

## Stop after this track

The transport button beside repeat enables a persistent mode: finish the current track and stop automatic
queue advancement. The toggle remains enabled after stopping and is restored when the desktop player starts
again. Next remains available for explicit navigation. The saved setting is `Playback.StopAfterCurrent`, off
by default.

Enable or disable the mode before the final buffered part of the track. The limitations below apply to 0.5.1.

## Known limitations

- Loading a cache with many albums in one folder repeats the folder-cover search for every album, even when
  that cover is discarded. In a local synthetic test with 4,683 file entries and 1,146 albums, cache loading
  took 7.7 to 8.8 seconds, versus 0.04 to 0.14 seconds before album regrouping. These are cache-loading
  measurements, not timings for scanning or decoding that many recordings.
- Stop-after-current is checked at decoder end, ahead of the audible end by the buffered audio. Enabling it
  in that interval can let the next track play; disabling it then can still stop playback. This reproduced
  in the final 100 ms of a two-second track with a measured FIFO capacity of 0.372 seconds.
- The desktop restores the stop toggle itself. Creating `PlaybackEngine` with
  `Playback.StopAfterCurrent=true`, or changing it through `ApplySettings`, does not synchronise the engine's
  separate property in this version. Engine consumers must set `PlaybackEngine.StopAfterCurrent` directly.

The release incorporates contributions from [PR #1](https://github.com/jerrybeomsoo/FUPlayer/pull/1),
[PR #2](https://github.com/jerrybeomsoo/FUPlayer/pull/2) and
[PR #3](https://github.com/jerrybeomsoo/FUPlayer/pull/3).
