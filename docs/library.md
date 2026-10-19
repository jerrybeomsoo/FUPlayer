# Library, Tracks and queue

## Albums

Add music folders in Library. Tracks are grouped within each folder by album title and album artist;
different track artists do not split a compilation. Files without an album tag use the folder's album.
Identically tagged albums in different folders remain separate. Older caches regroup automatically.

Cover images are selected from embedded art or recognisable filenames such as `cover`, `front`,
`folder`, `albumart`, `artwork`, or the album's title. Names such as `front01` and
`Artist - Album Title - cover` work. An unrelated image is never chosen just because it is the only one
in the folder. An album-title match takes precedence over a generic image. A folder containing several
albums uses album-specific filenames or embedded covers, rather than sharing one generic cover.
This also applies to files on the Desktop. The thumbnail policy refreshes old arbitrary-image selections.

## Tracks

The Tracks page searches individual files across the library. Every search word must match a title,
artist, album, album artist, genre or filename. Search ignores case, kana type and character width.
The Albums page also supports field searches such as `+artist:bach album:cello`.
Sort Tracks by title, artist, album, rating or recently changed. Ratings are read from file tags;
FUPlayer does not edit them here.

Double-click or Enter plays the selected track alone. The context menu can play the results from that
track, play next, append to the queue or show its file in Explorer.

## Stop after this track

The transport toggle beside repeat finishes the current track and stops automatic advancement.
It remains enabled after stopping and is saved between desktop runs. Next still navigates explicitly.

## Known limitations

Set Stop after this track before the final buffered part of a track. A very late change can still let
the next track play, or still stop after the toggle is disabled. v0.6.0 does not fix that boundary case.
Library startup may take longer with very large flat folders, although repeated image discovery per
album has been removed. See [troubleshooting](troubleshooting.md) for reporting a problem.
