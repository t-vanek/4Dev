"""Deploy previously prepared numeric chart patches after verifying and backing up every current input."""
import argparse
import datetime
import hashlib
import json
import os
from pathlib import Path
import uuid


def digest(data):return hashlib.sha256(data).hexdigest()


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--game",type=Path,required=True);parser.add_argument("--prepared",type=Path,required=True)
    parser.add_argument("--backup",type=Path,required=True)
    args=parser.parse_args();manifest=json.loads((args.prepared/"manifest.json").read_text(encoding="utf-8"))
    root=args.game.resolve();incoming=args.prepared.resolve();files=[]
    for row in manifest["files"]:
        target=(root/row["path"]).resolve();source=(incoming/row["path"]).resolve()
        if not target.is_relative_to(root) or not source.is_relative_to(incoming):raise ValueError("Chart path escapes its directory")
        before,after=target.read_bytes(),source.read_bytes()
        if digest(before)!=row["before_sha256"] or digest(after)!=row["after_sha256"]:
            raise ValueError("A game or prepared input has changed since review: "+row["path"])
        files.append((row,target,before,after))
    args.backup.mkdir(parents=True,exist_ok=False)
    for row,target,before,after in files:
        backup=args.backup/row["path"];backup.parent.mkdir(parents=True,exist_ok=True);backup.write_bytes(before)
        if digest(backup.read_bytes())!=row["before_sha256"]:raise ValueError("Client backup verification failed")
    written=[]
    try:
        for row,target,before,after in files:
            if before==after:continue
            temporary=target.with_name(target.name+".gameforge-"+str(uuid.uuid4())+".tmp")
            try:
                temporary.write_bytes(after);os.replace(temporary,target)
            finally:
                if temporary.exists():temporary.unlink()
            written.append((target,before))
            if digest(target.read_bytes())!=row["after_sha256"]:raise ValueError("Client deployment verification failed")
    except Exception:
        for target,before in written:target.write_bytes(before)
        raise
    manifest["applied_utc"]=datetime.datetime.now(datetime.timezone.utc).isoformat()
    (args.backup/"manifest.json").write_text(json.dumps(manifest,ensure_ascii=False,indent=2)+"\n",encoding="utf-8")
    print(json.dumps({"changed_files":len(written),"backup":str(args.backup),"verified":True}))


if __name__=="__main__":main()
