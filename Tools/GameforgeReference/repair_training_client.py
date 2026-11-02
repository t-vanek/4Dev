"""Add native-client definitions for the server's isolated Gor routing aliases."""
import argparse
import hashlib
import json
from pathlib import Path
import struct

from client_records import quests

ROOT=Path(__file__).resolve().parents[2]


def expected_terms(source):
    return [(t['bTermType'],t['dwTermID'],t['bCount']) for t in source['terms']]


def validate(data,plan):
    _,records,_=quests(data,False);index={r.key:r for r in records}
    for ident,alias in plan['routing_aliases'].items():
        source=plan['source_quests'][str(alias['source_id'])];record=index.get(int(ident))
        if record is None:raise ValueError('Client cannot resolve advertised routing action '+ident)
        if record.extra['type']!=9 or record.extra['terms']!=expected_terms(source):raise ValueError('Client routing definition differs: '+ident)
    return index


def repaired_bytes(data,plan):
    _,records,_=quests(data,False);index={r.key:r for r in records};added=[]
    for ident,alias in plan['routing_aliases'].items():
        ident=int(ident)
        if ident in index:continue
        source=plan['source_quests'][str(alias['source_id'])];original=index[alias['source_id']]
        if original.extra['terms']!=expected_terms(source):raise ValueError('Source routing item differs')
        if source['bType']!=9:raise ValueError('Alias source is not routing')
        # Native quest records start with DWORD ID, BYTE type (retail parent/flag are absent).
        # The inherited third action was disabled; only the new alias receives its evidenced routing type.
        raw=struct.pack('<I',ident)+bytes([9])+original.raw[5:]
        added.append(raw)
    result=data+b''.join(added)
    validated=validate(result,plan)
    for record in records:
        if validated[record.key].raw!=record.raw:raise ValueError('Existing client record changed')
    return result,len(added)


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--apply',action='store_true')
    parser.add_argument('--game',type=Path,default=ROOT/'Game')
    args=parser.parse_args();path=args.game/'TQuest.mpq';plan=json.loads((ROOT/'Gameforge/gor-training/plan.json').read_text(encoding='utf8'));data=path.read_bytes()
    result,count=repaired_bytes(data,plan)
    metadata=dict(path=str(path),aliases_added=count,before_sha256=hashlib.sha256(data).hexdigest(),after_sha256=hashlib.sha256(result).hexdigest(),existing_records_preserved=True,client_restart_required=count>0)
    if args.apply and count:
        work=ROOT/'.runtime/gor-sword-dialog-20261101';work.mkdir(exist_ok=True)
        backup=work/('TQuest.before.'+metadata['before_sha256']+'.mpq')
        if not backup.exists():backup.write_bytes(data)
        temp=path.with_name('TQuest.mpq.training.tmp');temp.write_bytes(result)
        if path.read_bytes()!=data:raise ValueError('Client quest data changed during preparation')
        temp.replace(path);validate(path.read_bytes(),plan)
        (work/'client-repair.json').write_text(json.dumps(metadata,indent=2)+'\n',encoding='utf8')
    print(json.dumps(metadata))


if __name__=='__main__':main()
