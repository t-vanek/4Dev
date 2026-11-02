"""Freeze the Marka placement defect and its original-server packet evidence."""
import hashlib
import json
from pathlib import Path

from reconstruction_client import navigation
from sql_snapshot import sql

ROOT = Path(__file__).resolve().parents[2]
PROOF = ROOT / 'Gameforge/gor-training/marka-spawn.json'


def main():
    if PROOF.exists():
        raise ValueError('Keep frozen evidence; do not overwrite the before-state')
    observed = sql("SELECT RecordKey,SourceKey,Payload FROM Evidence WHERE Kind='event.monster_appearance' AND JSON_VALUE(Payload,'$.template_id')='800' ORDER BY EntityKey FOR JSON PATH;", 'FourDevReference')
    observations = []
    for row in observed:
        event = json.loads(row['Payload'])
        entity = f"{event['stream']}:{event['direction']}:{event['sequence']}"
        packets = sql(f"SELECT Payload FROM Evidence WHERE Kind='packet' AND SourceKey='{row['SourceKey']}' AND EntityKey='{entity}' FOR JSON PATH;", 'FourDevReference')
        assert len(packets) == 1
        observations.append(dict(record_key=row['RecordKey'], source_key=row['SourceKey'], decoded=event, packet=json.loads(packets[0]['Payload'])))
    assert len(observations) >= 2
    node_path = Path('C:/Program Files (x86)/4Story/TQNode.qpd')
    nodes = [r for r in navigation(node_path.read_bytes()) if r['quest_id'] == 11027 and r['monster_kind'] == 800]
    assert len(nodes) == 1
    position = {k: nodes[0][k] for k in ('x', 'y', 'z')}
    for o in observations:
        assert o['decoded']['instance_id'] >> 16 == 5024
        assert {k: o['decoded'][k] for k in position} == position
    before = sql('SELECT * FROM TMONSPAWNCHART WHERE wID=5024 FOR JSON PATH;')
    assert len(before) == 1
    after = dict(before[0], **{'fPos' + k.upper(): v for k, v in position.items()})
    proof = dict(schema=1, status='prepared', quest_id=11027, monster_id=800, spawn_id=5024,
        scope='Only fPosX/fPosY/fPosZ of existing spawn 5024. No monster stats, population, probabilities, delays or quest rules changed.',
        original_navigation=dict(path=str(node_path), sha256=hashlib.sha256(node_path.read_bytes()).hexdigest(), node=nodes[0]),
        observations=observations, spawn_before=before[0], spawn_after=after,
        map_monsters=sql('SELECT * FROM TMAPMONCHART WHERE wSpawnID=5024 FOR JSON PATH;'),
        history_review=dict(files=['Servers/TMapSvr.Net/TMap.Server/Map/MapService.Spawns.cs'],
            inspected=['git log', 'git show 949d26c', 'git blame 105-178', 'MonsterSpawnTests', 'QuestSpawnTests', 'QuestRegenTests'],
            finding='Existing C# correctly uses chart placement, scatter and attributes; preserve the port and its earlier fixes. Reproduced inherited SQL placement differs from two original appearances and pinned hunt navigation.'),
        limitations=['Appearance packets alone do not prove a complete spawn origin/roam/respawn rule. Placement uses agreement of two full-HP appearances, the original instance spawn ID and the pinned hunt marker.',
            'Inherited spawn radius, count, event, probability and delay remain provisional; unchanged by this repair.',
            'Captured Marka HP is 711; inherited local HP is 1186. Combat-stat reconciliation is outside this placement repair.',
            'Local real-client kill/turn-in/relog remains unverified.'],
        validation=dict(real_client_verified=False, full_gameforge_parity_verified=False))
    PROOF.write_text(json.dumps(proof, ensure_ascii=False, indent=2) + '\n', encoding='utf8')
    print(json.dumps(dict(observations=len(observations), before=before[0], after=after)))


if __name__ == '__main__':
    main()
