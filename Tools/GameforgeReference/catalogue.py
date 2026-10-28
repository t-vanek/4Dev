"""Capture all public Gameforge catalogues and structured detail tables, with resumable evidence caching."""
import argparse
import concurrent.futures
import datetime
from html.parser import HTMLParser
import html
import json
from pathlib import Path
import re
import sqlite3
from urllib.parse import urljoin, urlsplit, urlunsplit, parse_qsl, urlencode
import zlib
from monsters import Source, text


class Tables(HTMLParser):
    def __init__(self):
        super().__init__(convert_charrefs=True)
        self.stack, self.tables = [], []
    def handle_starttag(self, tag, attrs):
        attrs = dict(attrs)
        if tag == "table":
            self.stack.append({"class": attrs.get("class", ""), "rows": [], "row": None, "cell": None})
        if not self.stack:
            return
        table = self.stack[-1]
        if tag == "tr":
            table["row"] = []
        elif tag in ("td", "th") and table["row"] is not None:
            table["cell"] = {"header": tag == "th", "chunks": [], "links": [], "images": []}
        elif tag == "a" and table["cell"] is not None and attrs.get("href"):
            table["cell"]["links"].append(html.unescape(attrs["href"]))
        elif tag == "img" and table["cell"] is not None:
            table["cell"]["images"].append({key: attrs[key] for key in ("src", "alt", "title") if key in attrs})
    def handle_data(self, value):
        if self.stack and self.stack[-1]["cell"] is not None:
            self.stack[-1]["cell"]["chunks"].append(value)
    def handle_endtag(self, tag):
        if not self.stack:
            return
        table = self.stack[-1]
        if tag in ("td", "th") and table["cell"] is not None:
            cell = table["cell"]
            cell["text"] = " ".join(" ".join(cell.pop("chunks")).split())
            table["row"].append(cell)
            table["cell"] = None
        elif tag == "tr" and table["row"] is not None:
            if table["row"]:
                table["rows"].append(table["row"])
            table["row"] = None
        elif tag == "table":
            self.stack.pop()
            self.tables.append({"class": table["class"], "rows": table["rows"]})


def listing(markup, base, kind):
    rows = []
    pattern = r'<tr\b([^>]*data-target="(' + kind + r'/details/(\d+)/[^\"]*)"[^>]*)>(.*?)</tr>'
    for match in re.finditer(pattern, markup, re.S):
        # The publisher emits unescaped quotes inside data-title (for example quest 234).
        # It is the final attribute on these rows; read to its final quote rather than truncating the name.
        name = re.search(r'data-title="(.*)"\s*$', match[1], re.S)
        if name is None:
            raise ValueError("Missing catalogue name")
        cells = re.findall(r"<td\b[^>]*>(.*?)</td>", match[4], re.S)
        images=[]
        for cell in cells:
            images.append([{key:html.unescape(value) for key,value in re.findall(r'(src|alt|title)="([^"]*)"',image)}
                           for image in re.findall(r'<img\b[^>]*>',cell)])
        rows.append({"id": int(match[3]), "name": html.unescape(name[1]), "url": urljoin(base + "/", match[2]),
                     "cells": [text(cell) for cell in cells],"cell_images":images})
    totals = re.findall(r'class="enc_total"[^>]*>\s*(\d+)', markup)
    if not totals or len(set(totals)) != 1 or not rows:
        raise ValueError(f"Invalid {kind} catalogue page")
    return rows, int(totals[0])


def capture_list(source, kind, output, reparse=False):
    if output.exists():
        result=json.loads(output.read_text(encoding="utf-8"))
        if reparse:
            rows=[]
            for page in result["pages"]:
                markup,_,digest=source.get(page["url"])
                if digest!=page["sha256"]:raise ValueError("The cached catalogue changed")
                found,total=listing(markup,source.base,kind)
                if total!=len(result["records"]):raise ValueError("Catalogue totals changed")
                rows.extend(found)
            if {row["id"] for row in rows}!={row["id"] for row in result["records"]} or len(rows)!=len(result["records"]):
                raise ValueError("Reparsed catalogue IDs changed")
            result["records"]=sorted(rows,key=lambda row:row["id"])
            result["parser_version"]=2
            output.write_text(json.dumps(result,ensure_ascii=False,indent=2)+"\n",encoding="utf-8")
            print(f"{kind}: reparsed {len(rows)} records from the same captured HTML",flush=True)
        return result
    legacy = Path(__file__).resolve().parents[2] / "Gameforge/monsters-web.json"
    if kind == "monster" and legacy.exists():
        pinned = json.loads(legacy.read_text(encoding="utf-8"))
        result = {key: pinned[key] for key in ("source", "captured_utc", "release_version", "pages")}
        result["records"] = [{"id": row["id"], "name": row["name"], "url": row["url"],
                              "cells": [row["name"], "-".join(map(str, row["level"])), row["region"]]}
                             for row in pinned["monsters"]]
        output.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        return result
    root, _, _ = source.get("/" + kind)
    next_url = re.search(r'class="enc_next" href="([^"]+)"', root)
    if not next_url:
        raise ValueError(f"Missing {kind} pagination link")
    split = urlsplit(html.unescape(next_url[1]))
    params = dict(parse_qsl(split.query, keep_blank_values=True))
    if "level_min" in params:
        params["level_min"], params["level_max"] = "0", "140"
    path = "/" + kind + "/browse/{}"
    def url(page):
        return urlunsplit(("https", urlsplit(source.base).netloc, path.format(page), urlencode(params), ""))
    first, _, _ = source.get(url(1))
    _, total = listing(first, source.base, kind)
    rows, pages = [], []
    for page in range(1, (total + 9) // 10 + 1):
        markup, page_url, digest = source.get(url(page))
        found, check_total = listing(markup, source.base, kind)
        if check_total != total:
            raise ValueError("Catalogue totals changed during capture")
        rows.extend(found)
        pages.append({"url": page_url, "sha256": digest})
        if page % 25 == 0:
            print(f"{kind}: catalogue page {page}, {len(rows)}/{total}", flush=True)
    if len(rows) != total or len({row["id"] for row in rows}) != total:
        raise ValueError(f"Non-reconciling {kind} catalogue")
    result = {"source": source.base + "/" + kind, "captured_utc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
              "release_version": None, "pages": pages, "records": sorted(rows, key=lambda row: row["id"])}
    output.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(f"{kind}: {total} catalogue records captured", flush=True)
    return result


def parse_detail(source, kind, row):
    markup, url, digest = source.get(row["url"])
    heading = re.search(r"<h1\b[^>]*>(.*?)</h1>", markup, re.S)
    if not heading:
        raise ValueError("Detail page has no heading")
    heading_text = text(heading[1])
    if heading_text.casefold() in {
        'předměty v iberii', 'nestvůry v iberii', 'úkoly v iberii', 'npc v iberii',
        'items in iberia', 'monsters in iberia', 'quests in iberia', 'npcs in iberia',
    }:
        raise ValueError('The requested detail returned a catalogue landing page, not entity evidence')
    tables = Tables()
    tables.feed(markup)
    fields = {}
    for table in tables.tables:
        for cells in table["rows"]:
            if len(cells) == 2 and cells[0]["header"]:
                fields[cells[0]["text"].rstrip(":")] = cells[1]["text"]
    # Retain named facts and table relationships; never execute page scripts or infer numeric drop odds.
    content = {"id": row["id"], "name": row["name"], "heading": heading_text, "url": url, "sha256": digest,
               "fields": fields, "tables": tables.tables}
    if kind == "quest":
        content["quest"] = quest_facts(markup)
    maps = re.findall(r'/cdn/img/maps/400/map_(\d+)\.[a-z]+', markup)
    dots = re.findall(r"class=['\"]dot['\"][^>]*style=['\"][^'\"]*top:(\d+)px;left:(\d+)px", markup)
    if maps:
        content["map"] = {"map_image_ids": sorted(set(map(int,maps))), "pixel_markers": [[int(x),int(y)] for y,x in dots],
                          "precision": "Map pixels only; not full world coordinates or spawn heights/timers"}
    return kind, row["id"], url, digest, zlib.compress(json.dumps(content, ensure_ascii=False).encode(), 6)


def quest_facts(markup):
    match = re.search(r'id="tabs-1"(.*?)(?=<div[^>]+id="tabs-2")', markup, re.S)
    if not match:
        raise ValueError("Quest details section is missing")
    section = match[1]
    level = re.search(r'class="questlevel"[^>]*>.*?<strong>(.*?)</strong>', section, re.S)
    giver = re.search(r'class="questgiver".*?/npc/details/(\d+)', section, re.S)
    xp = re.search(r'Zkušenost:\s*([\d\s]+)\s*Exp', text(section))
    currency = {}
    for name in ("gold","silver","bronze"):
        number = re.search(r'(\d+)\s*<span class="'+name+r'"', section)
        if number:currency[name]=int(number[1])
    result = {"level_label": text(level[1]) if level else None,"giver_id":int(giver[1]) if giver else None,
              "experience":int(xp[1].replace(" ","")) if xp else None,
              "money":currency.get("gold",0)*1000000+currency.get("silver",0)*1000+currency.get("bronze",0) if currency else None}
    right = section.split('<div class="detail_right">',1)
    if len(right)==2:
        parts = re.split(r'<hr\s*/?>',right[1],maxsplit=1)
        result["objectives"] = text(parts[0])
        reward = parts[1] if len(parts)==2 else ""
        result["reward_items"] = []
        for li in re.findall(r'<li\b[^>]*>(.*?)</li>',reward,re.S):
            item = re.search(r'/item/details/(\d+)',li)
            if item:
                count=re.search(r'[×x]\s*(\d+)',text(li))
                result["reward_items"].append({"id":int(item[1]),"count":int(count[1]) if count else 1,"text":text(li)})
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--cache", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--kinds", nargs="+", default=["monster", "quest", "npc", "item"])
    parser.add_argument("--workers", type=int, default=2, choices=(1, 2))
    parser.add_argument("--lists-only", action="store_true")
    parser.add_argument("--reparse-lists", action="store_true")
    args = parser.parse_args()
    if any(kind not in ("monster", "quest", "npc", "item") for kind in args.kinds):
        parser.error("Unsupported catalogue kind")
    source = Source("https://cz.4story.gameforge.com", args.cache)
    args.output.mkdir(parents=True, exist_ok=True)
    for kind in args.kinds:
        catalogue = capture_list(source, kind, args.output / (kind + "-catalogue.json"),args.reparse_lists)
        if args.lists_only:
            continue
        store = sqlite3.connect(args.output / (kind + "-details.sqlite"))
        store.execute("CREATE TABLE IF NOT EXISTS details(kind TEXT,id INTEGER,url TEXT,sha256 TEXT,facts BLOB,PRIMARY KEY(kind,id))")
        store.execute("CREATE TABLE IF NOT EXISTS errors(kind TEXT,id INTEGER,error TEXT,PRIMARY KEY(kind,id))")
        done = {row[0] for row in store.execute("SELECT id FROM details WHERE kind=?", (kind,))}
        todo = [row for row in catalogue["records"] if row["id"] not in done]
        print(f"{kind}: {len(done)} details cached, {len(todo)} remaining", flush=True)
        with concurrent.futures.ThreadPoolExecutor(max_workers=args.workers) as pool:
            pending = {pool.submit(parse_detail, source, kind, row): row for row in todo}
            count = 0
            for future in concurrent.futures.as_completed(pending):
                row = pending[future]
                try:
                    result = future.result()
                    store.execute("INSERT OR REPLACE INTO details VALUES (?,?,?,?,?)", result)
                    store.execute("DELETE FROM errors WHERE kind=? AND id=?", (kind, row["id"]))
                except Exception as error:
                    store.execute("INSERT OR REPLACE INTO errors VALUES (?,?,?)", (kind, row["id"], str(error)))
                    print(f"{kind} {row['id']}: {error}", flush=True)
                count += 1
                if count % 25 == 0:
                    store.commit()
                    print(f"{kind}: processed {count}/{len(todo)} new details", flush=True)
        store.commit()
        actual = store.execute("SELECT COUNT(*) FROM details WHERE kind=?", (kind,)).fetchone()[0]
        failures = store.execute("SELECT COUNT(*) FROM errors WHERE kind=?", (kind,)).fetchone()[0]
        print(f"{kind}: {actual}/{len(catalogue['records'])} captured, {failures} errors", flush=True)
        store.close()


if __name__ == "__main__":
    main()
