import argparse
import sys
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path

from . import biq, download, stage, textures
from .nexus import Client, NexusError, load_api_key


def _parse(argv):
    p = argparse.ArgumentParser(prog="mele-installer", description=__doc__)
    sub = p.add_subparsers(dest="cmd", required=True)
    for name, h in (("check", "validate API key and list what a group needs"),
                    ("install", "download and stage every mod in a group")):
        s = sub.add_parser(name, help=h)
        s.add_argument("group", type=Path, nargs="+", help=".biq2 file(s)")
        s.add_argument("--link", action="append", default=[], metavar="NAME=URL",
                       help="Nexus file URL for a mod the group file has no link for (repeatable)")
        s.add_argument("--cache", type=Path, default=Path.home() / ".cache" / "mele-auto-mod-installer")
        if name == "install":
            s.add_argument("--library", type=Path, required=True,
                           help="ME3Tweaks Mod Manager mod library folder (contains LE1/LE2/LE3)")
            s.add_argument("--jobs", type=int, default=3)
            s.add_argument("--only", help="only mods whose name contains this text")
    t = sub.add_parser("textures", help="download/stage the guide's texture mods from textures.json")
    t.add_argument("manifest", type=Path)
    t.add_argument("--library", type=Path, required=True)
    t.add_argument("--cache", type=Path, default=Path.home() / ".cache" / "mele-auto-mod-installer")
    t.add_argument("--game", choices=["LE1", "LE2", "LE3"], action="append")
    t.add_argument("--write-batch", action="store_true",
                   help="don't download; write M3 batch groups (.biq2) for the staged M3TO mods into <library>/BatchModQueues")
    t.add_argument("--only", help="only items whose name contains this text")
    t.add_argument("--keep-archives", action="store_true", help="keep downloaded archives (they are large)")
    return p.parse_args(argv)


def _check(client, groups, cache):
    me = client.validate()
    print("API key OK for:", me.get("name"), "| premium:", me.get("is_premium"))
    for g in groups:
        todo = [m for m in g.mods if m.downloadable and not download.cached_archive(cache, m)]
        manual = [m for m in g.mods if not m.downloadable]
        print(f"{g.game}: {len(g.mods)} mods, {len(todo)} to download, {len(manual)} without a Nexus link")
        for m in manual:
            print(f"  manual: {m.name}")


def _install(client, groups, a):
    failures = []
    for g in groups:
        mods = [m for m in g.mods if not a.only or a.only.lower() in m.name.lower()]
        print(f"== {g.game}: {len(mods)} mods")

        def one(m):
            if not m.downloadable:
                return m, "manual", "no Nexus link; obtain manually"
            try:
                if stage.is_staged(a.library, m):
                    return m, "skip", "already staged"
                archive, how = download.fetch(client, a.cache, m)
                return m, stage.stage(archive, a.library, m), how
            except (NexusError, stage.StageError, OSError, ValueError) as e:
                return m, "FAIL", str(e)

        with ThreadPoolExecutor(a.jobs) as ex:
            for m, status, note in ex.map(one, mods):
                print(f"  [{status}] {m.name}: {note}")
                if status in ("FAIL", "manual", "ok-hash-differs"):
                    failures.append((g.game, m.name, status, note))
    if failures:
        print("\nNeeds attention:")
        for game, name, status, note in failures:
            print(f"  {game} {name}: {status} {note}")
    print("\nNext: in ME3Tweaks Mod Manager, reload the library, then Batch Mod Installer > load the .biq2 > Install.")
    return 1 if any(f[2] == "FAIL" for f in failures) else 0


def _textures(client, a):
    m = textures.load(a.manifest)
    if a.write_batch:
        out = a.library / "BatchModQueues"
        for game in a.game or ["LE1", "LE2", "LE3"]:
            print("wrote", textures.write_m3to_batch(m, game, a.library, out))
        return 0
    failed = 0
    for game in a.game or ["LE1", "LE2", "LE3"]:
        print(f"== {game} textures")
        for i, item in enumerate(m[game], 1):
            if a.only and a.only.lower() not in item["name"].lower():
                continue
            try:
                status, note = textures.process(client, m, game, i, item, a.library, a.cache, a.keep_archives)
            except (NexusError, stage.StageError, OSError, ValueError) as e:
                status, note, failed = "FAIL", str(e), failed + 1
            print(f"  [{status}] {i}. {item['name']}: {note}", flush=True)
    return 1 if failed else 0


def main(argv=None):
    a = _parse(argv)
    if a.cmd == "textures":
        try:
            return _textures(Client(load_api_key()), a)
        except NexusError as e:
            print(f"error: {e}", file=sys.stderr)
            return 2
    links = dict(x.split("=", 1) for x in a.link)
    groups = [biq.with_links(biq.load(p), links) for p in a.group]
    try:
        client = Client(load_api_key())
        if a.cmd == "check":
            return _check(client, groups, a.cache) or 0
        return _install(client, groups, a)
    except NexusError as e:
        print(f"error: {e}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    sys.exit(main())
