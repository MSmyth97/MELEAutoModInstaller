"""Download files listed in a group into a content-addressed-ish cache."""
import hashlib
import json
import urllib.parse
import urllib.request
from pathlib import Path

from .biq import ModEntry
from .nexus import Client, NexusError


def md5_file(path: Path) -> str:
    h = hashlib.md5()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def cache_dir(cache: Path, entry: ModEntry) -> Path:
    return cache / str(entry.file_id)


def cached_archive(cache: Path, entry: ModEntry) -> Path | None:
    """Return the verified archive for entry if it is already downloaded."""
    d = cache_dir(cache, entry)
    info_path = d / "info.json"
    if not info_path.is_file():
        return None
    info = json.loads(info_path.read_text())
    archive = d / Path(info["file_name"]).name
    if archive.is_file() and archive.stat().st_size == info["size_in_bytes"]:
        return archive
    return None


def fetch(client: Client, cache: Path, entry: ModEntry) -> tuple[Path, str]:
    """Ensure entry's archive is in cache. Returns (path, 'cached'|'downloaded')."""
    if entry.file_id is None:
        raise NexusError(f"{entry.name}: no Nexus file_id in group file")
    hit = cached_archive(cache, entry)
    if hit:
        return hit, "cached"
    info = client.file_info(entry.game_domain, entry.mod_id, entry.file_id)
    d = cache_dir(cache, entry)
    d.mkdir(parents=True, exist_ok=True)
    dest = d / Path(info["file_name"]).name
    part = dest.with_suffix(dest.suffix + ".part")
    url = client.download_url(entry.game_domain, entry.mod_id, entry.file_id)
    url = urllib.parse.quote(url, safe=":/?&=%+~@,;")
    req = urllib.request.Request(url, headers={"User-Agent": "MELEAutoModInstaller"})
    with urllib.request.urlopen(req, timeout=60) as r, open(part, "wb") as f:
        while chunk := r.read(1 << 20):
            f.write(chunk)
    if info.get("size_in_bytes") and part.stat().st_size != info["size_in_bytes"]:
        part.unlink()
        raise NexusError(f"{entry.name}: size mismatch (got {part.stat().st_size}, want {info['size_in_bytes']})")
    if info.get("md5") and md5_file(part) != info["md5"].lower():
        part.unlink()
        raise NexusError(f"{entry.name}: md5 mismatch")
    part.rename(dest)
    info["size_in_bytes"] = dest.stat().st_size
    (d / "info.json").write_text(json.dumps(info))
    return dest, "downloaded"
