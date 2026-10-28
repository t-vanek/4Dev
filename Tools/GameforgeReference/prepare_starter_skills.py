"""Remove starter skills whose independently decoded original-client requirements exceed level one."""
import argparse
import collections
import json
from pathlib import Path


def load(path):return json.loads(path.read_text(encoding="utf-8-sig"))


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--snapshot",type=Path,required=True);parser.add_argument("--facts",type=Path,required=True)
    parser.add_argument("--plan",type=Path,required=True);parser.add_argument("--sql",type=Path,required=True)
    args=parser.parse_args();skills={r["wID"]:r for r in load(args.facts/"skill_requirements.json")}
    server={r["wID"]:r for r in load(args.snapshot/"TSKILLCHART.json")}
    starting=load(args.snapshot/"TSTARTSKILL.json");hotkeys=load(args.snapshot/"TSTARTHOTKEY.json")
    removed=[]
    for row in starting:
        source=skills.get(row["wSkillID"]);current=server.get(row["wSkillID"])
        if not source or not current or source["bLevel"]!=current["bLevel"] or source["dwClassID"]!=current["dwClassID"]:
            raise ValueError("Starter skill does not have matching original/server level and class requirements")
        if not source["dwClassID"] & (1<<row["bClassID"]):raise ValueError("Starter skill is assigned to the wrong class")
        if source["bLevel"]>1:
            removed.append({**row,"name":source["name"],"required_level":source["bLevel"],"class_mask":source["dwClassID"]})
    invalid={(r["bClassID"],r["wSkillID"]) for r in removed};cleared=[]
    for row in hotkeys:
        for slot in range(1,13):
            if row[f"bType{slot}"]==1 and (row["bClassID"],row[f"wID{slot}"]) in invalid:
                cleared.append({"class":row["bClassID"],"page":row["bInvenID"],"slot":slot,"skill_id":row[f"wID{slot}"]})
    source=next(r for r in load(args.facts/"evidence.json")["files"] if r["path"]=="Tcd/TSkill.tcd")
    plan={"scope":"Remove only starter skills with original-client required level > 1; clear only their starter hotkey entries",
          "source":source,"removed_starter_rows":removed,"cleared_hotkeys":cleared,
          "remaining_per_class":dict(collections.Counter(r["bClassID"] for r in starting if (r["bClassID"],r["wSkillID"]) not in invalid)),
          "unverified":"The complete official starter list is not published. Existing character skills/hotkeys are retained."}
    args.plan.write_text(json.dumps(plan,ensure_ascii=False,indent=2)+"\n",encoding="utf-8")
    lines=["-- Remove skills above the level-one requirement from new-character profiles only.",
           "-- Original TSkill IDs/requirements and each deletion are in Gameforge/normalization/starter-skills.json.",
           "USE [TGame_gsp];","GO","SET QUOTED_IDENTIFIER ON;","SET ANSI_NULLS ON;","SET NOCOUNT ON;","SET XACT_ABORT ON;","GO",
           "BEGIN TRANSACTION;","CREATE TABLE #GfStartSkill(ClassId TINYINT,SkillId SMALLINT,OldLevel TINYINT,RequiredLevel TINYINT,ClassMask INT,PRIMARY KEY(ClassId,SkillId));",
           "INSERT #GfStartSkill VALUES "+",\n".join(f"({r['bClassID']},{r['wSkillID']},{r['bLevel']},{r['required_level']},{r['class_mask']})" for r in removed)+";",
           "IF EXISTS(SELECT 1 FROM #GfStartSkill r LEFT JOIN dbo.TSKILLCHART s ON s.wID=r.SkillId WHERE s.wID IS NULL OR s.bLevel<>r.RequiredLevel OR s.dwClassID<>r.ClassMask) THROW 51000,'Starter skill requirements changed',1;",
           "IF EXISTS(SELECT 1 FROM #GfStartSkill r JOIN dbo.TSTARTSKILL s ON s.bClassID=r.ClassId AND s.wSkillID=r.SkillId WHERE s.bLevel<>r.OldLevel) THROW 51000,'Starter skill row changed',1;",
           "DELETE s FROM dbo.TSTARTSKILL s JOIN #GfStartSkill r ON r.ClassId=s.bClassID AND r.SkillId=s.wSkillID;",
           "DECLARE @removed INT=@@ROWCOUNT;",
           "CREATE TABLE #GfStartHotkey(ClassId TINYINT,PageId TINYINT,SlotId TINYINT,SkillId SMALLINT,PRIMARY KEY(ClassId,PageId,SlotId));",
           "INSERT #GfStartHotkey VALUES "+",\n".join(f"({r['class']},{r['page']},{r['slot']},{r['skill_id']})" for r in cleared)+";"]
    for slot in range(1,13):
        lines.extend([f"IF EXISTS(SELECT 1 FROM #GfStartHotkey r LEFT JOIN dbo.TSTARTHOTKEY h ON h.bClassID=r.ClassId AND h.bInvenID=r.PageId WHERE r.SlotId={slot} AND (h.bClassID IS NULL OR NOT((h.bType{slot}=1 AND h.wID{slot}=r.SkillId) OR (h.bType{slot}=0 AND h.wID{slot}=0)))) THROW 51000,'Starter hotkey baseline changed',1;",
                      f"UPDATE h SET bType{slot}=0,wID{slot}=0 FROM dbo.TSTARTHOTKEY h JOIN #GfStartHotkey r ON r.ClassId=h.bClassID AND r.PageId=h.bInvenID WHERE r.SlotId={slot};"])
    lines.extend(["IF EXISTS(SELECT 1 FROM dbo.TSTARTSKILL s JOIN #GfStartSkill r ON r.ClassId=s.bClassID AND r.SkillId=s.wSkillID) THROW 51000,'High-level starter skill remains',1;",
                  "SELECT @removed AS RemovedHighLevelStarterSkills;","DROP TABLE #GfStartHotkey;","DROP TABLE #GfStartSkill;","COMMIT TRANSACTION;","GO"])
    args.sql.write_text("\n".join(lines)+"\n",encoding="utf-8")
    print(json.dumps({"removed_starter_skills":len(removed),"cleared_hotkeys":len(cleared),"remaining_per_class":plan["remaining_per_class"]}))


if __name__=="__main__":main()
