"""Texture section of the guide: M3TO mods (staged like content mods) and .mem mods."""
import fnmatch
import json
import shutil
import subprocess
import tempfile
import zipfile
from pathlib import Path, PurePosixPath

from . import download, stage
from .biq import ModEntry
from .nexus import Client, NexusError

M3TO_TORRENT_NAME = "ALOT_ISL_M3TO.torrent"


def load(path: Path) -> dict:
    return json.loads(path.read_text())


def _entry(manifest, game, item) -> ModEntry:
    return ModEntry(
        name=item["name"], game_domain=manifest["game_domain"], mod_id=item.get("mod_id"),
        file_id=item.get("file_id"), moddesc_rel=PurePosixPath(game) / item["name"] / "moddesc.ini",
        moddesc_md5="", moddesc_size=0, link="")


def fetch_torrent_member(client: Client, manifest, cache: Path, member: str) -> Path:
    """Download one file out of the ALOT/ISL M3TO torrent with aria2c."""
    out = cache / "torrent"
    target = out / "ALOT_ISL_M3TO" / member
    done = target.with_name(member + ".done")
    # An existing file is not proof of a download: aria2 writes shared boundary pieces
    # of neighbouring files, leaving sparse stubs. Only a completion marker counts.
    if target.is_file() and done.exists():
        return target
    if not shutil.which("aria2c"):
        raise NexusError("aria2c is required for the ISL torrent (pacman -S aria2)")
    t = manifest["torrent"]
    holder = ModEntry("M3TO torrent", manifest["game_domain"], t["mod_id"], t["file_id"],
                      PurePosixPath("x"), "", 0, "")
    zpath, _ = download.fetch(client, cache, holder)
    out.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(zpath) as z:
        z.extract(M3TO_TORRENT_NAME, out)
    torrent = out / M3TO_TORRENT_NAME
    listing = subprocess.run(["aria2c", "-S", str(torrent)], capture_output=True, text=True).stdout
    idx = next((ln.split("|")[0].strip() for ln in listing.splitlines() if ln.rstrip().endswith("/" + member)), None)
    if idx is None:
        raise NexusError(f"{member} not found in torrent")
    r = subprocess.run(["aria2c", f"--select-file={idx}", "--seed-time=0", "--file-allocation=none",
                        "--bt-stop-timeout=600", "-d", str(out), str(torrent)])
    if r.returncode != 0 or not target.is_file():
        raise NexusError(f"torrent download of {member} failed (aria2c exit {r.returncode}); rerun to resume")
    done.touch()
    return target


def _extract_mem(archive: Path, dest: Path, select: list[str] | None) -> list[str]:
    with tempfile.TemporaryDirectory(prefix="mele-", dir=dest.parent) as tmp:
        stage._extract(archive, Path(tmp))
        mems = [p for p in Path(tmp).rglob("*") if p.is_file() and p.suffix.lower() in (".mem", ".tpf", ".mod")]
        if select:
            want = {s.lower() for s in select}
            mems = [p for p in mems if p.name.lower() in want]
            missing = want - {p.name.lower() for p in mems}
            if missing:
                raise stage.StageError(f"missing selected files in archive: {sorted(missing)}")
        if not mems:
            raise stage.StageError("no .mem files found in archive")
        if dest.exists():
            shutil.rmtree(dest)
        dest.mkdir(parents=True)
        for p in mems:
            shutil.move(p, dest / p.name)
    return sorted(p.name for p in dest.iterdir())


def process(client, manifest, game, idx, item, library: Path, cache: Path, keep_archives: bool):
    """Returns (status, note)."""
    e = _entry(manifest, game, item)
    if "torrent_member" in item:
        archive = fetch_torrent_member(client, manifest, cache, item["torrent_member"])
        how = "torrent"
    else:
        archive, how = download.fetch(client, cache, e)
    if item["type"] == "m3to":
        status = stage.stage(archive, library, e)
    else:
        dest = cache.parent / "mele-auto-mod-installer-mem" / game / f"{idx:02d} {item['name']}"
        dest.parent.mkdir(parents=True, exist_ok=True)
        names = _extract_mem(archive, dest, item.get("select"))
        status = "mem-ready"
        how = f"{how}; {len(names)} file(s) -> {dest}"
    if not keep_archives:
        if "torrent_member" in item:
            archive.unlink(missing_ok=True)
            archive.with_name(archive.name + ".done").unlink(missing_ok=True)
        else:
            shutil.rmtree(archive.parent, ignore_errors=True)
    return status, how


def write_m3to_batch(manifest, game: str, library: Path, out_dir: Path) -> Path:
    """Write an M3 Batch Mod Installer group (.biq2) for a game's staged M3TO mods, in guide order."""
    import hashlib
    from datetime import datetime
    mods = []
    for item in manifest[game]:
        if item["type"] != "m3to":
            continue
        ini = library / game / item["name"] / "moddesc.ini"
        if not ini.is_file():
            raise stage.StageError(f"{item['name']}: not staged in the library ({ini})")
        data = ini.read_bytes()
        modname = next((ln.split("=", 1)[1].strip() for ln in data.decode("utf-8-sig", "replace").splitlines()
                        if ln.lower().startswith("modname")), item["name"])
        link = f"https://nexusmods.com/{manifest['game_domain']}/mods/363?tab=files"
        if item.get("file_id"):
            link += f"&file_id={item['file_id']}"
        mods.append({
            "moddescpath": f"{game}\\{item['name']}\\moddesc.ini",
            "configurationtime": datetime.now().astimezone().isoformat(),
            "moddeschash": hashlib.md5(data).hexdigest(),
            "moddescsize": len(data),
            "downloadlink": link,
            "userchosenoptions": [],
            "allchosenoptions": [],
            "haschosenoptions": True,
            "IsStandalone": True,
            "modname": modname,
        })
    group = {"game": game, "description": f"{game} M3TO textures (ALOT, ISL) - generated from guide 1127",
             "restorebeforeinstall": False, "mods": mods}
    out_dir.mkdir(parents=True, exist_ok=True)
    path = out_dir / f"{game} Textures M3TO (generated).biq2"
    path.write_text(json.dumps(group, indent=2), encoding="utf-8")
    return path
