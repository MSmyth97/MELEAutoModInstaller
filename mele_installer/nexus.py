"""Minimal Nexus Mods API v1 client (Premium: download links need no nxm key)."""
import json
import os
import stat
import urllib.error
import urllib.request
from pathlib import Path

from . import __version__

API = "https://api.nexusmods.com/v1"
KEY_FILE = Path(os.environ.get("XDG_CONFIG_HOME", Path.home() / ".config")) / "mele-auto-mod-installer" / "api_key"


class NexusError(RuntimeError):
    pass


def load_api_key() -> str:
    key = os.environ.get("NEXUS_API_KEY", "").strip()
    if not key and KEY_FILE.is_file():
        mode = KEY_FILE.stat().st_mode
        if mode & (stat.S_IRWXG | stat.S_IRWXO):
            raise NexusError(f"{KEY_FILE} is readable by others; run: chmod 600 {KEY_FILE}")
        key = KEY_FILE.read_text().strip()
    if not key:
        raise NexusError(f"No API key. Set NEXUS_API_KEY or put it in {KEY_FILE} (chmod 600).")
    return key


class Client:
    def __init__(self, api_key: str):
        self._headers = {
            "apikey": api_key,
            "Application-Name": "MELEAutoModInstaller",
            "Application-Version": __version__,
            "User-Agent": f"MELEAutoModInstaller/{__version__}",
        }

    def _get(self, path: str):
        req = urllib.request.Request(f"{API}{path}", headers=self._headers)
        try:
            with urllib.request.urlopen(req, timeout=30) as r:
                return json.load(r)
        except urllib.error.HTTPError as e:
            body = e.read().decode(errors="replace")[:200]
            raise NexusError(f"HTTP {e.code} for {path}: {body}") from None

    def validate(self) -> dict:
        return self._get("/users/validate.json")

    def file_info(self, game: str, mod_id: int, file_id: int) -> dict:
        return self._get(f"/games/{game}/mods/{mod_id}/files/{file_id}.json")

    def download_url(self, game: str, mod_id: int, file_id: int) -> str:
        links = self._get(f"/games/{game}/mods/{mod_id}/files/{file_id}/download_link.json")
        if not links:
            raise NexusError(f"no download link for {mod_id}/{file_id}")
        return links[0]["URI"]
