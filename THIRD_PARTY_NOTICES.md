# Third-party notices

The project's all-rights-reserved notice does not replace the licences of bundled
third-party material. This inventory covers the fonts, artwork, and audio added to
the game. Unity and Package Manager dependencies retain their own terms.

## Inter

- Files: `Assets/Fonts/Inter-Regular.ttf` and its generated TextMesh Pro SDF asset.
- Embedded version: `4.001;git-9221beed3`.
- Copyright (c) 2016 The Inter Project Authors (https://github.com/rsms/inter).
- Licence: SIL Open Font License 1.1, reproduced in full in
  [Assets/Fonts/Inter-OFL.txt](Assets/Fonts/Inter-OFL.txt).
- Source: [Inter](https://rsms.me/inter/). The bundled notice is copied unchanged
  from [the font's upstream revision](https://github.com/rsms/inter/blob/9221beed3/LICENSE.txt).
- Build copy: `Legal/Inter-OFL.txt` inside StreamingAssets.

The original font and its generated font asset remain subject to the OFL, not the
project's proprietary notice. The upstream copyright statement lists no Reserved
Font Name.

## Liberation Sans

- Files: `Assets/TextMesh Pro/Fonts/LiberationSans.ttf` and the generated
  `LiberationSans SDF` assets shipped with the TextMesh Pro resources.
- Digitized data copyright (c) 2010 Google Corporation, with Reserved Font Arimo,
  Tinos and Cousine. Copyright (c) 2012 Red Hat, Inc., with Reserved Font Name
  Liberation.
- Licence: SIL Open Font License 1.1. The original notice and full terms are in
  [LiberationSans - OFL.txt](Assets/TextMesh%20Pro/Fonts/LiberationSans%20-%20OFL.txt).
- Build copy: `Legal/LiberationSans-OFL.txt` inside StreamingAssets.

## Kenney Pixel Platformer

- Files: Kenney-sourced artwork under `Assets/Sprites/Kenney/`.
- Creator: Kenney (https://kenney.nl/), Pixel Platformer pack version 1.2.
- Licence: [Creative Commons CC0 1.0](https://creativecommons.org/publicdomain/zero/1.0/).
- Source: [Pixel Platformer](https://kenney.nl/assets/pixel-platformer).
- Original distribution notice:
  [Kenney-Pixel-Platformer-CC0.txt](Assets/Sprites/Kenney/Kenney-Pixel-Platformer-CC0.txt).
- Build copy: `Legal/Kenney-Pixel-Platformer-CC0.txt` inside StreamingAssets.

The project notice does not assert exclusive rights over the CC0 artwork.

## Audio

The four gameplay effects were synthesized for JumpNowBro on 2026-10-07 with
Codex assistance, using only mathematical oscillators and seeded noise. No external
recordings, samples, soundfonts, or downloaded audio were used. Their complete source
recipe is [scripts/generate-sfx.py](scripts/generate-sfx.py); run it with Python 3 to
regenerate the 44.1 kHz, mono, 16-bit PCM WAVs. The source and generated clips are
project material covered by the repository's [LICENSE](LICENSE), with no additional
third-party audio attribution.

| File under `Assets/Audio/SFX/` | Cue | Duration |
| --- | --- | --- |
| `player_jump.wav` | Rising tone | 280 ms |
| `player_land.wav` | Short low impact | 60 ms |
| `player_dash.wav` | Noise and falling tone | 240 ms |
| `control_swap.wav` | Two-tone chime | 420 ms |

These replace the four undocumented downloaded `*_bfxr.wav` files. The old clips
are absent from the current asset set; this replacement does not establish rights
for their earlier versions in Git history.

`Assets/Audio/Music/music_loop.mp3` and the four communication MP3s (`callout`,
`ping`, `count_beat`, `count_go`) are silent placeholders. The undocumented local
music substitution was removed from the project. Communication sound replacements
remain tracked separately in issues #155–#158.

For future audio additions, record the creator, source, terms, and required credits
here. Unity builds use the local assets, so check the actual build inputs as well as
Git. See the [music notes](Assets/Audio/Music/README.md).

## Unity and packages

Unity, the imported TextMesh Pro resources, and dependencies listed in
`Packages/manifest.json` are not relicensed by the project notice. Keep their vendor
licences and notices, including any notices supplied in player output. Consult the
installed package licence/third-party-notice files when changing dependencies or
preparing a release; this asset inventory is not a replacement for those terms.

## Notice files in player builds

`Assets/Editor/LegalBuildProcessor.cs` registers these existing files with Unity's
build pipeline, which copies them unchanged into the player's StreamingAssets:

- `Legal/LICENSE.txt` — the project's notice.
- `Legal/THIRD_PARTY_NOTICES.md` — this inventory.
- `Legal/Inter-OFL.txt` — Inter's complete upstream notice/licence.
- `Legal/LiberationSans-OFL.txt` — the bundled Liberation Sans notice/licence.
- `Legal/Kenney-Pixel-Platformer-CC0.txt` — Kenney's distribution notice.

The processor is editor-only and uses
[AddAdditionalPathToStreamingAssets](https://docs.unity3d.com/6000.4/Documentation/ScriptReference/Build.BuildPlayerContext.AddAdditionalPathToStreamingAssets.html).
It copies the canonical files at build time, avoiding manually synchronized copies.
A missing source notice makes the build fail. For Windows/Linux players the files
are under `<Player>_Data/StreamingAssets/Legal`; on macOS they are inside the app
bundle at `Contents/Resources/Data/StreamingAssets/Legal`.

When verifying a release, inspect that directory in the actual built player and
check that each file matches its source and that the audio matches this inventory.
