# Music assets

`music_loop.mp3` is a **silent placeholder** in Git, committed so the game has a valid (silent)
music reference for anyone who clones the repo. A real track can be substituted locally. The
current substitute is internet-sourced with its licence unverified and is planned for replacement
before launch; keeping it out of Git does not establish permission to distribute it in a build.

## Substituting a track locally

Overwrite `music_loop.mp3` with a track you have permission to use — **keep the filename** so the AudioManager's
music slot stays valid — then tell git to ignore your local copy so it is never committed:

    git update-index --skip-worktree "Assets/Audio/Music/music_loop.mp3"

To re-track it later: `git update-index --no-skip-worktree "Assets/Audio/Music/music_loop.mp3"`.

## Release builds

Before distributing a build, replace the unverified track or document its author, source,
redistribution/commercial-use permissions, and required credits in
[THIRD_PARTY_NOTICES.md](../../../THIRD_PARTY_NOTICES.md).

Unity bakes the **assigned local clip** into the build, not the Git version. Check the actual
build audio and permissions even when the repository contains only the silent music placeholder.
