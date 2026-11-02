"""Repair quest 7906 navigation using its frozen routing/NPC evidence; no gameplay SQL writes."""
import argparse
import hashlib
import json
from pathlib import Path
from quest_navigation_repair import encoded,repair

ROOT=Path(__file__).resolve().parents[2]
PLAN=ROOT/'Gameforge/gor-training/ostrich-eggs-navigation.json'


def main():
    parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('--apply',action='store_true');parser.add_argument('--game',type=Path,default=ROOT/'Game');parser.add_argument('--plan',type=Path,default=PLAN);args=parser.parse_args()
    plan=json.loads(args.plan.read_text(encoding='utf8'));path=args.game/'TQNode.qpd';before=path.read_bytes();after,count=repair(before,plan)
    result=dict(path=str(path.resolve()),nodes_changed=count,before_sha256=hashlib.sha256(before).hexdigest(),after_sha256=hashlib.sha256(after).hexdigest(),restart_required=count>0,deployed=False)
    if args.apply and count:
        folder=ROOT/('.runtime/gor-egg-navigation-20261101' if int(plan['quest_id'])==7906 else f".runtime/gor-quest-navigation-{int(plan['quest_id'])}-20261101");folder.mkdir(exist_ok=True)
        backup=folder/('TQNode.before.'+result['before_sha256']+'.qpd')
        if not backup.exists():backup.write_bytes(before)
        temporary=path.with_suffix('.egg-navigation.tmp');temporary.write_bytes(after)
        if path.read_bytes()!=before:raise ValueError('Client navigation changed during repair')
        temporary.replace(path);result.update(deployed=True,backup=str(backup.resolve()))
        (folder/'deployment.json').write_text(json.dumps(result,indent=2)+'\n',encoding='utf8')
    print(json.dumps(result))


if __name__=='__main__':main()
