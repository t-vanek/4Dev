"""Verify complete affected chart rows against the baseline plus explicitly planned field changes."""
import argparse
import collections
import concurrent.futures
import copy
import json
from pathlib import Path
import struct

from normalization import equal
from sql_snapshot import sql


def load(path):return json.loads(path.read_text(encoding="utf-8-sig"))


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--snapshot",type=Path,required=True);parser.add_argument("--database",required=True)
    parser.add_argument("--client-plan",type=Path,required=True);parser.add_argument("--quest-plan",type=Path,required=True)
    parser.add_argument("--shop-plan",type=Path,required=True);parser.add_argument("--monster-plan",type=Path)
    parser.add_argument("--item-public-plan",type=Path)
    parser.add_argument("--starter-plan",type=Path)
    parser.add_argument("--output",type=Path,required=True)
    args=parser.parse_args();schema=load(args.snapshot/"schema.json")
    client,quest,shops=load(args.client_plan),load(args.quest_plan),load(args.shop_plan)
    plans=client["changes"]+quest["changes"]
    if args.item_public_plan:plans+=load(args.item_public_plan)["changes"]
    tables={row["table"] for row in plans}|{"TNPCITEMCHART","TMONSTERCHART","TMONATTRCHART","TQUESTTERMCHART",
        "TQCONDITIONCHART","TNPCCHART","TSTARTITEMCHART","TSTARTSKILL","TSTARTHOTKEY","TITEMATTRCHART","TFORMULACHART"}
    expected={table:load(args.snapshot/(table+".json")) for table in tables}
    for row in plans:
        matches=[r for r in expected[row["table"]] if r[row["key_column"]]==row["key"]]
        if len(matches)!=1:raise ValueError("A planned field does not have exactly one baseline row")
        if not equal(matches[0][row["column"]],row["before"]):raise ValueError("Plan and captured baseline differ")
        matches[0][row["column"]]=row["after"]
    for row in shops["changes"]:
        expected["TNPCITEMCHART"]=[r for r in expected["TNPCITEMCHART"] if r["wNpcID"]!=row["id"]]
        expected["TNPCITEMCHART"].extend({"wNpcID":row["id"],"dwItemID":i} for i in row["after"])
    if args.starter_plan:
        starters=load(args.starter_plan)
        removed={(r["bClassID"],r["wSkillID"]) for r in starters["removed_starter_rows"]}
        expected["TSTARTSKILL"]=[r for r in expected["TSTARTSKILL"] if (r["bClassID"],r["wSkillID"]) not in removed]
        for row in starters["cleared_hotkeys"]:
            target=next(r for r in expected["TSTARTHOTKEY"] if r["bClassID"]==row["class"] and r["bInvenID"]==row["page"])
            target[f"bType{row['slot']}"]=0;target[f"wID{row['slot']}"]=0
    monster=load(args.monster_plan) if args.monster_plan else {"corrections":[]}
    attributes={(r["wID"],r["bLevel"]):r for r in expected["TMONATTRCHART"]}
    monsters={r["wID"]:r for r in expected["TMONSTERCHART"]}
    for row in monster["corrections"]:
        original=copy.deepcopy(attributes[(row["old_attr"],row["old_level"])])
        original.update(wID=row["new_attr"],bLevel=row["level"],dwMaxHP=row["hp"])
        expected["TMONATTRCHART"].append(original)
        monsters[row["id"]].update(bLevel=row["level"],wMonAttr=row["new_attr"])
    def read(table):return sql("SELECT * FROM dbo.["+table+"] FOR JSON PATH,INCLUDE_NULL_VALUES;",args.database)
    with concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool:
        actual=dict(zip(sorted(tables),pool.map(read,sorted(tables))))
    checked=[]
    for table in sorted(tables):
        floating={r["column_name"] for r in schema if r["table_name"]==table and r["data_type"] in ("real","float")}
        def canonical(rows):
            return collections.Counter(json.dumps({key:struct.pack('<f',value).hex() if key in floating and value is not None else value
                                                    for key,value in row.items()},sort_keys=True,ensure_ascii=True) for row in rows)
        before,after=canonical(expected[table]),canonical(actual[table])
        if before!=after:
            missing=list((before-after).elements());unexpected=list((after-before).elements())
            raise ValueError(f"Full chart mismatch in {table}: missing={missing[:1]} unexpected={unexpected[:1]}")
        checked.append({"table":table,"rows":len(actual[table]),"result":"All columns and rows match the baseline plus the explicit plan"})
    # Compute actual shop prices using final SQL values, not just the selected original-client formula.
    final_items={r["wItemID"]:r for r in actual["TITEMCHART"]}
    final_attrs={r["wID"]:r for r in actual["TITEMATTRCHART"]}
    final_money={r["bLevel"]:r["dwMoney"] for r in actual["TLEVELCHART"]}
    for row in shops["changes"]+shops["already_matching"]:
        for entry in row["published_items"]:
            item=final_items[entry["item_id"]]
            grade=final_attrs[item["wAttrID"]]["bGrade"] if item["wAttrID"] else item["bLevel"]
            price=int(struct.unpack('<f',struct.pack('<f',final_money[grade]*struct.unpack('<f',struct.pack('<f',item["fPrice"]))[0]))[0]+.99)
            if price!=entry["price"]:raise ValueError("Final SQL shop price differs from the independently published price")
    result={"database":args.database,"checked_tables":checked,"field_changes":len(plans),
            "monster_corrections":len(monster["corrections"]),"restored_shops":len(shops["changes"]),
            "assertions":["Full affected rows match only the explicit changes", "Original monster attribute rows and EXP retained",
                          "Quest objectives/conditions and NPC placement retained; starter charts change only as explicitly planned", "Published gold-shop prices match final SQL"]}
    args.output.write_text(json.dumps(result,ensure_ascii=False,indent=2)+"\n",encoding="utf-8")
    print(json.dumps({"verified_tables":len(checked),"verified_rows":sum(r["rows"] for r in checked),"database":args.database}))


if __name__=="__main__":main()
