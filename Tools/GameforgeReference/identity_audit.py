"""Join freshly decoded original-client IDs to the public Gameforge catalogues, without changing gameplay data."""
import argparse
import collections
import datetime
import hashlib
import html
import json
from pathlib import Path
import re
import sqlite3
import unicodedata
import zlib


def load(path):return json.loads(path.read_text(encoding="utf-8-sig"))
def normal(value):return "".join(c for c in unicodedata.normalize("NFKD",value).casefold() if c.isalnum() and not unicodedata.combining(c))
def escape(value):return html.escape(str(value),quote=True)


def direct_monsters(data,public):
    path=public/"monster-variants.sqlite"
    if not path.exists():return
    lookup={row["id"]:row for row in data["records"]}
    names={normal(row["wiki_name"]) for row in data["records"] if row["wiki_name"]}
    expected={row["id"] for row in data["records"] if row["status"]=="client_only" and any(normal(name) in names for name in row["client_names"])}
    found=set();errors=[]
    with sqlite3.connect(path) as store:
        for ident,url,digest,blob in store.execute("SELECT id,url,sha256,facts FROM details WHERE kind='monster'"):
            facts=json.loads(zlib.decompress(blob));row=lookup[ident]
            if normal(facts["heading"]) not in {normal(name) for name in row["client_names"]}:
                raise ValueError("Direct ID detail identity does not agree with the original client")
            if not re.search(r'/monster/details/'+str(ident)+r'(?:/|$)',url):raise ValueError("Direct ID URL mismatch")
            row["direct_detail"]={"url":url,"sha256":digest,"heading":facts["heading"],"level":facts["fields"].get("Level"),
                                  "health":facts["fields"].get("Život"),"catalogue_representative_id":facts["catalogue_representative_id"]}
            found.add(ident)
        errors=[{"id":ident,"reason":reason} for ident,reason in store.execute("SELECT id,error FROM errors WHERE kind='monster'")]
    data["direct_variant_check"]={"candidate_ids":len(expected),"verified_same_name_details":len(found),
                                  "unconfirmed":errors,"pending":len(expected-found-{row["id"] for row in errors})}


def domain(kind,local,public):
    client=collections.defaultdict(list)
    key={"item":"wItemID","quest":"dwQuestID","npc":"wID","monster":"id"}[kind]
    for row in local:client[int(row[key])].append(row)
    wiki={row["id"]:row for row in public["records"]}
    records=[]
    for ident in sorted(set(client)|set(wiki)):
        source=client.get(ident,[]);published=wiki.get(ident)
        names=sorted({row.get("szNAME",row.get("title" if kind=="quest" else "name","")) for row in source})
        normalized={normal(name) for name in names}
        if not source:status="wiki_only"
        elif not published:status="client_only"
        elif published["name"] in names:status="exact_name"
        elif normal(published["name"]) in normalized:status="normalized_name"
        else:status="name_mismatch"
        result={"id":ident,"status":status,"client_names":names,
                "wiki_name":published["name"] if published else None,"wiki_url":published["url"] if published else None}
        if len(normalized)>1:result["ambiguous_client_names"]=True
        if kind=="quest" and source:
            result["client_types"]=sorted({row["bType"] for row in source})
            result["client_disabled"]=all(row["bType"]==0 for row in source)
        if published and source and kind in ("quest","item"):
            value=published["cells"][1]
            if re.fullmatch(r"\d+",value):
                result["wiki_list_level"]=int(value)
                result["client_levels"]=sorted({row["bLevel"] for row in source})
                result["level_difference"]=int(value) not in result["client_levels"]
        records.append(result)
    counts=collections.Counter(row["status"] for row in records)
    shared=len(client.keys()&wiki.keys())
    return {"source":public["source"],"listing_url":public["pages"][0]["url"],"client_unique_ids":len(client),"client_records":len(local),"wiki_ids":len(wiki),
            "shared_ids":shared,"same_name":counts["exact_name"]+counts["normalized_name"],
            "exact_name":counts["exact_name"],"normalized_name":counts["normalized_name"],"name_mismatches":counts["name_mismatch"],
            "wiki_only":counts["wiki_only"],"client_only":counts["client_only"],
            "level_differences":sum(row.get("level_difference",False) for row in records),
            "disabled_client_quests_listed_on_wiki":sum(row.get("client_disabled",False) and row["wiki_url"] is not None for row in records),
            "records":records}


def report(document,path):
    labels={"item":"Předměty","monster":"Monstra","npc":"NPC","quest":"Questy"}
    out=['<!doctype html><html lang="cs"><meta charset="utf-8"><title>Gameforge — porovnání ID</title>',
         '<style>body{font:15px/1.55 system-ui;max-width:1300px;margin:35px auto;padding:0 22px;color:#182338;background:#fafbfe}table{border-collapse:collapse;width:100%;margin:18px 0}td,th{text-align:left;padding:9px 12px;border:1px solid #dce2ec}th{background:#eaf0f9}tr:nth-child(even){background:#f1f4f9}a{color:#1455b9}code{font-size:13px}details{margin:25px 0}summary{font-size:19px;font-weight:650;cursor:pointer}.note{background:#eaf0f9;padding:15px 20px;border-radius:8px}h1{line-height:1.2}</style>',
         '<h1>ID původního klienta × veřejná databáze Gameforge</h1>',
         '<p>Zdroj klienta: <code>C:\\Program Files (x86)\\4Story</code>. Čteny byly přímo TItem.tcd, TMon.tcd, TNPC*.tcd a TQuest.mpq; každý vstup byl ověřen proti připnutému SHA-256.</p><p>Webové katalogy zahrnují všechny třídy a země v rozsahu úrovní 0–140; monstra používají původní snímek 1–140. Nejde jen o výchozí první stránku webu.</p>',
         '<table><tr><th>Oblast</th><th>ID v klientu</th><th>ID v přehledu webu</th><th>Společná ID</th><th>Shoda názvu</th><th>Jiný název</th><th>Jen v přehledu webu</th><th>V klientu, mimo přehled</th></tr>']
    for kind,data in document["domains"].items():
        out.append('<tr><td>'+labels[kind]+'</td>'+''.join('<td>'+str(data[key])+'</td>' for key in ("client_unique_ids","wiki_ids","shared_ids","same_name","name_mismatches","wiki_only","client_only"))+'</tr>')
    out.extend(['</table><div class="note">Shoda názvu zahrnuje přesný text a shodu po odstranění rozdílů v diakritice, velikosti písmen, mezerách a interpunkci. Jiný název vyžaduje kontrolu; sám neprokazuje jiné použití ID. Nepřítomnost na veřejném webu neprokazuje custom obsah. Soubor TMon neobsahuje serverový level monstra.</div>',
                '<p>Přesné číslo patche místního klienta ani webových dat není ověřeno. Tabulka popisuje skutečný průnik obou zachycených zdrojů.</p>'])
    levels=[(labels[kind],data["shared_ids"],data["level_differences"]) for kind,data in document["domains"].items() if kind in ("item","quest")]
    out.append('<p>Kontrola levelů v přehledu: '+escape('; '.join(f'{label}: {count} porovnaných ID, {different} rozdílů' for label,count,different in levels))+'.</p>')
    direct=document["domains"]["monster"].get("direct_variant_check")
    if direct:
        out.append('<h2>Další ID monster ověřená přímo v detailech</h2><p>Přehled slučuje nebo vynechává některé varianty stejných jmen. Z '+str(direct["candidate_ids"])+
                   ' dalších klientských ID bylo přímo podle ID a názvu potvrzeno '+str(direct["verified_same_name_details"])+
                   '; nepotvrzených požadavků: '+str(len(direct["unconfirmed"]))+'; čeká na zpracování: '+str(direct["pending"])+'. Tyto záznamy nejsou zahrnuté v počtu ID veřejného přehledu výše.</p><p>Například <a href="https://cz.4story.gameforge.com/monster/details/283/kamenovrhac_kapaku">ID 283</a> má level 11 a <a href="https://cz.4story.gameforge.com/monster/details/412/kamenovrhac_kapaku">ID 412</a> level 17. Rozsah 11–17 z přehledu se proto nesmí libovolně přiřadit jednomu ID. Údaje a zdrojové SHA-256 jednotlivých detailů jsou v JSON.</p>')
    for kind,data in document["domains"].items():
        issues=[row for row in data["records"] if row["status"] in ("name_mismatch","wiki_only") or row.get("ambiguous_client_names") or (row.get("client_disabled") and row["wiki_url"])]
        out.append('<details><summary>'+labels[kind]+': sporná nebo chybějící přiřazení ('+str(len(issues))+')</summary><table><tr><th>ID</th><th>Klient</th><th>Gameforge web</th><th>Stav</th></tr>')
        for row in issues:
            status=[]
            if row["status"]=="wiki_only":status.append("ID v klientu chybí")
            if row["status"]=="name_mismatch":status.append("jiný název")
            if row.get("ambiguous_client_names"):status.append("více jmen pro klientské ID")
            if row.get("client_disabled"):status.append("quest v klientu vypnutý (typ 0)")
            name=escape(row["wiki_name"] or "")
            if row["wiki_url"]:name='<a href="'+escape(row["wiki_url"])+'">'+name+'</a>'
            out.append('<tr><td>'+str(row["id"])+'</td><td>'+escape(' / '.join(row["client_names"]))+'</td><td>'+name+'</td><td>'+escape('; '.join(status))+'</td></tr>')
        out.append('</table></details>')
    out.append('<p>Úplné záznamy včetně ID pouze v klientu jsou v <a href="identity-audit.json">identity-audit.json</a>. Tato kontrola neprovádí změny v herních databázích.</p></html>')
    path.write_text('\n'.join(out),encoding="utf-8")


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--facts",type=Path,required=True);parser.add_argument("--public",type=Path,required=True)
    parser.add_argument("--output",type=Path,required=True)
    args=parser.parse_args();args.output.mkdir(parents=True,exist_ok=True)
    evidence=load(args.facts/"evidence.json")
    npc_info=load(args.facts/"npc_info.json")
    types={"item":load(args.facts/"items.json"),"monster":load(args.facts/"monsters.json"),
           "npc":load(args.facts/"npcs.json"),"quest":load(args.facts/"quests.json")["quests"]}
    document={"generated_utc":datetime.datetime.now(datetime.timezone.utc).isoformat(),
              "client_source":"C:\\Program Files (x86)\\4Story","client_evidence":evidence,
              "independent_npc_id_check":{"source":"Tcd/TNPCINFO.tcd","ids":len(npc_info),"matches_spawn_id_set":
                  {row["id"] for row in npc_info}=={row["wID"] for row in types["npc"]}},
              "method":"Exact ID join; exact or accent/case/punctuation-normalized names; no fuzzy identity auto-approval",
              "domains":{}}
    for kind,rows in types.items():
        source=args.public/(kind+"-catalogue.json");catalogue=load(source)
        data=domain(kind,rows,catalogue)
        data["catalogue_sha256"]=hashlib.sha256(source.read_bytes()).hexdigest()
        data["catalogue_captured_utc"]=catalogue["captured_utc"]
        if kind=="monster":direct_monsters(data,args.public)
        document["domains"][kind]=data
    (args.output/"identity-audit.json").write_text(json.dumps(document,ensure_ascii=False,indent=2)+"\n",encoding="utf-8")
    report(document,args.output/"identity-audit.html")
    print(json.dumps({kind:{key:value for key,value in data.items() if key!="records"} for kind,data in document["domains"].items()},ensure_ascii=True,indent=2))


if __name__=="__main__":main()
