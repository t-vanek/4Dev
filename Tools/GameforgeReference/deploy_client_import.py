"""Deploy a verified additive client import with complete input checks, backups and rollback on write failure."""
import argparse
import datetime
import hashlib
import json
import os
from pathlib import Path

from client_import import write_atomic


def sha(data):return hashlib.sha256(data).hexdigest()


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--game',type=Path,required=True);parser.add_argument('--prepared',type=Path,required=True)
    parser.add_argument('--backup',type=Path,required=True);parser.add_argument('--data-validation',type=Path,required=True)
    parser.add_argument('--resource-validation',type=Path,required=True)
    args=parser.parse_args();game=args.game.resolve();prepared=args.prepared.resolve()
    data_checks=json.loads(args.data_validation.read_text(encoding='utf-8'));resource_checks=json.loads(args.resource_validation.read_text(encoding='utf-8'))
    if not data_checks.get('passed') or resource_checks['unresolved']:raise ValueError('Client import has not passed validation')
    report=json.loads((prepared/'gameforge-import.json').read_text(encoding='utf-8'));files=[]
    manifest_hash=sha((prepared/'gameforge-import.json').read_bytes())
    if data_checks['manifest_sha256']!=manifest_hash or resource_checks['manifestSha256']!=manifest_hash:
        raise ValueError('The prepared import changed since validation')
    if len({row['path'].casefold() for row in report['files']})!=len(report['files']):raise ValueError('Duplicate deployment path')
    for row in report['files']:
        target=(game/row['path']).resolve();source=(prepared/row['path']).resolve()
        if not target.is_relative_to(game) or not source.is_relative_to(prepared):raise ValueError('Deployment path escapes its root')
        before=target.read_bytes() if target.is_file() else None;after=source.read_bytes()
        if (sha(before) if before is not None else None)!=row['before_sha256']:raise ValueError('Current input changed: '+row['path'])
        if sha(after)!=row['after_sha256']:raise ValueError('Prepared content changed: '+row['path'])
        files.append((row,target,before,after))
    args.backup.mkdir(parents=True,exist_ok=False)
    for row,target,before,after in files:
        if before is None:continue
        backup=args.backup/row['path'];backup.parent.mkdir(parents=True,exist_ok=True);backup.write_bytes(before)
        if sha(backup.read_bytes())!=row['before_sha256']:raise ValueError('Client backup validation failed')
    report['data_validation']=data_checks;report['resource_validation']=resource_checks
    (args.backup/'before-import.json').write_text(json.dumps(report,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
    def priority(item):
        path=item[0]['path']
        # The new executable supports both old and extended charts. Replacing it first also catches a
        # locked/running client before any data changes are made.
        return 0 if path=='TClient.exe' else 1 if path.startswith(('Data/','Node/','Path/','Tmh/')) else 2 if path.startswith('Index/') else 3
    written=[]
    try:
        for row,target,before,after in sorted(files,key=priority):
            write_atomic(target,after);written.append((row,target,before))
            if sha(target.read_bytes())!=row['after_sha256']:raise ValueError('Deployment read-back failed')
    except Exception:
        for row,target,before in reversed(written):
            if sha(target.read_bytes())!=row['after_sha256']:raise ValueError('A deployed path changed externally; preserve it for review: '+row['path'])
            if before is None:target.unlink()
            else:write_atomic(target,before)
        raise
    report['applied_utc']=datetime.datetime.now(datetime.timezone.utc).isoformat();report['backup']=str(args.backup.resolve())
    (args.backup/'deployment.json').write_text(json.dumps(report,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
    print(json.dumps({'applied_files':len(written),'backup':str(args.backup),'all_output_hashes_verified':True}))


if __name__=='__main__':main()
