"""Restore published gold-shop item sets for NPCs with matching original-client identities."""
import argparse
import collections
import hashlib
import json
from pathlib import Path
import re
import sqlite3
import struct
import zlib

from client_facts import items
from identity_audit import normal


def load(path):return json.loads(path.read_text(encoding="utf-8-sig"))


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--snapshot",type=Path,required=True);parser.add_argument("--facts",type=Path,required=True)
    parser.add_argument("--public",type=Path,required=True);parser.add_argument("--cache",type=Path,required=True)
    parser.add_argument("--game",type=Path,required=True);parser.add_argument("--plan",type=Path,required=True)
    parser.add_argument("--sql",type=Path,required=True)
    args=parser.parse_args()
    original=collections.defaultdict(list)
    for row in load(args.facts/"npcs.json"):original[row["wID"]].append(row)
    local={r["wID"]:r for r in load(args.snapshot/"TNPCCHART.json")}
    stock=collections.defaultdict(list)
    for row in load(args.snapshot/"TNPCITEMCHART.json"):stock[row["wNpcID"]].append(row["dwItemID"])
    sql_items={r["wItemID"] for r in load(args.snapshot/"TITEMCHART.json")}
    game_items={r["wItemID"] for r in items((args.game/"Tcd/TItem.tcd").read_bytes())}
    source_items={r["wItemID"]:r for r in load(args.facts/"items.json")}
    levels={r["bLevel"]:r["dwMoney"] for r in load(args.facts/"level_fees.json")}
    attrs={r["wID"]:r for r in load(args.facts/"item_attributes.json")}
    catalogue=load(args.public/"npc-catalogue.json")
    with sqlite3.connect(args.public/"npc-details.sqlite") as connection:
        details={i:json.loads(zlib.decompress(blob)) for i,blob in connection.execute("SELECT id,facts FROM details WHERE kind='npc'")}
    if set(details)!={r["id"] for r in catalogue["records"]}:raise ValueError("NPC capture is incomplete")
    changes=[];verified=[];gaps=[]
    for ident,detail in sorted(details.items()):
        src=original[ident];current=local.get(ident)
        if not current or not src:continue
        if current["bType"]!=2 or any(r["bType"]!=2 for r in src):continue
        if normal(detail["name"]) not in {normal(r["name"]) for r in src}:
            gaps.append({"id":ident,"reason":"Public/client NPC name differs"});continue
        candidates=[t for t in detail["tables"] if t["class"]=="detail_left" and
                    any("/item/details/" in link for row in t["rows"] for cell in row for link in cell["links"])]
        if len(candidates)!=1:
            gaps.append({"id":ident,"reason":"No unique explicit stock table"});continue
        payload=(args.cache/(hashlib.sha256(detail["url"].encode()).hexdigest()+".html")).read_bytes()
        if hashlib.sha256(payload).hexdigest()!=detail["sha256"]:raise ValueError("Changed shop evidence")
        entries=[];reason=None
        for cells in candidates[0]["rows"]:
            if len(cells)!=2 or any(cell["header"] for cell in cells):
                reason="Unsupported stock row";break
            ids={int(m[1]) for link in cells[0]["links"] if (m:=re.search(r'/item/details/(\d+)/',link))}
            price=re.fullmatch(r'(\d+)\s+(\d+)\s+(\d+)',cells[1]["text"])
            if len(ids)!=1 or not price:
                reason="Missing/ambiguous item or gold/silver/bronze price";break
            item_id=next(iter(ids))
            if item_id not in sql_items or item_id not in game_items or item_id not in source_items:
                reason="Published stock item is missing from SQL, selected client or game client";break
            template=source_items[item_id]
            grade=attrs[template["wAttrID"]]["bGrade"] if template["wAttrID"] else template["bLevel"]
            price_expected=int(struct.unpack('<f',struct.pack('<f',levels[grade]*template["fPrice"]))[0]+.99)
            price_published=int(price[1])*1000000+int(price[2])*1000+int(price[3])
            if price_expected!=price_published:
                reason="Published shop price differs from the original-client price formula";break
            entries.append({"item_id":item_id,"price":price_published})
        if reason:
            gaps.append({"id":ident,"reason":reason,"source":detail["url"]});continue
        incoming=sorted({r["item_id"] for r in entries});old=sorted(stock[ident])
        row={"id":ident,"name":detail["name"],"before":old,"after":incoming,"published_items":entries,
             "source":detail["url"],"sha256":detail["sha256"]}
        (verified if old==incoming else changes).append(row)
    plan={"scope":"Exact item sets of normal gold shops (type 2), with client/SQL identity and published-price checks",
          "changes":changes,"already_matching":verified,"gaps":gaps,
          "unverified":"Other NPC types, absent stock tables, PvP prices, magic/crafting shops and NPC positions are retained"}
    args.plan.write_text(json.dumps(plan,ensure_ascii=False,indent=2)+"\n",encoding="utf-8")
    lines=["-- Exact published Gameforge gold-shop stock; sources in Gameforge/normalization/shop-plan.json.",
           "USE [TGame_gsp];","GO","SET QUOTED_IDENTIFIER ON;","SET ANSI_NULLS ON;","SET NOCOUNT ON;","SET XACT_ABORT ON;","GO",
           "BEGIN TRANSACTION;","CREATE TABLE #GfShops(NpcId INT PRIMARY KEY);",
           "CREATE TABLE #GfStock(NpcId INT,ItemId INT,State TINYINT);",
           "CREATE TABLE #GfApply(NpcId INT PRIMARY KEY);"]
    if changes:
        lines.append("INSERT #GfShops VALUES "+",\n".join("("+str(r["id"])+")" for r in changes)+";")
    values=[f"({r['id']},{i},{state})" for r in changes for state,key in ((0,"before"),(1,"after")) for i in r[key]]
    for start in range(0,len(values),500):lines.append("INSERT #GfStock VALUES "+",\n".join(values[start:start+500])+";")
    lines.extend(["IF EXISTS(SELECT 1 FROM #GfShops s LEFT JOIN dbo.TNPCCHART n ON n.wID=s.NpcId WHERE n.wID IS NULL OR n.bType<>2) THROW 51000,'Gold-shop NPC identity/type changed',1;",
        "IF EXISTS(SELECT 1 FROM #GfStock s LEFT JOIN dbo.TITEMCHART i ON i.wItemID=s.ItemId WHERE s.State=1 AND i.wItemID IS NULL) THROW 51000,'Published shop item missing',1;",
        "DECLARE @npc INT,@state TINYINT,@matches TINYINT;",
        "DECLARE shops CURSOR LOCAL FAST_FORWARD FOR SELECT NpcId FROM #GfShops;",
        "OPEN shops; FETCH NEXT FROM shops INTO @npc;",
        "WHILE @@FETCH_STATUS=0 BEGIN",
        "SET @state=0; SET @matches=0;",
        "WHILE @state<=1 BEGIN",
        "IF NOT EXISTS(SELECT dwItemID,COUNT(*) N FROM dbo.TNPCITEMCHART WHERE wNpcID=@npc GROUP BY dwItemID EXCEPT SELECT ItemId,COUNT(*) FROM #GfStock WHERE NpcId=@npc AND State=@state GROUP BY ItemId) AND NOT EXISTS(SELECT ItemId,COUNT(*) N FROM #GfStock WHERE NpcId=@npc AND State=@state GROUP BY ItemId EXCEPT SELECT dwItemID,COUNT(*) FROM dbo.TNPCITEMCHART WHERE wNpcID=@npc GROUP BY dwItemID) BEGIN SET @matches=@matches+1; IF @state=1 SET @matches=@matches+2; END;",
        "SET @state=@state+1; END;",
        "IF @matches=0 THROW 51000,'Shop stock baseline changed',1;",
        "IF @matches<2 INSERT #GfApply VALUES(@npc);",
        "FETCH NEXT FROM shops INTO @npc; END; CLOSE shops; DEALLOCATE shops;",
        "DELETE n FROM dbo.TNPCITEMCHART n JOIN #GfApply a ON a.NpcId=n.wNpcID;",
        "INSERT dbo.TNPCITEMCHART(wNpcID,dwItemID) SELECT s.NpcId,s.ItemId FROM #GfStock s JOIN #GfApply a ON a.NpcId=s.NpcId WHERE s.State=1;",
        "IF EXISTS(SELECT n.wNpcID,n.dwItemID FROM dbo.TNPCITEMCHART n JOIN #GfShops s ON s.NpcId=n.wNpcID EXCEPT SELECT NpcId,ItemId FROM #GfStock WHERE State=1) OR EXISTS(SELECT NpcId,ItemId FROM #GfStock WHERE State=1 EXCEPT SELECT n.wNpcID,n.dwItemID FROM dbo.TNPCITEMCHART n JOIN #GfShops s ON s.NpcId=n.wNpcID) THROW 51000,'Shop stock postcondition failed',1;",
        "SELECT COUNT(*) AS RestoredGoldShops FROM #GfApply;","DROP TABLE #GfApply;","DROP TABLE #GfStock;","DROP TABLE #GfShops;","COMMIT TRANSACTION;","GO"])
    args.sql.write_text("\n".join(lines)+"\n",encoding="utf-8")
    print(json.dumps({"changed_shops":len(changes),"already_matching":len(verified),"gaps":len(gaps)}))


if __name__=="__main__":main()
