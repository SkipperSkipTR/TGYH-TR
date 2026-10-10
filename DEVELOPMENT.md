# Development

The patch is a BepInEx 6 (IL2CPP) plugin. It never modifies game files: text, font,
textures and videos are swapped in memory while the game runs.

## Repository layout

```
Plugin/           the plugin (TGYHTurkishPatch.dll)
Installer/        Windows installer (TGYH_TR.exe, WPF, .NET Framework 4.7.2)
install.sh        installer for macOS / Linux / Steam Deck
TextureBuilder/   offline encoder used by the release tools
tools/            build_release.py, build_texture_pack.py, find_unmodified_textures.py
texts/            the translation, copied here by build_release.py
```

Not in the repository (too large, and derived from game data): the replacement textures,
videos and the font bundle. They live in the installed plugin folder and are shipped only
inside release packages.

## Working copy

Clone the repository **into the game folder as `mod_files`**:

```
<game folder>/
├── BepInEx/plugins/TGYHTurkishPatch/    installed, working copy of the mod
│   ├── TGYHTurkishPatch.dll
│   ├── texts/       translation CSV / JSON files
│   ├── textures/    replacement textures: .png (editable) or .tgyc (prebuilt)
│   ├── videos/      replacement .webm / .mp4 / .mov, named after the VideoClip
│   ├── fonts/       AssetBundle containing a TMP_FontAsset
│   └── cache/       generated; safe to delete, never ship
└── mod_files/       this repository
```

The game needs BepInEx 6 installed and started once, so that `BepInEx/interop` exists; the
plugin compiles against those assemblies. Then:

```
dotnet build Plugin/TGYHTurkishPatch.csproj -c Release
```

The DLL is written straight into `BepInEx/plugins/TGYHTurkishPatch/`. With the repository
somewhere else, add `-p:GameDir="<game folder>"`.

## How the plugin works

- **Language** — "Türkçe" is the first entry under Options > Language. The game only knows
  its own 12 languages, so while Turkish is active the game itself runs in English and the
  plugin overlays everything else. Picking any other language hands control back to the
  game's own code. The choice is stored in PlayerPrefs (`TGY_CustomLanguage`).
- **Texts** — `texts/*.csv` with an `id` column plus a translation column (`translated_text`,
  `turkish`, `tr`, ...), or `texts/*.json` as `"id or English text": "translation"`. The
  game's master table has 1,700 lines; a row whose translation equals the English is skipped.
- **Textures** — matched by Texture2D name. Dump-style suffixes in file names
  (`-CAB-<hash>-<pathid>`, `-sharedassetsN.assets-<pathid>`) are ignored. Loaded textures are
  polled a few times a second; a match is prepared on a background thread and uploaded on the
  main thread. PNGs are encoded into the texture's own GPU format on first use and cached in
  `cache/`; `.tgyc` files are already in that format and need no work on the player's machine.
- **Sprite outlines** — the game draws each sprite through a tight outline mesh around its
  original artwork, so translated text painted slightly outside that outline is in the
  texture but never drawn. `build_texture_pack.py` compares each PNG with the original,
  finds repainted pixels no sprite outline covers, and writes extra rectangles for the
  sprite they belong to into `textures/sprites.txt`. The plugin appends them to the sprite's
  mesh (`Sprite.OverrideGeometry`) and removes them when Turkish is switched off. Two cases
  cannot be recovered and are listed at the end of a build: pixels painted outside the
  sprite's own rectangle in the atlas, and pixels touching another sprite's artwork.
- **Videos** — a VideoPlayer whose clip (or GameObject) name matches a file in `videos/` is
  redirected to that file.
- **Font** — the TMP_FontAsset from the bundle replaces the game's fonts while Turkish is
  active. Its material is overwritten at runtime with the game's own Smile material settings
  (Mobile/Distance Field shader, face dilate 0.28, softness 0.245). The font asset should
  contain every character the translation uses, including "…".

## Editing the translation

Work in `BepInEx/plugins/TGYHTurkishPatch/` and press **F9** in-game (while Turkish is
active) to reload texts, textures, videos and the font without restarting.

Only keep PNGs you actually changed in `textures/`. `tools/find_unmodified_textures.py`
lists PNGs identical to the game's originals; `--move` moves them out.

## Configuration

`BepInEx/config/com.tgyh.turkishpatch.cfg`, created on first launch:

| Section | Key | Default | Meaning |
|---|---|---|---|
| General | Enabled | true | Master switch |
| Features | EnableTextReplacements / EnableTextureReplacements / EnableVideoReplacements / EnableFontReplacements | true | Per-feature switches |
| Controls | ReloadHotKey | F9 | Hot-reload key (F1-F12, A-Z, Alpha0-Alpha9; Windows/Proton only) |
| Textures | EnableDiskCache | true | Cache encoded PNGs in `cache/textures` |
| Textures | KeepOriginals | true | Keep originals in VRAM so switching language restores them |
| Textures | WorkerThreads | up to 3 | Background threads preparing textures |
| Textures | ScanInterval | 0.1 | Seconds between checks for newly loaded textures |
| Debug | VerboseLogging | false | Log every replacement |

## Publishing a release

1. Set `Version` in `Plugin/Plugin.cs`.
2. Run `python tools/build_release.py` (needs the .NET SDK and `pip install UnityPy Pillow`).
   It writes two files to `dist/`:
   - `TGYHTurkishPatch-v<version>.zip` — the plugin package. No BepInEx inside.
   - `TGYH_TR.exe` — the Windows installer.
3. On GitHub create a release tagged `v<version>` and attach the zip. Both installers use
   the newest release that has a `TGYHTurkishPatch*.zip` attached, and check it against the
   SHA-256 GitHub publishes for the file.
4. If the installer changed, replace `TGYH_TR.exe` in the `Kurulum` release; the README
   links to that file.

To try a package before publishing, put the zip next to `TGYH_TR.exe`; the installer then
uses it instead of downloading.

BepInEx is not redistributed. The installers download the pinned build
(`6.0.0-be.788`, see `BepInExBuild` in `Installer/PatchInstaller.cs` and `install.sh`) from
builds.bepinex.dev, choosing win-x86, win-x64 or macos-x64 to match the game.

The installers also set `UnityLogListening = false` in `BepInEx/config/BepInEx.cfg`. With
BepInEx's default (`true`) it injects a class into the game to forward Unity's log, and on
this game that crashes at startup: the log ends with "Class::Init signatures have been
exhausted, using a substitute!" and `ErrorLog.log` shows an access violation. For the same
reason the plugin must not use Il2CppInterop class injection or delegate conversion.

### Other builds of the game (macOS, other stores)

Sprite atlases are named `sactx-…-<hash>`, and the hash differs between builds even when the
atlas is pixel-identical; the macOS build, for example, shares only 3 of 90 names with
Windows. One package still serves every build, because the plugin recognises atlases by
picture:

- `build_texture_pack.py` stores a small fingerprint of each **original** atlas in
  `textures/signatures.txt` (alpha and green on a 32x32 grid).
- When the game loads an atlas whose name matches a replacement except for the hash, the
  plugin takes the same fingerprint from the loaded texture on the GPU. If it agrees, the
  replacement is used; if the atlas was repacked into a different layout, it is left alone
  and the log says so.

Nothing has to be generated per build. While editing with PNGs, run
`python tools/build_texture_pack.py --signatures` to (re)write `signatures.txt` and `sprites.txt`
next to them.

A replacement still needs the atlas to have the same size, format and layout as the one it
was made from. After a game update that repacks atlases, build the release again on the
updated game; textures that no longer fit are skipped with a log message.
