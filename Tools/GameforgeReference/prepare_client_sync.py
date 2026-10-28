"""Prepare numeric chart patches while preserving every unrelated byte of the current game files."""
import argparse
import hashlib
import json
from pathlib import Path
import struct

from client_facts import ITEM_FIELDS, Reader, items


def load(path):return json.loads(path.read_text(encoding="utf-8-sig"))
def digest(data):return hashlib.sha256(data).hexdigest()


def item_offsets(data):
    reader=Reader(data);result={}
    for _ in range(reader.u16()):
        start=reader.p;ident=reader.u16();reader.take(4);reader.string()
        use=reader.p;reader.take(2);tail=reader.p;reader.take(101)
        result[ident]={"bType":(start+2,"B"),"bKind":(start+3,"B"),"wAttrID":(start+4,"H"),"wUseValue":(use,"H")}
        result[ident].update({name:(tail+offset,fmt) for name,(offset,fmt) in ITEM_FIELDS.items()})
    reader.done();return result


def quest_offsets(data):
    reader=Reader(data);result={}
    for _ in range(reader.i32()):reader.take(4);reader.string(True);reader.take(1)
    while reader.p<len(data):
        ident=reader.u32();kind=reader.u8();reader.take(4);main=reader.p;reader.take(1)
        for _ in range(8):reader.string(True)
        level=reader.p;reader.take(1);terms=[]
        for _ in range(reader.i32()):
            terms.append({"bTermType":reader.u8(),"dwTermID":reader.u32(),"bCount":reader.u8()})
            reader.string(True);reader.string(True)
        rewards=[]
        for _ in range(reader.i32()):
            offset=reader.p
            values=struct.unpack("<BIBBB",reader.take(8))
            rewards.append(dict(zip(("bRewardType","dwRewardID","bCount","bTakeMethod","bTakeData"),values),offset=offset))
        reader.take(8)
        if ident in result:raise ValueError("Duplicate game quest ID")
        result[ident]={"type":kind,"main":main,"level":level,"terms":terms,"rewards":rewards}
    reader.done();return result


def fixed_offsets(data,fmt,key,size):
    reader=Reader(data);result={}
    for _ in range(reader.u16()):
        offset=reader.p;row=struct.unpack("<"+fmt,reader.take(size));result[row[key]]=offset
    reader.done();return result


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--game",type=Path,required=True);parser.add_argument("--client-plan",type=Path,required=True)
    parser.add_argument("--quest-plan",type=Path,required=True);parser.add_argument("--snapshot",type=Path,required=True)
    parser.add_argument("--output",type=Path,required=True)
    args=parser.parse_args();args.output.mkdir(parents=True,exist_ok=True)
    client=load(args.client_plan);quest=load(args.quest_plan)
    paths={"item":"Tcd/TItem.tcd","level":"Tcd/TLevel.tcd","mount":"Tcd/TMount.tcd","quest":"TQuest.mpq"}
    before={key:(args.game/path).read_bytes() for key,path in paths.items()}
    after={key:bytearray(value) for key,value in before.items()};allowed={key:set() for key in paths};writes=[]
    offsets=item_offsets(before["item"])
    levels=fixed_offsets(before["level"],"BIII",0,13);mounts=fixed_offsets(before["mount"],"HHHH",0,8)
    quests=quest_offsets(before["quest"])
    sql_rewards={row["dwID"]:row for row in load(args.snapshot/"TQREWARDCHART.json")}
    def patch(label,offset,fmt,value,change):
        if value<0 and fmt in ("B","H","I"):value+=1<<(8*struct.calcsize(fmt))
        data=struct.pack("<"+fmt,value)
        allowed[label].update(range(offset,offset+len(data)))
        if after[label][offset:offset+len(data)]!=data:
            writes.append({"file":paths[label],"offset":offset,"bytes":len(data),"table":change["table"],
                           "key":change["key"],"column":change["column"],"value":value})
            after[label][offset:offset+len(data)]=data
    for change in client["changes"]:
        table,key,column=change["table"],change["key"],change["column"]
        if table=="TITEMCHART" and column!="szNAME":
            if key not in offsets:raise ValueError("A changed server item is missing from the game client: "+str(key))
            offset,fmt=offsets[key][column];patch("item",offset,fmt,change["after"],change)
        elif table=="TLEVELCHART":
            offset=levels[key]+{"dwMoney":1,"dwRegCost":5,"dwSearchCost":9}[column]
            patch("level",offset,"I",change["after"],change)
        elif table=="TMOUNTCHART":
            offset=mounts[key]+{"wDefMonID":2,"wUpgMonID":4}[column]
            patch("mount",offset,"H",change["after"],change)
    verified={row["quest_id"]:row for row in quest["verified_quests"]}
    for change in quest["changes"]:
        ident=change["quest_id"];row=quests.get(ident)
        if not row or row["type"]!=8:raise ValueError("The game client's mission type differs: "+str(ident))
        def objective(rows):return sorted((r["bTermType"],r["dwTermID"],r["bCount"]) for r in rows)
        if objective(row["terms"])!=objective(verified[ident]["terms"]):
            raise ValueError("The game client's objectives differ: "+str(ident))
        if change["table"]=="TQUESTCHART":
            offset=row[{"bMain":"main","bLevel":"level"}[change["column"]]]
            patch("quest",offset,"B",change["after"],change)
        else:
            reward=sql_rewards[change["key"]]
            matches=[r for r in row["rewards"] if all(r[k]==reward[k] for k in ("bRewardType","bCount","bTakeMethod","bTakeData"))]
            if len(matches)!=1:raise ValueError("The client reward identity is missing/ambiguous: "+str(ident))
            patch("quest",matches[0]["offset"]+1,"I",change["after"],change)
    manifest={"scope":"Only numeric fields corresponding to the prepared SQL plans; current text, visuals and other records retained byte-for-byte",
              "files":[],"writes":writes}
    for label,path in paths.items():
        original,result=before[label],bytes(after[label])
        if len(original)!=len(result) or any(a!=b and i not in allowed[label] for i,(a,b) in enumerate(zip(original,result))):
            raise ValueError("A byte outside the intended numeric fields changed")
        # Validate complete consumption again with the same readers used to build the offsets.
        if label=="item":items(result)
        elif label=="quest":quest_offsets(result)
        elif label=="mount":fixed_offsets(result,"HHHH",0,8)
        else:fixed_offsets(result,"BIII",0,13)
        target=args.output/path;target.parent.mkdir(parents=True,exist_ok=True);target.write_bytes(result)
        manifest["files"].append({"path":path,"bytes":len(result),"before_sha256":digest(original),"after_sha256":digest(result),
                                  "changed_bytes":sum(a!=b for a,b in zip(original,result))})
    (args.output/"manifest.json").write_text(json.dumps(manifest,ensure_ascii=False,indent=2)+"\n",encoding="utf-8")
    print(json.dumps({"numeric_field_writes":len(writes),"files":manifest["files"]}))


if __name__=="__main__":main()
