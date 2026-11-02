"""Extract archives into an ME3Tweaks Mod Manager mod library layout."""
import hashlib
import shutil
import subprocess
import tempfile
from pathlib import Path

from .biq import ModEntry


class StageError(RuntimeError):
    pass


def _md5(path: Path) -> str:
    return hashlib.md5(path.read_bytes()).hexdigest()


def _extract(archive: Path, out: Path) -> None:
    for cmd in (["7z", "x", "-y", f"-o{out}", str(archive)], ["bsdtar", "-xf", str(archive), "-C", str(out)]):
        if shutil.which(cmd[0]) and subprocess.run(cmd, capture_output=True).returncode == 0:
            return
    raise StageError(f"could not extract {archive.name} (need 7z or bsdtar)")


def is_staged(library: Path, entry: ModEntry) -> bool:
    ini = library / entry.moddesc_rel
    return ini.is_file() and _md5(ini) == entry.moddesc_md5


def stage(archive: Path, library: Path, entry: ModEntry) -> str:
    """Install archive into library. Returns 'ok', or 'ok-hash-differs' if the mod
    version in the archive differs from the one the group was recorded with."""
    dest = library / entry.moddesc_rel.parent
    root = library.resolve()
    if root not in dest.resolve().parents:
        raise StageError(f"{entry.name}: destination escapes library")
    # Extract on the library's own filesystem: /tmp is often a small tmpfs, and
    # staying on one filesystem lets the final step be a rename instead of a copy.
    scratch = library.parent / ".mele-installer-tmp"
    scratch.mkdir(exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="mele-", dir=scratch) as tmp:
        tmp = Path(tmp)
        _extract(archive, tmp)
        inis = [p for p in tmp.rglob("*") if p.is_file() and p.name.lower() == "moddesc.ini"]
        if not inis:
            raise StageError(f"{entry.name}: no moddesc.ini in archive (nested archive or non-M3 mod?)")
        match = [p for p in inis if _md5(p) == entry.moddesc_md5] if entry.moddesc_md5 else []
        if not entry.moddesc_md5 and len(inis) == 1:
            match = inis  # no recorded hash (e.g. texture mods): accept a single moddesc.ini
        status = "ok"
        if not match:
            status = "ok-hash-differs"
            match = [p for p in inis if p.parent.name == dest.name] or (inis if len(inis) == 1 else [])
            if not match:
                raise StageError(f"{entry.name}: {len(inis)} moddesc.ini files, none match the group")
        if dest.exists():
            shutil.rmtree(dest)
        dest.parent.mkdir(parents=True, exist_ok=True)
        shutil.move(match[0].parent, dest)
    return status
