"""Strict, record-preserving edits to a single quest's native navigation group."""
import struct
from reconstruction_client import navigation


def encoded(node):
    return struct.pack('<BIIIIfff',*(node[k] for k in ['type','term_id','monster_kind','npc_id','map_id','x','y','z']))


def groups(data):
    navigation(data)  # Validate every count and the complete input before editing anything.
    result=[];offset=4
    for _ in range(struct.unpack_from('<I',data)[0]):
        ident,count=struct.unpack_from('<II',data,offset);end=offset+8+29*count
        result.append((ident,offset,end,bytes(data[offset:end])));offset=end
    return result


def planned_nodes(plan):
    if 'before_nodes' in plan:return plan['before_nodes'],plan['after_nodes']
    return [c['before'] for c in plan['changes']],[c['after'] for c in plan['changes']]


def repair(data,plan):
    quest_id=int(plan['quest_id']);all_groups=groups(data);selected=[g for g in all_groups if g[0]==quest_id]
    if len(selected)!=1:raise ValueError('Quest navigation group missing or duplicated')
    before,after=planned_nodes(plan)
    if not before or not after:raise ValueError('Empty navigation repair scope')
    packed=lambda rows:struct.pack('<II',quest_id,len(rows))+b''.join(encoded(n) for n in rows)
    original=packed(before);replacement=packed(after);_,start,end,current=selected[0]
    if current==replacement:return bytes(data),0
    if current!=original:raise ValueError('Quest navigation baseline differs')
    result=bytes(data[:start])+replacement+bytes(data[end:]);checked=groups(result)
    if [g[3] for g in checked if g[0]==quest_id]!=[replacement]:raise ValueError('Navigation postcondition failed')
    if [g[3] for g in checked if g[0]!=quest_id]!=[g[3] for g in all_groups if g[0]!=quest_id]:raise ValueError('Unrelated navigation group changed')
    return result,max(len(before),len(after))
