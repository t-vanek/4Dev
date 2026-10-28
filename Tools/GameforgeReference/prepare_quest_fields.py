"""Normalize existing mission fields only when client, website and SQL quest identities agree."""
import argparse
import collections
import hashlib
import json
from pathlib import Path
import sqlite3
import zlib

from catalogue import quest_facts
from identity_audit import normal
from normalization import migration


def load(path):return json.loads(path.read_text(encoding="utf-8-sig"))


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--snapshot",type=Path,required=True);parser.add_argument("--facts",type=Path,required=True)
    parser.add_argument("--public",type=Path,required=True);parser.add_argument("--cache",type=Path,required=True)
    parser.add_argument("--plan",type=Path,required=True);parser.add_argument("--sql",type=Path,required=True)
    args=parser.parse_args()
    quests={row["dwQuestID"]:row for row in load(args.facts/"quests.json")["quests"]}
    local={row["dwQuestID"]:row for row in load(args.snapshot/"TQUESTCHART.json")}
    public={row["id"]:row for row in load(args.public/"quest-catalogue.json")["records"]}
    evidence=next(row for row in load(args.facts/"evidence.json")["files"] if row["path"]=="TQuest.mpq")
    terms=collections.defaultdict(list);rewards=collections.defaultdict(list)
    for row in load(args.snapshot/"TQUESTTERMCHART.json"):terms[row["dwQuestID"]].append(row)
    for row in load(args.snapshot/"TQREWARDCHART.json"):rewards[row["dwQuestID"]].append(row)
    def objective(rows):return collections.Counter((row["bTermType"],row["dwTermID"],row["bCount"]) for row in rows)
    with sqlite3.connect(args.public/"quest-details.sqlite") as store:
        detail={ident:json.loads(zlib.decompress(blob)) for ident,blob in store.execute("SELECT id,facts FROM details WHERE kind='quest'")}
    if detail.keys()!=public.keys():raise ValueError("The public quest capture is incomplete")
    changes=[];verified=[];gaps=[]
    def change(table,key,row,column,value,qid):
        if row[column]==value:return
        changes.append({"table":table,"key_column":key,"key":row[key],"column":column,"before":row[column],"after":value,
                        "quest_id":qid,"source":"TQuest.mpq","source_sha256":evidence["sha256"],
                        "web_source":detail[qid]["url"],"web_sha256":detail[qid]["sha256"]})
    for qid,pub in sorted(public.items()):
        source=quests.get(qid);current=local.get(qid);raw=detail[qid]
        payload=(args.cache/(hashlib.sha256(raw["url"].encode()).hexdigest()+".html")).read_bytes()
        if hashlib.sha256(payload).hexdigest()!=raw["sha256"]:raise ValueError("Changed public quest evidence")
        web=quest_facts(payload.decode("utf-8"))
        reason=None
        if not source or normal(source["title"])!=normal(pub["name"]):reason="Client/public name mismatch"
        elif not current:reason="SQL quest missing"
        elif source["bType"]!=8 or current["bType"]!=8:reason="Not a matching active mission type"
        elif source["unknown_prefix"]!=current["dwParentID"]:reason="Parent relationship differs"
        elif current["bTriggerType"]!=3 or not web["giver_id"] or current["dwTriggerID"]!=web["giver_id"]:reason="NPC giver differs or is not published"
        elif objective(source["terms"])!=objective(terms[qid]):reason="Mission objectives differ"
        elif not (web["level_label"] or "").isdecimal() or int(web["level_label"])!=source["bLevel"]:reason="Public/client level label differs"
        if reason:
            gaps.append({"quest_id":qid,"reason":reason,"source":raw["url"]});continue
        verified.append({"quest_id":qid,"name":source["title"],"parent_id":current["dwParentID"],"giver_id":current["dwTriggerID"],
                         "terms":[{k:row[k] for k in ("bTermType","dwTermID","bCount")} for row in terms[qid]],
                         "public":web,"source":raw["url"],"sha256":raw["sha256"]})
        change("TQUESTCHART","dwQuestID",current,"bLevel",source["bLevel"],qid)
        change("TQUESTCHART","dwQuestID",current,"bMain",source["bMain"],qid)
        # Currency and XP are separately published and encoded in the original client.
        # Retain every server-only reward column; do not manufacture missing reward rows.
        for kind,label in ((1,"money"),(6,"experience")):
            incoming=[row for row in source["rewards"] if row["bRewardType"]==kind]
            existing=[row for row in rewards[qid] if row["bRewardType"]==kind]
            if not incoming and not existing:continue
            if len(incoming)!=1 or len(existing)!=1:
                gaps.append({"quest_id":qid,"reward_type":kind,"reason":"Missing or non-unique reward row"});continue
            wanted,old=incoming[0],existing[0]
            if wanted["dwRewardID"]!=web[label]:
                gaps.append({"quest_id":qid,"reward_type":kind,"reason":"Client/public reward values differ"});continue
            if any(wanted[key]!=old[key] for key in ("bCount","bTakeMethod","bTakeData")):
                gaps.append({"quest_id":qid,"reward_type":kind,"reason":"Reward method/count differs"});continue
            change("TQREWARDCHART","dwID",old,"dwRewardID",wanted["dwRewardID"],qid)
    plan={"scope":"Existing matching mission identities; original-client level/main flag and independently published gold/XP",
          "unverified":"Other quest graphs, activation, conditions and reward rows are retained; no missing rows are inferred",
          "verified_quests":verified,"changes":changes,"gaps":gaps}
    args.plan.write_text(json.dumps(plan,ensure_ascii=False,indent=2)+"\n",encoding="utf-8")
    migration(plan,load(args.snapshot/"schema.json"),args.sql)
    selected={row["quest_id"] for row in changes}
    check=[row for row in verified if row["quest_id"] in selected]
    guards=["CREATE TABLE #GfQuestIdentity(Id INT PRIMARY KEY,ParentId INT,GiverId INT);",
            "CREATE TABLE #GfQuestTerms(QuestId INT,TermType TINYINT,TermId INT,TermCount TINYINT);"]
    for start in range(0,len(check),500):
        values=check[start:start+500]
        guards.append("INSERT #GfQuestIdentity VALUES "+",\n".join(f"({r['quest_id']},{r['parent_id']},{r['giver_id']})" for r in values)+";")
    values=[f"({row['quest_id']},{term['bTermType']},{term['dwTermID']},{term['bCount']})" for row in check for term in row['terms']]
    for start in range(0,len(values),500):guards.append("INSERT #GfQuestTerms VALUES "+",\n".join(values[start:start+500])+";")
    guards.extend(["IF EXISTS(SELECT 1 FROM #GfQuestIdentity r LEFT JOIN dbo.TQUESTCHART q ON q.dwQuestID=r.Id WHERE q.dwQuestID IS NULL OR q.bType<>8 OR q.dwParentID<>r.ParentId OR q.bTriggerType<>3 OR q.dwTriggerID<>r.GiverId) THROW 51000,'Verified quest identity changed',1;",
        "IF EXISTS(SELECT QuestId,TermType,TermId,TermCount,COUNT(*) AS N FROM #GfQuestTerms GROUP BY QuestId,TermType,TermId,TermCount EXCEPT SELECT t.dwQuestID,t.bTermType,t.dwTermID,t.bCount,COUNT(*) FROM dbo.TQUESTTERMCHART t JOIN #GfQuestIdentity q ON q.Id=t.dwQuestID GROUP BY t.dwQuestID,t.bTermType,t.dwTermID,t.bCount) OR EXISTS(SELECT t.dwQuestID,t.bTermType,t.dwTermID,t.bCount,COUNT(*) FROM dbo.TQUESTTERMCHART t JOIN #GfQuestIdentity q ON q.Id=t.dwQuestID GROUP BY t.dwQuestID,t.bTermType,t.dwTermID,t.bCount EXCEPT SELECT QuestId,TermType,TermId,TermCount,COUNT(*) FROM #GfQuestTerms GROUP BY QuestId,TermType,TermId,TermCount) THROW 51000,'Verified quest objectives changed',1;",
        "DROP TABLE #GfQuestTerms;","DROP TABLE #GfQuestIdentity;"])
    script=args.sql.read_text(encoding="utf-8").replace("BEGIN TRANSACTION;","BEGIN TRANSACTION;\n"+"\n".join(guards),1)
    args.sql.write_text(script,encoding="utf-8")
    print(json.dumps({"public_quests":len(public),"verified_matching_missions":len(verified),"field_changes":len(changes),
                      "changed_quests":len(selected),"gaps":len(gaps)}))


if __name__=="__main__":main()
