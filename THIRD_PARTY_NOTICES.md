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

## Audio: provenance unresolved; replacement required before launch

The following sounds were downloaded from the internet. Their original authors,
source URLs, and redistribution licences have not yet been documented:

- `Assets/Audio/SFX/jump_bfxr.wav`
- `Assets/Audio/SFX/dash_bfxr.wav`
- `Assets/Audio/SFX/land_bfxr.wav`
- `Assets/Audio/SFX/swap_bfxr.wav`

The `_bfxr` filenames do not establish authorship or permission. These files are
tracked in the source repository, so source redistribution also needs to be
resolved. Replace them with original or appropriately licensed sounds, or document
the original permissions and required credits before further redistribution.
Listing them here does not grant permission or establish licence compliance.

The tracked `.mp3` SFX and music were introduced as silent placeholders. A local
checkout can substitute a real track into `Assets/Audio/Music/music_loop.mp3`;
Unity includes the assigned local audio in a build even if Git ignores that change.
The current substituted music is also internet-sourced with unverified licensing
and is planned for replacement before launch. Do not infer commercial rights from
the earlier music README's description of it as "licensed". See the
[music notes](Assets/Audio/Music/README.md) for the replacement workflow.

Before distributing a build, replace or verify every real audio clip and record
its creator, source, licence, and any required attribution here. A silent placeholder
in Git is not evidence that a locally built player contains only silent audio.

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
check that each file matches its source. Presence of notices does not clear the
unresolved audio permissions above.
