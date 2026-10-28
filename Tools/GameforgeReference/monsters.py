"""Capture the public Gameforge monster database and audit local levels by published monster ID."""
import argparse
import datetime
import hashlib
import html
import json
from pathlib import Path
import re
import threading
import time
from urllib.error import URLError, HTTPError
from urllib.parse import urljoin, urlparse
from urllib.request import Request, urlopen


def text(markup):
    return " ".join(html.unescape(re.sub(r"<[^>]+>", " ", markup)).split())


def bounds(value):
    match = re.fullmatch(r"(\d+)(?:\s*-\s*(\d+))?", value)
    if not match:
        raise ValueError(f"Unrecognized numeric range: {value!r}")
    lo, hi = int(match[1]), int(match[2] or match[1])
    if lo > hi:
        raise ValueError(f"Reversed numeric range: {value!r}")
    return [lo, hi]


class Source:
    def __init__(self, base, cache):
        if base not in ("https://cz.4story.gameforge.com", "https://en.4story.gameforge.com"):
            raise ValueError("Only the official CZ and EN Gameforge databases are supported")
        self.base, self.cache, self.last = base, cache, 0.0
        self.lock = threading.Lock()
        cache.mkdir(parents=True, exist_ok=True)

    def get(self, url):
        url = urljoin(self.base, html.unescape(url)).replace(":443/", "/")
        if urlparse(url).hostname != urlparse(self.base).hostname:
            raise ValueError(f"Unexpected source host: {url}")
        url = "https://" + url.removeprefix("https://").removeprefix("http://")
        key = hashlib.sha256(url.encode()).hexdigest()
        path = self.cache / (key + ".html")
        if not path.exists():
            for attempt in range(4):
                with self.lock:
                    time.sleep(max(0, 0.4 - (time.monotonic() - self.last)))
                    self.last = time.monotonic()
                request = Request(url, headers={"User-Agent": "4Dev-Gameforge-reference-audit/1.0"})
                try:
                    with urlopen(request, timeout=30) as response:
                        if urlparse(response.url).hostname != urlparse(self.base).hostname:
                            raise ValueError("The official database redirected to another host")
                        payload = response.read(5_000_001)
                        if len(payload) > 5_000_000:
                            raise ValueError("Unexpectedly large database response")
                    break
                except (URLError, TimeoutError) as error:
                    if isinstance(error, HTTPError) and error.code not in (408, 429, 500, 502, 503, 504):
                        raise
                    if attempt == 3:
                        raise
                    time.sleep(2 ** attempt)
            path.write_bytes(payload)
            (self.cache / (key + ".json")).write_text(json.dumps({
                "url": url, "retrieved_utc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
                "sha256": hashlib.sha256(payload).hexdigest()
            }, indent=2) + "\n", encoding="utf-8")
        return path.read_text(encoding="utf-8"), url, hashlib.sha256(path.read_bytes()).hexdigest()


def parse_listing(markup, source):
    rows = []
    for match in re.finditer(r'<tr\b[^>]*data-target="(monster/details/(\d+)/[^\"]*)"[^>]*>(.*?)</tr>', markup, re.S):
        cells = re.findall(r"<td\b[^>]*>(.*?)</td>", match[3], re.S)
        if len(cells) != 3:
            raise ValueError("Unrecognized monster listing row")
        rows.append({"id": int(match[2]), "name": text(cells[0]), "level": bounds(text(cells[1])),
                     "region": text(cells[2]), "url": urljoin(source + "/", match[1])})
    if not rows:
        raise ValueError("No monster rows found; refusing to treat an error page as an empty reference")
    totals = re.findall(r'class="enc_total"[^>]*>\s*(\d+)', markup)
    if not totals or len(set(totals)) != 1:
        raise ValueError("Missing or inconsistent listing totals")
    return rows, int(totals[0])


def capture(source, output):
    first, _, _ = source.get("/monster/browse/1?level_min=1&level_max=140&map_id=0&query=")
    _, total = parse_listing(first, source.base)
    rows, pages = [], []
    for page in range(1, (total + 9) // 10 + 1):
        markup, url, digest = source.get(f"/monster/browse/{page}?level_min=1&level_max=140&map_id=0&query=")
        found, current_total = parse_listing(markup, source.base)
        if current_total != total:
            raise ValueError("The database changed during capture; use a fresh cache")
        rows.extend(found)
        pages.append({"url": url, "sha256": digest})
        if page % 10 == 0:
            print(f"Gameforge: captured page {page}, {len(rows)}/{total} monster listings", flush=True)
    if len(rows) != total or len({row["id"] for row in rows}) != total:
        raise ValueError("Listing totals or unique IDs do not reconcile; no reference was saved")
    document = {"source": source.base + "/monster", "captured_utc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
                "release_version": None, "scope": "Published monster IDs, names, level ranges and regions; not a full server database",
                "pages": pages, "monsters": sorted(rows, key=lambda row: row["id"])}
    output.parent.mkdir(parents=True, exist_ok=True)
    with output.open("x", encoding="utf-8") as stream:
        json.dump(document, stream, ensure_ascii=False, indent=2)
        stream.write("\n")
    print(f"Captured {total} published monsters: {output}")


def details(source, row):
    markup, url, digest = source.get(row["url"])
    # Read only the first detail table; the remaining tables list drops and other unrelated values.
    fields = {}
    for match in re.finditer(r"<tr\b[^>]*>(.*?)</tr>", markup, re.S):
        cells = re.findall(r"<t[dh]\b[^>]*>(.*?)</t[dh]>", match[1], re.S)
        if len(cells) == 2:
            fields[text(cells[0]).rstrip(":")] = text(cells[1])
    labels = {"level": ("Level",), "health": ("Health", "Život"), "damage": ("Damage", "Poškození"),
              "magic_damage": ("Magic Damage", "Magické poškození")}
    result = {"id": row["id"], "url": url, "sha256": digest}
    for name, options in labels.items():
        value = next((fields[label] for label in options if label in fields), None)
        if value is None:
            raise ValueError(f"Missing {name} in official detail page {url}")
        result[name] = bounds(value)
    if result["level"] != row["level"]:
        raise ValueError(f"Detail/listing level mismatch: {url}")
    return result


def audit(reference, database, output, source):
    public = json.loads(reference.read_text(encoding="utf-8"))
    local = {row["wID"]: row for row in json.loads(database.read_text(encoding="utf-8-sig"))["monsters"]}
    rows = []
    for row in public["monsters"]:
        if row["id"] not in local:
            continue
        actual = local[row["id"]]
        lo, hi = row["level"]
        if not lo <= actual["bLevel"] <= hi:
            entry = {**row, "local_level": actual["bLevel"], "local_attr_id": actual["wMonAttr"]}
            if lo == hi:
                entry["official_details"] = details(source, row)
            rows.append(entry)
    document = {"source": public["source"], "reference_monsters": len(public["monsters"]),
                "shared_ids": sum(row["id"] in local for row in public["monsters"]),
                "out_of_range": rows, "note": "Matching levels do not prove matching attributes, spawns, rewards or release version"}
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(document, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(f"Audited {document['shared_ids']} shared IDs; {len(rows)} levels outside the published range: {output}")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("action", choices=("capture", "audit"))
    parser.add_argument("--base", default="https://cz.4story.gameforge.com")
    parser.add_argument("--cache", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--reference", type=Path)
    parser.add_argument("--database", type=Path)
    args = parser.parse_args()
    source = Source(args.base, args.cache)
    if args.action == "capture":
        capture(source, args.output)
    else:
        if not args.reference or not args.database:
            parser.error("audit requires --reference and --database")
        audit(args.reference, args.database, args.output, source)


if __name__ == "__main__":
    main()
