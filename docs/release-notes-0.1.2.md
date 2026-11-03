## v0.1.2

Finds your game for you, and tells you when Mod Manager has the wrong game folder.

**Still early.** It has been run end to end on one machine (Linux, Proton prefix, Mass Effect 1). Read *What has and has not been verified* in the README before relying on it, and make a backup of each game first.

### New
- **Finds your game.** On first launch, with no folder chosen, the app looks for the game and fills the folder in if there is exactly one install. *Find my game* looks again at any time, and lists several installs for you to choose from. It looks in Steam libraries, Heroic, Legendary and Epic, the Windows registry and the usual EA, Origin and Epic folders, and in the usual game folders and mounted drives. A folder counts only if a game executable is really there. `mele detect` shows what it finds, and `--game-dir` is now optional when there is exactly one install.
- **Checks Mod Manager's game folder.** Mod Manager keeps its own list of game folders, and a wrong or mistyped one fails silently. The Mod Manager card, *Check first* and `mele m3 targets` now say whether Mod Manager has your game folder, has one that does not exist (for example a mistyped drive), or has a different install, and show the exact path to enter. On Linux that is the `Z:\...` path.

### Behind the scenes
- Releases are now created as a single draft with both packages attached. The old release step left a duplicate release for every tag.
- The Windows GUI tests now run in CI and pass.

### Not tested
- **Windows has never been run on a real Windows machine.** The new Windows-specific parts (registry and default install folders, the Mod Manager target files) are covered by tests that run in CI but not by a real install.
- The game search is tested on a real Linux machine for installs in a games folder. The Steam, Heroic and Epic sources are tested against sample files only, not real launcher installs.
- Installing textures with the native Linux build of Mass Effect Modder, and Mass Effect 2 and 3 through the app.

### Downloads
- `mele-auto-mod-installer-0.1.2-linux-x64.tar.gz`
- `mele-auto-mod-installer-0.1.2-win-x64.zip`

Both include 7-Zip (see `THIRD-PARTY.txt`) and are self-contained: nothing else to install, except `umu-launcher` on Linux for Mod Manager. Licence: MIT.
