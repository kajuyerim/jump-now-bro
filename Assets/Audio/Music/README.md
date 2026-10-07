# Music assets

`music_loop.mp3` is a silent placeholder in both Git and the project. The earlier
undocumented local track has been removed from the asset set. The AudioManager's
music reference remains valid, so replacing it later needs no scene rewiring.

## Adding music

Use a track with documented permission for its intended use and distribution.
Record its creator, source, terms, and required credits in
[THIRD_PARTY_NOTICES.md](../../../THIRD_PARTY_NOTICES.md), then replace
`music_loop.mp3` while keeping its `.meta` file. Git LFS tracks the audio normally;
do not hide substitutions with `skip-worktree`.

Unity bakes the assigned local clip into the build. Verify the actual build inputs
and packaged notices before distribution, even if the Git version is still silent.
