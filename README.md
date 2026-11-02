# Mass Effect Legendary Edition Mod Installer

Automates the busywork of the [Mass Effect Legendary Edition modding guide](https://www.nexusmods.com/masseffectlegendaryedition/mods/1127) on Nexus Mods: it downloads every mod and texture the guide lists, puts them where ME3Tweaks Mod Manager expects them, and installs the `.mem` textures with Mass Effect Modder. Runs on Windows and Linux, as a desktop app or a command-line tool.

> **Status: early (0.1.0).** It has been run end to end on one machine (Linux, Steam/Proton prefix, all three games). See [What has and has not been verified](#what-has-and-has-not-been-verified) before you rely on it, and make Mod Manager backups first.

## What it does for you

| Step | Done by |
|---|---|
| Fetch the guide's current install groups (`.biq2`) from its Nexus page | this app |
| Download and unpack ~230 mods (Nexus Premium API) | this app |
| Download ALOT, ISL (a torrent) and the guide's texture mods, pick the files the guide says to pick | this app |
| Download ME3Tweaks Mod Manager and Mass Effect Modder (and, on Linux, create a Proton prefix for Mod Manager) | this app |
| Install the content mods and ALOT/ISL into the game | **you, in Mod Manager's Batch Mod Installer** (Mod Manager has no command line for this; the app puts the groups where it finds them and waits) |
| Install the `.mem` textures into the game | this app, through Mass Effect Modder, after you confirm |
| Citadel DLC setting in EGM, Shepard hair mods | **you** (listed at the end; they cannot be automated) |

## Requirements

- **A Nexus Mods Premium account.** Free accounts cannot get download links from the API, so the app refuses to run without Premium.
- A **personal API key** (nexusmods.com, your profile, API keys).
- Mass Effect Legendary Edition installed (the folder that contains `Game`, `Launcher` and `Support`).
- **Mod Manager backups** of every game you want to mod (Mod Manager: Mod Management, then Backup). The app will not touch a game without a valid one.
- **7-Zip.** Release packages bundle it (`7zip/` next to the program). If you build from source you need `7z`/`7zz` on your `PATH` (Windows: `winget install 7zip.7zip`).
- **Linux only:** [`umu-launcher`](https://github.com/Open-Wine-Components/umu-launcher) (`umu-run`) and a GE-Proton build, to run Mod Manager. Not needed if you only use the command line against an existing Mod Manager prefix.
- **Disk space:** plan for **150 GB or more** of working space (the texture packs are large; archives are deleted once unpacked, so the peak is lower than the total). The app stops at 40 GB free.

## Quick start (desktop app)

1. Download the package for your system from the Releases page, unpack it, and run `MELEAutoModInstaller` (`.exe` on Windows).
2. **Nexus account:** paste your API key and press *Save and check*.
3. **Your game:** choose the game folder and tick the games you want. Untick textures if you only want the mods.
4. **Mod Manager:** press *Set up Mod Manager* (downloads it; on Linux also creates the Proton prefix). Open it, add your game (on Linux, give Mod Manager the path through the `Z:` drive, e.g. `Z:\home\you\Games\Mass Effect Legendary Edition`), and **make a backup of each game**. Back in this app, *Refresh* under Backups.
5. Press **Check first** if you want a read-only preview, then **Start**.
6. When it says *Your turn*, open Mod Manager's **Batch Mod Installer**, load the listed groups (they are already in its list) and install them one game at a time. Press *I have installed them*.
7. Confirm the texture install when asked. It takes 15 to 40 minutes per game.

You can press **Stop** at any time. Running again continues where it left off; nothing finished is redone.

## Command line

```
mele plan     --game-dir DIR [--prefix DIR]            read-only: checks, guide groups, what a run would do
mele run      --game-dir DIR [--prefix DIR] [--game LE1]... [--skip-textures] [--yes]
mele state    show | reset | adopt [--game LE1]...      what the app believes is done
mele backups  [--prefix DIR]
mele m3       setup | status | launch
mele mem      update | install --game LE1 --mfl FILE --game-dir DIR
mele check | install | textures                         lower-level steps (see `mele --help`)
```

`--prefix` is the Wine/Proton prefix Mod Manager runs in (Linux). The Nexus key is read from `NEXUS_API_KEY` or a file only you can read (`~/.config/MELEAutoModInstaller/api_key`); it is never printed.

**Already modded by hand (or with an older version)?** Run `mele state adopt` for those games first, otherwise the app would download and install textures again. **Starting a new playthrough** after restoring your backups? Run `mele state reset` (the GUI's *Forget progress*), otherwise it thinks the work is done.

## Safety rules the app enforces

- Preflight stops the run *before changing anything* if: the account is not Premium, a game folder or backup is missing or invalid, 7-Zip is missing, or free space is too low.
- Downloads are verified (size, and MD5 where Nexus provides it); mods are matched by the hash of their `moddesc.ini`; a changed mod is flagged, not silently trusted.
- The only step that modifies game files (the MEM texture install) re-checks the backup, asks you first, and is recorded as done only when Mass Effect Modder reports success.
- If the guide's mod list changes between runs, the app asks you to run the Mod Manager batch again.
- The API key file is created owner-only and a key file readable by others is refused.

## Where things live

| What | Linux | Windows |
|---|---|---|
| Settings, API key | `~/.config/MELEAutoModInstaller/` | `%APPDATA%\MELEAutoModInstaller\` |
| Progress (`state.json`), downloaded tools, cache, the app's Proton prefix | `~/.local/share/MELEAutoModInstaller/` | `%LOCALAPPDATA%\MELEAutoModInstaller\` |
| Mod Manager and its mod library | inside the prefix: `…/drive_c/users/steamuser/Documents/ME3TweaksModManager/` | `Documents\ME3TweaksModManager\` |

## Troubleshooting

- **"7-Zip is required"** – install it (see Requirements), or use a release package, which includes it.
- **"umu-launcher (umu-run) is required"** (Linux) – install it with your package manager. If Mod Manager is already set up in another prefix, point the app at it under *Advanced* (or `--prefix`).
- **"no valid Mod Manager backup"** – make the backup in Mod Manager, then *Refresh*. The backup must be one Mod Manager recorded; the app reads that record (the registry on Windows, the prefix's `user.reg` on Linux).
- **Mod Manager does not start on Linux / wine version mismatch** – do not run a Steam/Proton game from the same prefix at the same time; its wineserver blocks other Proton versions in that prefix.
- **"MEM failed"** – restore the game from your Mod Manager backup if it misbehaves, then retry. The log shows MEM's own output.
- **A mod is "InstalledVersionDiffers"** – the guide pins a file and Nexus now serves a newer build with a different `moddesc.ini`. It was still installed; check the mod's page if something is off.

## What has and has not been verified

Verified on the author's machine (Linux, CachyOS, a Steam non-Steam-shortcut Proton prefix, Nexus Premium):
- Downloading and staging all 229 mods in the guide's three groups; all matched by hash.
- ALOT, ISL (via torrent) and the guide's `.mem` textures for all three games, installed with the Windows build of MEM under Proton, with MEM's own verification passing.
- Read-only native-Linux checks of MEM, backup discovery through the Wine registry file, creating a fresh Proton prefix and starting Mod Manager in it.
- The orchestrator's safety rules and the GUI's behaviour under automated tests, and every GUI page rendered and inspected as an image.

**Not yet verified** (treat as experimental):
- A genuine first-time, start-to-finish run through the GUI on a freshly restored game.
- **Installing textures with the native Linux build of MEM** (`mele mem install`). Reading works; the install itself has only been proven with the Windows build under Proton.
- **Everything on Windows.** The Windows code paths (registry backup lookup, the `.7z` MEM download, UTF-16 MEM output, the Windows package) compile and are covered by tests that run in CI, but have not been run on a Windows machine by the author.
- The CI workflow has not been run on GitHub yet. (The release packages and their bundled 7-Zip build locally and match the pinned hashes, but the bundled 7-Zip has not been exercised by the packaged app, and not at all on Windows.)

## Building from source

Requires the .NET 10 SDK.

```bash
dotnet build src/MELEAutoModInstaller.sln
dotnet test src/Mele.Tests                           # engine tests (no network)
dotnet run --project src/Mele.App.Tests              # GUI tests, headless (xUnit v3: run directly, not via dotnet test)
dotnet run --project src/Mele.App                    # the desktop app
dotnet run --project src/Mele.Cli -- --help
build/package.sh linux-x64 dist                      # or win-x64; bundles 7-Zip, writes an archive
```

Live tests (real Nexus API, real Mod Manager prefix) are skipped unless `MELE_LIVE=1`; they need a Premium key and read, but do not modify, your game. Set `MELE_SHOTS=<folder>` when running the GUI tests to keep rendered screenshots.

Layout: `src/Mele.Engine` (everything that matters, no UI), `src/Mele.Cli`, `src/Mele.App` (Avalonia), plus tests. `guide.json` and `textures.json` describe the guide (they are embedded in the build). `docs/phase0-findings.md` records why ME3TweaksCore is not used (it does not run natively on Linux). `mele_installer/` is the original Python prototype, kept for reference.

## Roadmap

A goal is to bring in the functionality [ME3TweaksCore](https://github.com/ME3Tweaks/ME3TweaksCore) offers, which this app currently lacks. It was dropped from v0.1 because it does not run natively on Linux (see `docs/phase0-findings.md`), so each item needs either Core (Windows) or our own implementation (Linux):

- Create and restore Mod Manager-compatible game backups from the app (today it only checks that they exist).
- Detect the game folder automatically (Steam, EA, Epic) and validate it (vanilla, modded, broken).
- ASI loader and Bink video patch management.
- Reading game packages (e.g. to show which texture mods are really installed); needs Oodle, so Windows first.

## Credits

The mod selection and install order come from the guide by **Synth** on Nexus Mods. The actual installing is done by **ME3Tweaks Mod Manager** (ME3Tweaks) and **Mass Effect Modder** (Pawel Kolodziejski and contributors); this app only downloads and drives them, and neither is redistributed in its packages. Archives are unpacked with **7-Zip** (Igor Pavlov). See `packaging/THIRD-PARTY.txt`. This project is not affiliated with or endorsed by ME3Tweaks, Nexus Mods, BioWare or Electronic Arts.

Licence: not chosen yet.
