"""Parser for ME3Tweaks Mod Manager Batch Mod Installer groups (.biq2, JSON)."""
import json
import re
from dataclasses import dataclass, replace
from pathlib import PurePosixPath

_LINK = re.compile(r"nexusmods\.com/(?P<game>[^/]+)/mods/(?P<mod>\d+).*[?&]file_id=(?P<file>\d+)")


@dataclass(frozen=True)
class ModEntry:
    name: str
    game_domain: str | None
    mod_id: int | None
    file_id: int | None
    moddesc_rel: PurePosixPath  # e.g. LE1/Mod Name/moddesc.ini, relative to the M3 library
    moddesc_md5: str
    moddesc_size: int
    link: str

    @property
    def downloadable(self) -> bool:
        return self.file_id is not None


@dataclass(frozen=True)
class Group:
    game: str
    description: str
    mods: list[ModEntry]


def _safe_rel(raw: str) -> PurePosixPath:
    p = PurePosixPath(raw.replace("\\", "/"))
    if p.is_absolute() or ".." in p.parts:
        raise ValueError(f"unsafe moddesc path in group file: {raw!r}")
    return p


def with_links(group: Group, links: dict[str, str]) -> Group:
    """Return group with download links supplied for entries that have none (matched by exact mod name)."""
    names = {m.name for m in group.mods}
    unknown = set(links) - names
    if unknown and not (set(links) - unknown):
        return group  # all overrides are for another group file
    mods = []
    for m in group.mods:
        hit = _LINK.search(links.get(m.name, "")) if not m.downloadable else None
        if hit:
            m = replace(m, game_domain=hit["game"], mod_id=int(hit["mod"]), file_id=int(hit["file"]), link=links[m.name])
        mods.append(m)
    return Group(group.game, group.description, mods)


def load(path) -> Group:
    with open(path, encoding="utf-8-sig") as f:
        data = json.load(f)
    mods = []
    for m in data["mods"]:
        link = m.get("downloadlink") or ""
        hit = _LINK.search(link)
        mods.append(ModEntry(
            name=m["modname"].strip(),
            game_domain=hit["game"] if hit else None,
            mod_id=int(hit["mod"]) if hit else None,
            file_id=int(hit["file"]) if hit else None,
            moddesc_rel=_safe_rel(m["moddescpath"]),
            moddesc_md5=m["moddeschash"].lower(),
            moddesc_size=int(m["moddescsize"]),
            link=link,
        ))
    return Group(data["game"], data.get("description", ""), mods)
