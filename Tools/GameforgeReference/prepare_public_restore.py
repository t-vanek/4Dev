"""Prepare exact ID-specific monster level/HP corrections from captured official detail pages."""
import argparse
import hashlib
import json
from pathlib import Path
import re
import sqlite3
import zlib

from catalogue import Tables
from direct_variants import candidates
from identity_audit import normal
from monsters import bounds
from prepare_restore import SQL_HEADER, SQL_FOOTER


def load(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--snapshot", type=Path, required=True)
    parser.add_argument("--facts", type=Path, required=True)
    parser.add_argument("--public", type=Path, required=True)
    parser.add_argument("--cache", type=Path, required=True)
    parser.add_argument("--reference-output", type=Path, required=True)
    parser.add_argument("--sql-output", type=Path, required=True)
    parser.add_argument("--allow-incomplete", action="store_true", help="Draft only: permit an unfinished direct-variant crawl")
    args = parser.parse_args()
    catalogue = load(args.public / "monster-catalogue.json")
    listings = {row["id"]: row for row in catalogue["records"]}
    client = {row["id"]: row for row in load(args.facts / "monsters.json")}
    monsters = {row["wID"]: row for row in load(args.snapshot / "TMONSTERCHART.json")}
    attributes = {(row["wID"], row["bLevel"]): row for row in load(args.snapshot / "TMONATTRCHART.json")}
    # Also reserve dangling references: do not accidentally give an unrelated monster a new attribute row.
    used = {key[0] for key in attributes} | {row["wMonAttr"] for row in monsters.values()}
    free = iter(ident for ident in range(30000, 32768) if ident not in used)
    details = {}
    variant_errors = []
    databases = [args.public / "details.sqlite", args.public / "monster-details.sqlite", args.public / "monster-variants.sqlite"]
    for path in databases:
        if not path.exists():
            continue
        with sqlite3.connect(path) as connection:
            for ident, url, digest, blob in connection.execute("SELECT id,url,sha256,facts FROM details WHERE kind='monster'"):
                row = json.loads(zlib.decompress(blob))
                if row["id"] != ident or row["url"] != url or row["sha256"] != digest:
                    raise ValueError("Inconsistent detail evidence")
                if ident in details and details[ident]["sha256"] != digest:
                    raise ValueError("Conflicting captured details for ID " + str(ident))
                details[ident] = row
            if path.name == "monster-variants.sqlite":
                variant_errors = [{"id": row[0], "reason": row[1]} for row in connection.execute("SELECT id,error FROM errors")]
    if not listings.keys() <= details.keys():
        raise ValueError("The main monster catalogue detail capture is incomplete")
    audit_path = Path(__file__).resolve().parents[2] / "Gameforge/normalization/identity-audit.json"
    expected_variants = {row["id"] for row in candidates(load(audit_path), catalogue, "monster")}
    pending_variants = expected_variants - details.keys() - {row["id"] for row in variant_errors}
    if pending_variants and not args.allow_incomplete:
        raise ValueError(f"The direct-variant capture is incomplete: {len(pending_variants)} pending")
    corrections, verified, skipped = [], [], []
    for ident, row in sorted(details.items()):
        reason = None
        cache_file = args.cache / (hashlib.sha256(row["url"].encode()).hexdigest() + ".html")
        payload = cache_file.read_bytes()
        if hashlib.sha256(payload).hexdigest() != row["sha256"]:
            raise ValueError("The raw official HTML no longer matches its recorded SHA-256")
        primary = re.search(r'<table\b[^>]*class="detail_left"[^>]*>.*?</table>', payload.decode("utf-8"), re.S)
        if primary is None:
            raise ValueError("The primary monster detail table is missing")
        tables = Tables()
        tables.feed(primary[0])
        first = next((table for table in tables.tables if table["class"] == "detail_left"), None)
        if first is None:
            raise ValueError("The primary monster detail table is missing")
        fields = {cells[0]["text"].rstrip(":"): cells[1]["text"] for cells in first["rows"] if len(cells) == 2}
        level, hp = bounds(fields["Level"]), bounds(fields["Život"])
        representative = listings.get(ident) or listings.get(row.get("catalogue_representative_id"))
        if representative is None:
            raise ValueError("A direct variant has no public catalogue provenance")
        list_level = bounds(representative["cells"][1])
        outside_list_range = not list_level[0] <= level[0] <= level[1] <= list_level[1]
        if ident not in client or normal(client[ident]["name"]) != normal(row["name"]):
            reason = "The original-client name differs from the public name"
        elif normal(row["heading"]) != normal(row["name"]):
            reason = "The detail heading differs from the public catalogue name"
        elif ident in listings and outside_list_range:
            reason = "The catalogue and detail publish inconsistent level ranges for this ID"
        elif ident not in monsters:
            reason = "The SQL monster row is missing; unpublished defaults were not inferred"
        elif level[0] != level[1] or hp[0] != hp[1] or hp[0] <= 0:
            reason = "The ID-specific detail does not publish a single positive level/HP pair"
        else:
            monster = monsters[ident]
            original = attributes.get((monster["wMonAttr"], monster["bLevel"]))
            if original is None:
                reason = "The current SQL attribute row is missing"
        if reason:
            skipped.append({"id": ident, "reason": reason, "url": row["url"]})
            continue
        evidence = {"id": ident, "name": client[ident]["name"], "old_level": monster["bLevel"],
                    "old_attr": monster["wMonAttr"], "old_hp": original["dwMaxHP"], "level": level[0], "hp": hp[0],
                    "source": row["url"], "source_sha256": row["sha256"],
                    "catalogue_representative_id": representative["id"], "catalogue_level_range": list_level,
                    "outside_representative_range": outside_list_range}
        if monster["bLevel"] == level[0] and original["dwMaxHP"] == hp[0]:
            verified.append(evidence)
        else:
            evidence["new_attr"] = next(free)
            corrections.append(evidence)
    document = {"source": catalogue["source"], "restored_fields": ["level", "maximum_hp"],
                "identity": "Exact original-client ID and normalized name; values from the same ID-specific official detail URL",
                "grouped_catalogue": "The catalogue omits same-name variants, including other regions/levels; direct ID details must publish singleton values. No arbitrary range endpoint is selected.",
                "unverified_fields": ["experience", "damage", "defence", "mana", "drops", "spawns"],
                "variant_capture": {"expected": len(expected_variants), "pending": len(pending_variants), "errors": variant_errors},
                "detail_records": len(details), "corrections": corrections, "already_verified": verified, "skipped": skipped}
    args.reference_output.parent.mkdir(parents=True, exist_ok=True)
    args.reference_output.write_text(json.dumps(document, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    if not corrections:
        raise ValueError("There are no new corrections")
    values = []
    for row in corrections:
        source = row["source"].replace("'", "''")
        values.append(f"({row['id']},{row['old_level']},{row['old_attr']},{row['old_hp']},{row['new_attr']},"
                      f"{row['level']},{row['hp']},N'{source}','{row['source_sha256']}')")
    header = SQL_HEADER.replace("Gameforge/monster-restoration.json", "Gameforge/normalization/monster-details-restoration.json")
    batches=[",\n".join(values[start:start+500]) for start in range(0,len(values),500)]
    args.sql_output.write_text(header + ";\nINSERT #GameforgeReference VALUES\n".join(batches) + SQL_FOOTER, encoding="utf-8")
    print(json.dumps({"detail_records": len(details), "corrections": len(corrections),
                      "level_changes": sum(row["old_level"] != row["level"] for row in corrections),
                      "already_verified": len(verified), "skipped": len(skipped)}))


if __name__ == "__main__":
    main()
