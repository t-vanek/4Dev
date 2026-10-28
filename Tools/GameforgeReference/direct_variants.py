"""Look up original-client variants hidden by the public catalogue's name grouping."""
import argparse
import concurrent.futures
import json
from pathlib import Path
import re
import sqlite3
import zlib

from catalogue import parse_detail
from identity_audit import normal
from monsters import Source


def candidates(audit, catalogue, kind):
    by_name = {}
    for row in catalogue["records"]:
        by_name.setdefault(normal(row["name"]), row)
    result = []
    for row in audit["domains"][kind]["records"]:
        if row["status"] != "client_only":
            continue
        matches = {by_name[normal(name)]["id"] for name in row["client_names"] if normal(name) in by_name}
        if len(matches) != 1:
            continue
        representative = next(value for value in by_name.values() if value["id"] == next(iter(matches)))
        url = re.sub(r"(/" + kind + r"/details/)\d+", lambda m: m[1] + str(row["id"]), representative["url"], count=1)
        result.append({"id": row["id"], "name": representative["name"], "url": url,
                       "client_names": row["client_names"], "catalogue_representative_id": representative["id"]})
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--audit", type=Path, required=True)
    parser.add_argument("--public", type=Path, required=True)
    parser.add_argument("--cache", type=Path, required=True)
    parser.add_argument("--kind", choices=("monster", "item", "npc"), required=True)
    args = parser.parse_args()
    audit = json.loads(args.audit.read_text(encoding="utf-8"))
    catalogue = json.loads((args.public / (args.kind + "-catalogue.json")).read_text(encoding="utf-8"))
    rows = candidates(audit, catalogue, args.kind)
    source = Source("https://cz.4story.gameforge.com", args.cache)
    store = sqlite3.connect(args.public / (args.kind + "-variants.sqlite"))
    store.execute("CREATE TABLE IF NOT EXISTS details(kind TEXT,id INTEGER,url TEXT,sha256 TEXT,facts BLOB,PRIMARY KEY(kind,id))")
    store.execute("CREATE TABLE IF NOT EXISTS errors(kind TEXT,id INTEGER,error TEXT,PRIMARY KEY(kind,id))")
    done = {row[0] for row in store.execute("SELECT id FROM details")}
    todo = [row for row in rows if row["id"] not in done]
    print(f"{args.kind}: {len(rows)} same-name original-client variants, {len(todo)} remaining", flush=True)
    with concurrent.futures.ThreadPoolExecutor(max_workers=1) as pool:
        futures = {pool.submit(parse_detail, source, args.kind, row): row for row in todo}
        for index, future in enumerate(concurrent.futures.as_completed(futures), 1):
            row = futures[future]
            try:
                result = future.result()
                facts = json.loads(zlib.decompress(result[4]))
                # Monster detail headings contain the entity name, without an NPC title prefix.
                if args.kind == "monster" and normal(facts["heading"]) not in {normal(name) for name in row["client_names"]}:
                    raise ValueError("The direct detail heading differs from the original client's name")
                facts["client_names"] = row["client_names"]
                facts["catalogue_representative_id"] = row["catalogue_representative_id"]
                result = result[:4] + (zlib.compress(json.dumps(facts, ensure_ascii=False).encode(), 6),)
                store.execute("INSERT OR REPLACE INTO details VALUES (?,?,?,?,?)", result)
                store.execute("DELETE FROM errors WHERE kind=? AND id=?", (args.kind, row["id"]))
            except Exception as error:
                store.execute("INSERT OR REPLACE INTO errors VALUES (?,?,?)", (args.kind, row["id"], str(error)))
                print(f"{args.kind} {row['id']}: {error}", flush=True)
            if index % 25 == 0:
                store.commit()
                print(f"{args.kind}: processed {index}/{len(todo)} variants", flush=True)
    store.commit()
    print("Captured", store.execute("SELECT COUNT(*) FROM details").fetchone()[0], "variants; errors",
          store.execute("SELECT COUNT(*) FROM errors").fetchone()[0], flush=True)
    store.close()


if __name__ == "__main__":
    main()
