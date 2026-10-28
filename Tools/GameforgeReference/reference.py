"""Pin or verify the user-selected local Gameforge client without copying or modifying it."""
import argparse
import concurrent.futures
import datetime
import hashlib
import json
from pathlib import Path
import struct


ROOT_FILES = ("TClient.exe", "TClientCmd.tif", "TQuest.mpq", "TQNode.qpd", "TClientMP.mpc", "4Story.txt")
DATA_FOLDERS = ("Data", "Index", "Tcd", "Tmh", "Node", "Path")


def inspect(source, path):
    resolved = path.resolve()
    if not resolved.is_relative_to(source):
        raise ValueError(f"Reference file resolves outside its source: {path}")
    before = path.stat()
    with path.open("rb") as stream:
        digest = hashlib.file_digest(stream, "sha256").hexdigest()
    after = path.stat()
    if (before.st_size, before.st_mtime_ns) != (after.st_size, after.st_mtime_ns):
        raise ValueError(f"Reference changed while being read: {path}")
    return {"path": path.relative_to(source).as_posix(), "bytes": before.st_size, "sha256": digest}


def files(source):
    paths = []
    for name in ROOT_FILES:
        path = source / name
        if not path.is_file():
            raise ValueError(f"Required reference file is missing: {path}")
        paths.append(path)
    for name in DATA_FOLDERS:
        folder = source / name
        if folder.is_dir():
            paths.extend(p for p in folder.rglob("*") if p.is_file())
    return sorted(paths, key=lambda p: p.relative_to(source).as_posix().lower())


def inventory(source):
    with concurrent.futures.ThreadPoolExecutor(max_workers=4) as pool:
        return list(pool.map(lambda path: inspect(source, path), files(source)))


def pe_timestamp(path):
    with path.open("rb") as stream:
        header = stream.read(64)
        if len(header) != 64 or header[:2] != b"MZ":
            raise ValueError("The reference TClient.exe is not a PE file")
        stream.seek(struct.unpack_from("<I", header, 60)[0])
        pe = stream.read(12)
    if len(pe) != 12 or pe[:4] != b"PE\0\0":
        raise ValueError("The reference TClient.exe has an invalid PE header")
    return datetime.datetime.fromtimestamp(struct.unpack_from("<I", pe, 8)[0], datetime.timezone.utc).isoformat()


def capture(source, manifest):
    if manifest.is_relative_to(source):
        raise ValueError("Save the manifest outside the reference installation")
    rows = inventory(source)
    document = {
        "schema": 1,
        "reference": "User-selected local Gameforge CZ installation",
        "captured_utc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
        "product_marker": (source / "4Story.txt").read_text(encoding="ascii").strip(),
        "release_version": None,
        "client_pe_timestamp_utc": pe_timestamp(source / "TClient.exe"),
        "timestamp_is_release_version": False,
        "scope": "Client executable, UI, quests, tables and indexed game resources",
        "server_data": {
            "verified": False,
            "published_supplement": "Gameforge/monsters-web.json; exact applied fields in Gameforge/monster-restoration.json",
            "missing_reference": ["remaining monster levels/HP and unpublished combat attributes", "spawn charts", "loot and experience rates",
                                  "NPC shops", "server quest conditions and rewards"],
            "legacy_database": "RageZone/Araz data is provisional, not an official Gameforge baseline"
        },
        "files": rows
    }
    manifest.parent.mkdir(parents=True, exist_ok=True)
    with manifest.open("x", encoding="utf-8", newline="\n") as stream:
        json.dump(document, stream, ensure_ascii=False, indent=2)
        stream.write("\n")
    print(f"Captured {len(rows)} reference files ({sum(row['bytes'] for row in rows):,} bytes): {manifest}")


def verify(source, manifest):
    document = json.loads(manifest.read_text(encoding="utf-8"))
    if document.get("schema") != 1:
        raise ValueError("Unsupported reference manifest schema")
    expected = {row["path"]: row for row in document["files"]}
    if len(expected) != len(document["files"]):
        raise ValueError("Duplicate paths in the reference manifest")
    actual = {row["path"]: row for row in inventory(source)}
    missing = sorted(expected.keys() - actual.keys())
    added = sorted(actual.keys() - expected.keys())
    changed = sorted(path for path in expected.keys() & actual.keys() if expected[path] != actual[path])
    if missing or added or changed:
        raise ValueError(json.dumps({"missing": missing, "added": added, "changed": changed}, ensure_ascii=False))
    print(f"Verified {len(actual)} reference files; every size and SHA-256 matches {manifest}")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("action", choices=("capture", "verify"))
    parser.add_argument("--source", type=Path, required=True)
    parser.add_argument("--manifest", type=Path, default=Path(__file__).resolve().parents[2] / "Gameforge/reference.json")
    args = parser.parse_args()
    source, manifest = args.source.resolve(), args.manifest.resolve()
    try:
        if args.action == "capture":
            capture(source, manifest)
        else:
            verify(source, manifest)
    except (OSError, ValueError, KeyError) as error:
        parser.exit(1, f"Reference check failed: {error}\n")


if __name__ == "__main__":
    main()
