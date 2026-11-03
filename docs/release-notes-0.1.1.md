## v0.1.1

Fixes found during the first full run on a real machine, plus backup and restore tools in the app.

**Still early.** It has been run end to end on one machine (Linux, Proton prefix, Mass Effect 1). Read *What has and has not been verified* in the README before relying on it, and make a backup of each game first.

### Backup and restore (new)
Under **Backups** in the window, and as `mele backups create | restore | compare` on the command line:
- **Back up now / New backup...** copies a game, checks the copy, writes the marker Mod Manager looks for last, and tells Mod Manager where it is. A game that already has a backup can get a new one in a folder you choose; the old backup is kept.
- **Check** compares a game with its backup and says whether it looks vanilla.
- **Restore** shows exactly what it will do before it does anything. It keeps vanilla files in languages the backup was made without (Mod Manager's own backup skips unselected languages, and a plain mirror restore would delete those files) and keeps your graphics settings. Every failure is listed, and the result is checked against the backup afterwards.
- Backing up refuses a game that already has mods or textures installed, so a backup can't bring mods back.

### Fixes
- **Downloads no longer fail on files Nexus sizes only in whole KB.** One mod in the guide (LE1 Diversification Project) failed on every retry with a size mismatch of a few hundred bytes.
- **The results screen now names each failed mod or texture and why**, instead of only a count.
- **Warns before the Mod Manager step if the game already has mods or textures** left over from an earlier run. Mod Manager only complains later, at the batch install.
- The backup hint under Backups appears on Linux only.

### Linux notes
- Mod Manager's own "Restore to target" does nothing on Linux: it reports success whatever happened. Use the restore in this app. Details are in the README's Troubleshooting section.
- In the app's Wine prefix, `Z:` is the whole filesystem and `X:` is your home folder. `X:\home\you\...` does not exist, and Mod Manager does not warn you.

### Not tested
- **Windows has never been run on a real Windows machine**, including the new backup and restore (the registry write and Windows paths). The code builds and its tests pass on Windows in CI.
- Installing textures with the native Linux build of Mass Effect Modder, and Mass Effect 2 and 3 through the app.

### Downloads
- `mele-auto-mod-installer-0.1.1-linux-x64.tar.gz`
- `mele-auto-mod-installer-0.1.1-win-x64.zip`

Both include 7-Zip (see `THIRD-PARTY.txt`) and are self-contained: nothing else to install, except `umu-launcher` on Linux for Mod Manager. Licence: MIT.
