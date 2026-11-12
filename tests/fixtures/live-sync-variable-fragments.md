`live-sync-variable-fragments.json` contains three anonymized live fragment timing
traces: media starts/ends in seconds, fragment production offsets in milliseconds,
and receipt offsets in milliseconds. Every stream has an independent media origin;
production and receipt offsets each share an origin across the three streams.
No camera identifiers, absolute timestamps, addresses, credentials or images are
included.

The traces exercise alternating 1.013/0.394-second and 1.106/0.861-second fragments
with ordinary production and delivery jitter. A browser simulation advances the
videos every 20 ms, delivers these fragments, and checks steady playback after
startup. It rejects backwards pictures, backwards or instantaneous UTC cursor
jumps, excessive forward seeks, and frozen players. The pre-fix shared live clock
fails because every arrival can change its playback-delay anchor.

Server rewind tests also replay these traces and check every millisecond advertised
by the coverage snapshot. Each timestamp must produce a playable keyframe playlist
whose fragment timing maps that timestamp into retained media.
