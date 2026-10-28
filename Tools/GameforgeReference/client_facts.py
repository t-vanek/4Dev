"""Decode documented numeric charts from the pinned Gameforge installation, without modifying it."""
import argparse
import hashlib
import json
from pathlib import Path
import re
import struct
import sys

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "ClientCompare"))
from compare import Reader, table


ITEM_FIELDS = {
    "dwSlotID": (0,"I"), "dwClassID": (4,"I"), "bPrmSlotID": (8,"B"), "bSubSlotID": (9,"B"),
    "bLevel": (10,"B"), "bCanRepair": (11,"B"), "dwDuraMax": (12,"I"), "bRefineMax": (16,"B"),
    "fPrice": (17,"f"), "bMinRange": (25,"B"), "bMaxRange": (26,"B"), "bStack": (27,"B"),
    "bSlotCount": (28,"B"), "bCanGamble": (29,"B"), "bGambleProb": (30,"B"), "bDestroyProb": (31,"B"),
    "bCanGrade": (34,"B"), "bCanMagic": (35,"B"), "bCanRare": (36,"B"), "wDelayGroupID": (37,"H"),
    "dwDelay": (39,"I"), "bIsSell": (43,"B"), "bIsSpecial": (45,"B"), "wUseTime": (46,"H"),
    "bUseType": (48,"B"), "bCanWrap": (81,"B"), "dwCode": (82,"I"), "bCanColor": (86,"B")
}


def items(data):
    r=Reader(data); result=[]
    for _ in range(r.u16()):
        ident,kind_type,kind,attr=r.u16(),r.u8(),r.u8(),r.u16()
        name=r.string(); use=r.u16(); tail=r.take(101)
        row={"wItemID":ident,"bType":kind_type,"bKind":kind,"wAttrID":attr,"szNAME":name,"wUseValue":use}
        row.update({key:struct.unpack_from("<"+fmt,tail,offset)[0] for key,(offset,fmt) in ITEM_FIELDS.items()})
        result.append(row)
    r.done(); return result


def fixed(data,fmt,fields):
    r=Reader(data); result=[]
    for _ in range(r.u16()):
        result.append(dict(zip(fields,struct.unpack("<"+fmt,r.take(struct.calcsize("<"+fmt))))))
    r.done(); return result


def quests(data):
    r=Reader(data); classes=[]; result=[]
    for _ in range(r.i32()):
        classes.append({"id":r.u32(),"name":r.string(True),"main":r.u8()})
    while r.p<len(data):
        extra=r.u32(); ident=r.u32(); kind=r.u8(); cls=r.u32(); main=r.u8(); unknown=r.u8()
        texts=[r.string(True) for _ in range(8)]
        level=r.u8(); term_count=r.i32()
        if not 0<=term_count<1000: raise ValueError("Invalid quest term count")
        terms=[]
        for _ in range(term_count):
            terms.append({"bTermType":r.u8(),"dwTermID":r.u32(),"bCount":r.u8()})
            r.string(True);r.string(True)
        reward_count=r.i32()
        if not 0<=reward_count<1000: raise ValueError("Invalid quest reward count")
        rewards=[]
        for _ in range(reward_count):
            rewards.append({"bRewardType":r.u8(),"dwRewardID":r.u32(),"bCount":r.u8(),"bTakeMethod":r.u8(),"bTakeData":r.u8()})
        lower,upper=r.u32(),r.u32()
        result.append({"dwQuestID":ident,"bType":kind,"class_id":cls,"bMain":main,"bLevel":level,
                       "min_level":lower,"max_level":upper,"title":texts[0],"terms":terms,"rewards":rewards,
                       "unknown_prefix":extra,"unknown_flag":unknown})
    r.done();return {"classes":classes,"quests":result}


def npcs(data,map_id):
    r=Reader(data);result=[]
    for _ in range(r.u16()):
        ident,temp=r.u32(),r.u16();name,title=r.string(),r.string();tail=r.take(75)
        row={"wID":ident,"template_id":temp,"name":name,"title":title,"bType":tail[0],
             "class_index":tail[1],"level":tail[2],"bCountryID":tail[3],"wItemID":struct.unpack_from("<H",tail,28)[0],
             "max_hp":struct.unpack_from("<I",tail,30)[0],"wMapID":map_id,
             "fPosX":struct.unpack_from("<f",tail,38)[0],"fPosY":struct.unpack_from("<f",tail,42)[0],"fPosZ":struct.unpack_from("<f",tail,46)[0]}
        result.append(row)
    r.done();return result


def skill_requirements(data):
    # Original 144-byte layout: ID, CString, prev/item/def IDs, class mask, kind/positive, level/inc/max.
    # See the classic InitTSKILLTEMP reader retained in TChart.cpp. Modern Game uses the other 166-byte layout.
    r=Reader(data);result=[]
    for _ in range(r.u16()):
        ident=r.u16();name=r.string();tail=r.take(144)
        result.append({"wID":ident,"name":name,"dwClassID":struct.unpack_from('<I',tail,6)[0],
                       "bLevel":tail[12],"bNextLevel":tail[13],"bMaxLevel":tail[14]})
    r.done();return result


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source",type=Path,required=True)
    parser.add_argument("--manifest",type=Path,default=Path(__file__).resolve().parents[2]/"Gameforge/reference.json")
    parser.add_argument("--output",type=Path,required=True)
    args=parser.parse_args();args.output.mkdir(parents=True,exist_ok=True)
    manifest=json.loads(args.manifest.read_text(encoding="utf-8"));expected={row["path"].lower():row for row in manifest["files"]}
    evidence=[];gaps=[]
    def read(relative):
        data=(args.source/relative).read_bytes();digest=hashlib.sha256(data).hexdigest()
        if relative.lower() not in expected or expected[relative.lower()]["sha256"]!=digest:
            raise ValueError("Unpinned or changed client input: "+relative)
        evidence.append({"path":relative,"sha256":digest,"bytes":len(data)})
        return data
    def save(name,rows):
        (args.output/(name+".json")).write_text(json.dumps(rows,ensure_ascii=False,separators=(",",":"))+"\n",encoding="utf-8")
        print(name+": "+str(len(rows) if isinstance(rows,list) else len(rows.get("quests",[]))),flush=True)
    save("items",items(read("Tcd/TItem.tcd")))
    save("item_attributes",fixed(read("Tcd/TItemAttr.tcd"),"HBHHHHHHBB",
        ("wID","bGrade","wMinAP","wMaxAP","wDP","wMinMAP","wMaxMAP","wMDP","bBlockProb","client_speed_inc")))
    save("formulas",fixed(read("Tcd/TFormula.tcd"),"BIff",("bID","dwInit","fRateX","fRateY")))
    save("level_fees",fixed(read("Tcd/TLevel.tcd"),"BIII",("bLevel","dwMoney","dwRegCost","dwSearchCost")))
    save("item_grades",fixed(read("Tcd/TItemGrade.tcd"),"BB",("bLevel","bGrade")))
    save("mounts",fixed(read("Tcd/TMount.tcd"),"HHHH",("wID","wMonID","wSaddleMonID","client_icon")))
    save("quests",quests(read("TQuest.mpq")))
    for filename,label in (("TMon.tcd","monsters"),("TSkill.tcd","skills")):
        data=read("Tcd/"+filename)
        records,metadata=table(args.source/"Tcd"/filename)
        save(label,[{"id":int(row["id"]),"name":row["texts"][-1],"title":row["texts"][0] if len(row["texts"])>1 else "",
                     "record_sha256":row["sha256"]} for row in records])
        if filename=="TSkill.tcd":save("skill_requirements",skill_requirements(data))
    npc_rows=[]
    for path in sorted((args.source/"Tcd").glob("TNPC*.tcd")):
        match=re.fullmatch(r"TNPC([0-9a-fA-F]{8})\.tcd",path.name)
        if not match and path.name.lower()!="tnpcglobal.tcd":continue
        relative="Tcd/"+path.name
        try:
            found=npcs(read(relative),int(match[1],16)>>16 if match else None)
            for row in found:row["source_file"]=relative
            npc_rows.extend(found)
        except (ValueError,struct.error) as error:
            gaps.append({"file":relative,"reason":str(error)})
    save("npcs",npc_rows)
    read("Tcd/TNPCINFO.tcd")
    info,metadata=table(args.source/"Tcd/TNPCINFO.tcd")
    info_rows=[{"id":int(row["id"]),"name":row["texts"][0],"title":row["texts"][1],"record_sha256":row["sha256"]} for row in info]
    if {row["id"] for row in info_rows}!={row["wID"] for row in npc_rows}:
        raise ValueError("The independent TNPCINFO and NPC spawn chart ID sets differ")
    save("npc_info",info_rows)
    save("evidence",{"files":evidence,"gaps":gaps,"unmapped":"Fields not decoded here remain unverified; no schema guessed for unsupported files"})


if __name__=="__main__":main()
