"""Build auditable field changes and guarded SQL from decoded official client facts."""
import argparse
import collections
import hashlib
import json
import math
from pathlib import Path
import re
import struct


def load(path):return json.loads(path.read_text(encoding="utf-8-sig"))


def equal(a,b):
    if a is None or b is None:return a is b
    if isinstance(a,float) or isinstance(b,float):return struct.pack("<f",float(a))==struct.pack("<f",float(b))
    return a==b


def sql_value(value,kind):
    if value is None:return "NULL"
    if kind in ("nvarchar","nchar","varchar","char"):
        return "N'"+str(value).replace("'","''")+"'"
    if isinstance(value,float):
        if not math.isfinite(value):raise ValueError("Nonfinite source value")
        return format(value,".17g")
    return str(int(value))


def sql_type(column):
    kind=column["data_type"]
    if kind in ("nvarchar","nchar"):
        return kind+"("+str(column["max_length"]//2)+") COLLATE DATABASE_DEFAULT"
    if kind in ("varchar","char"):
        return kind+"("+str(column["max_length"])+") COLLATE DATABASE_DEFAULT"
    if kind=="float":return "FLOAT(53)"
    if kind in ("int","smallint","tinyint","bigint","real","bit"):return kind.upper()
    raise ValueError("Unsupported target type: "+kind)


def encode(value,column):
    if value is None:return None
    kind=column["data_type"]
    if kind=="smallint" and value>=32768:value-=65536
    if kind=="int" and value>=2147483648:value-=4294967296
    if kind in ("nvarchar","nchar","varchar","char"):
        limit=column["max_length"]//(2 if kind.startswith("n") else 1)
        if len(value)>limit:raise ValueError("Source string exceeds the target column")
    return value


def client_plan(snapshot,facts,manifest,identities=None):
    schema=load(snapshot/"schema.json");columns={(r["table_name"],r["column_name"]):r for r in schema}
    evidence={r["path"].lower():r["sha256"] for r in load(manifest)["files"]}
    changes=[];coverage=[];gaps=[]
    disputed={}
    if identities:
        audit=load(identities)
        for table,kind in (("TITEMCHART","item"),("TNPCCHART","npc")):
            disputed[table]={row["id"] for row in audit["domains"][kind]["records"] if row["status"] in ("name_mismatch","wiki_only")}
    def compare(name,table,key,source_file,mapping=None,ignore=(),filter_row=None):
        incoming=load(facts/(name+".json"));current={r[key]:r for r in load(snapshot/(table+".json"))}
        seen=collections.Counter(row.get(key) for row in incoming)
        fields=set();verified=0;changed=set()
        for row in incoming:
            if row.get(key) in disputed.get(table,set()):
                gaps.append({"domain":table,"key":row.get(key),"reason":"Public/client identity names require review"});continue
            if seen[row.get(key)]>1:
                gaps.append({"domain":table,"key":row.get(key),"reason":"Duplicate client rows require instance disambiguation"});continue
            if filter_row and not filter_row(row):continue
            if row[key] not in current:
                gaps.append({"domain":table,"key":row[key],"reason":"Official row absent; no unpublished defaults inferred"});continue
            verified+=1
            for raw,new in row.items():
                column=(mapping or {}).get(raw,raw)
                if raw in ignore or column==key or (table,column) not in columns:continue
                definition=columns[(table,column)]
                if definition["data_type"] in ("varchar","char"):
                    continue
                fields.add(column)
                before=current[row[key]][column]
                try:after=encode(new,definition)
                except ValueError as error:
                    gaps.append({"domain":table,"key":row[key],"field":column,"reason":str(error)});continue
                if equal(before,after):continue
                file=row.get("source_file",source_file)
                if file.lower() not in evidence:raise ValueError("Unpinned source file")
                changes.append({"table":table,"key_column":key,"key":row[key],"column":column,"before":before,"after":after,
                                "source":file,"source_sha256":evidence[file.lower()]})
                changed.add(row[key])
        coverage.append({"table":table,"source_rows":len(incoming),"verified_existing_rows":verified,"changed_rows":len(changed),
                         "verified_columns":sorted(fields),"unverified_columns":sorted({r["column_name"] for r in schema if r["table_name"]==table}-{key}-fields)})
    compare("items","TITEMCHART","wItemID","Tcd/TItem.tcd")
    compare("item_attributes","TITEMATTRCHART","wID","Tcd/TItemAttr.tcd",ignore=("client_speed_inc",))
    compare("formulas","TFORMULACHART","bID","Tcd/TFormula.tcd",{"dwInit":"dwinit"})
    compare("level_fees","TLEVELCHART","bLevel","Tcd/TLevel.tcd")
    compare("item_grades","TITEMGRADECHART","bLevel","Tcd/TItemGrade.tcd")
    # The decoded mount identifiers use the client naming; preserve explicit server names.
    mounts=load(facts/"mounts.json")
    for row in mounts:
        row["wMountID"]=row.pop("wID")
    (facts/"mounts-server-keys.json").write_text(json.dumps(mounts)+"\n",encoding="utf-8")
    compare("mounts-server-keys","TMOUNTCHART","wMountID","Tcd/TMount.tcd",
            {"wMonID":"wDefMonID","wSaddleMonID":"wUpgMonID"},ignore=("client_icon",))
    # Keep zero-coordinate placeholders and ambiguous duplicate instances out of world-position changes.
    npc_fields={"wID","bType","bCountryID","wItemID","wMapID","fPosX","fPosY","fPosZ","source_file"}
    npcs=load(facts/"npcs.json")
    npc_rows=[{k:v for k,v in row.items() if k in npc_fields} for row in npcs
              if row["fPosX"]!=0 or row["fPosY"]!=0 or row["fPosZ"]!=0]
    valid_maps={r["wMapID"] for r in load(snapshot/"TMAPCHART.json")} if "wMapID" in load(snapshot/"TMAPCHART.json")[0] else set()
    if valid_maps:npc_rows=[row for row in npc_rows if row["wMapID"] in valid_maps]
    (facts/"npcs-direct-fields.json").write_text(json.dumps(npc_rows,ensure_ascii=False)+"\n",encoding="utf-8")
    compare("npcs-direct-fields","TNPCCHART","wID","",ignore=("source_file",))
    # Client spawn files do not establish the server's full instance, AI and quest dependencies.
    # Keep those candidates reviewable, without moving live NPCs or changing their interaction type.
    deferred=[row for row in changes if row["table"]=="TNPCCHART"]
    changes=[row for row in changes if row["table"]!="TNPCCHART"]
    for row in coverage:row["applied"]=row["table"]!="TNPCCHART"
    return {"scope":"Decoded fields of the pinned local Gameforge client; web supplements are separate",
            "changes":changes,"deferred_npc_fields":deferred,"coverage":coverage,"gaps":gaps}


def migration(plan,schema,output):
    columns={(r["table_name"],r["column_name"]):r for r in schema}
    groups=collections.defaultdict(list)
    for change in plan["changes"]:groups[(change["table"],change["key_column"],change["column"])].append(change)
    lines=["-- Official client field normalization. Source facts and row changes are recorded in Gameforge/normalization/.",
           "USE [TGame_gsp];","GO","SET QUOTED_IDENTIFIER ON;","SET ANSI_NULLS ON;","SET NOCOUNT ON;","SET XACT_ABORT ON;","GO",
           "BEGIN TRANSACTION;","DECLARE @changed INT=0;"]
    for group_index,((table,key,column),rows) in enumerate(sorted(groups.items())):
        for identifier in (table,key,column):
            if not re.fullmatch("[A-Za-z0-9_]+",identifier):raise ValueError("Invalid SQL identifier")
        data_type=sql_type(columns[(table,column)]);key_type=sql_type(columns[(table,key)])
        kind=columns[(table,column)]["data_type"]
        temporary="#GfChange"+str(group_index)
        lines.append(f"CREATE TABLE {temporary} (Id {key_type} PRIMARY KEY,BeforeValue {data_type} NULL,AfterValue {data_type} NULL);")
        for offset in range(0,len(rows),500):
            values=[]
            for row in rows[offset:offset+500]:
                values.append("("+str(row["key"])+","+sql_value(row["before"],kind)+","+sql_value(row["after"],kind)+")")
            lines.append("INSERT "+temporary+" VALUES "+",\n".join(values)+";")
        def same(value):
            comparison=f"t.[{column}]=r.{value}"
            if kind in ("nvarchar","nchar","varchar","char"):
                comparison=f"CONVERT(VARBINARY(MAX),t.[{column}])=CONVERT(VARBINARY(MAX),r.{value})"
            elif kind in ("float","real"):
                # Charts are consumed as IEEE float32 by both the C++ and .NET clients/servers.
                comparison=f"CONVERT(REAL,t.[{column}])=CONVERT(REAL,r.{value})"
            return f"((t.[{column}] IS NOT NULL AND r.{value} IS NOT NULL AND {comparison}) OR (t.[{column}] IS NULL AND r.{value} IS NULL))"
        before,after=same("BeforeValue"),same("AfterValue")
        lines.append(f"IF EXISTS(SELECT 1 FROM {temporary} r LEFT JOIN dbo.[{table}] t ON t.[{key}]=r.Id WHERE t.[{key}] IS NULL OR NOT({before} OR {after})) THROW 51000,'Gameforge normalization baseline changed: {table}.{column}',1;")
        lines.append(f"UPDATE t SET [{column}]=r.AfterValue FROM dbo.[{table}] t JOIN {temporary} r ON t.[{key}]=r.Id WHERE NOT {after};")
        lines.append("SET @changed=@changed+@@ROWCOUNT;")
        lines.append(f"IF EXISTS(SELECT 1 FROM {temporary} r JOIN dbo.[{table}] t ON t.[{key}]=r.Id WHERE NOT {after}) THROW 51000,'Gameforge normalization postcondition failed: {table}.{column}',1;")
        lines.append("DROP TABLE "+temporary+";")
    lines.extend(["SELECT @changed AS NormalizedOfficialClientFields;","COMMIT TRANSACTION;","GO"])
    output.write_text("\n".join(lines)+"\n",encoding="utf-8")


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--snapshot",type=Path,required=True);parser.add_argument("--facts",type=Path,required=True)
    parser.add_argument("--manifest",type=Path,default=Path(__file__).resolve().parents[2]/"Gameforge/reference.json")
    parser.add_argument("--identities",type=Path,default=Path(__file__).resolve().parents[2]/"Gameforge/normalization/identity-audit.json")
    parser.add_argument("--plan",type=Path,required=True);parser.add_argument("--sql",type=Path,required=True)
    args=parser.parse_args();plan=client_plan(args.snapshot,args.facts,args.manifest,args.identities)
    args.plan.parent.mkdir(parents=True,exist_ok=True)
    args.plan.write_text(json.dumps(plan,ensure_ascii=False,indent=2)+"\n",encoding="utf-8")
    migration(plan,load(args.snapshot/"schema.json"),args.sql)
    print(json.dumps({"field_changes":len(plan["changes"]),"changed_rows":len({(r['table'],r['key']) for r in plan['changes']}),
                      "coverage":plan["coverage"],"gaps":len(plan["gaps"])},ensure_ascii=True,indent=2))


if __name__=="__main__":main()
