# Phase 0: ME3TweaksCore on native Linux (spike, 2026-11-02)

Goal: decide whether ME3TweaksCore (net10.0, GPL-3.0) can be used by a native Linux app.

## Result: partly. Core is used on Windows only; Linux uses our own code.

Built Core on Linux (SDK 10.0.114) with three workarounds, none of which we keep:
1. Define `WINDOWS`/`LINUX`/`MACOS` per OS (Core selects code with `#if` but defines none of them).
2. Replace its pre-build PowerShell step (compresses `Localization/Dictionaries/*.xaml` with the Windows-only
   `lzma.exe`) with `xz --format=lzma` **and** patch the uncompressed size into header bytes 5..12;
   `xz` writes "unknown size" and Core's decoder rejects it ("Cannot decompress streamed LZMA").
3. One-line patch: `MEMNoGuiUpdater.cs` calls `Utilities.MakeFileExecutable(Locations.MEMPath())` under `#if LINUX`,
   helpers that no longer exist; replace with `File.SetUnixFileMode(...)`.

Runtime on native Linux:
| Check | Result |
|---|---|
| `ME3TweaksCoreLib.Initialize` | FAIL: `DllNotFoundException: CompressionWrappers.dll` (LegendaryExplorerCore native, Windows only) |
| `new GameTarget(LE1, path, false, skipInit: true)` | FAIL: tries to load `oo2core_8_win64.dll` (Oodle, Windows DLL) |
| `MEMNoGuiUpdater.UpdateMEM` | OK: downloads MEM v534 Linux asset, runs it natively (stored as `MassEffectModderNoGui_LE.exe`, an ELF) |
| `MEMIPCHandler.SetGamePath(bool, MEGame, string)` | OK |
| `MEMIPCHandler.GetGameLocations` | HUNG (90 s) |
| `BackupService.GetGameBackupPath` | null: backup paths live in the Windows registry (`HKCU\Software\ME3Tweaks`, values `LE1VanillaBackupLocation` ...) |

Consequences:
- `MEMIPCHandler.InstallMEMFiles` needs a `GameTarget`, which cannot be built natively on Linux. Linux runs
  `MassEffectModderNoGui --install-mods --gameid N --input list.mfl --verify` itself (proven on all three games).
- On Linux the M3 backup locations are in the Wine prefix's `user.reg` text file; we read that ourselves.
- Anything reading LE packages natively (Oodle) is unavailable on Linux. Not needed for v1.

## Decision
Hybrid: one engine, a platform layer. Windows implementation uses ME3TweaksCore (GameTarget, BackupService,
MEMNoGuiUpdater/MEMIPCHandler). Linux implementation is our own thin MEM updater/runner and prefix-registry reader.
Core is referenced only when building on Windows, so Linux builds need no submodules, patches or xz tricks.
