## v0.1.2

Finds your game for you, and tells you when Mod Manager has the wrong game folder.

**Still early.** Mass Effect 1 has been run end to end through the app on Linux and on Windows. Read *What has and has not been verified* in the README before relying on it, and make a backup of each game first.

### New
- **Finds your game.** On first launch, with no folder chosen, the app looks for the game and fills the folder in if there is exactly one install. *Find my game* looks again at any time, and lists several installs for you to choose from. It looks in Steam libraries, Heroic, Legendary and Epic, the Windows registry and the usual EA, Origin and Epic folders, and in the usual game folders and mounted drives. A folder counts only if a game executable is really there. `mele detect` shows what it finds, and `--game-dir` is now optional when there is exactly one install.
- **Checks Mod Manager's game folder.** Mod Manager keeps its own list of game folders, and a wrong or mistyped one fails silently. The Mod Manager card, *Check first* and `mele m3 targets` now say whether Mod Manager has your game folder, has one that does not exist (for example a mistyped drive), or has a different install, and show the exact path to enter. On Linux that is the `Z:\...` path.

### Behind the scenes
- Releases are now created as a single draft with both packages attached. The old release step left a duplicate release for every tag.
- The Windows GUI tests now run in CI and pass.

### Verified on Windows
Mass Effect 1, run by the author on a real Windows machine: game search, the Mod Manager card, mod download and staging, the Mod Manager batch install, textures through Mass Effect Modder, and creating and restoring a backup all worked.

### Not tested
- **Mass Effect 2 and 3 through the app's window**, on either system.
- **Game search against real Steam, Heroic and Epic installs.** The folder scan has found real installs; the launcher sources are tested against sample files only.

### Downloads
- `mele-auto-mod-installer-0.1.2-linux-x64.tar.gz`
- `mele-auto-mod-installer-0.1.2-win-x64.zip`

Both include 7-Zip (see `THIRD-PARTY.txt`) and are self-contained: nothing else to install, except `umu-launcher` on Linux for Mod Manager. Licence: MIT.
