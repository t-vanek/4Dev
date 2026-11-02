"""Additional inspected original-client message layouts; no inferred gameplay formulas."""
import struct

BASE=0x20c9


def semantic_world(ident,body,direction):
    if direction!='s2c':return None
    if ident==BASE+0x21:
        # Original client's 004158a0: its final DWORD before the effect count is
        # absent in the local legacy protocol. Preserve it without assigning meaning.
        if len(body)<88:raise ValueError('Truncated original defend result')
        count=body[87]
        if len(body)!=88+5*count:raise ValueError('Invalid original defend effect count')
        attacker,target,attacker_type,target_type,host,host_type=struct.unpack_from('<IIBBIB',body)
        crit,hit,attack_level,level,pmin,pmax,mmin,mmax=struct.unpack_from('<BBHBIIII',body,28)
        skill,skill_level=struct.unpack_from('<HB',body,53)
        return dict(kind='combat_result',attacker_id=attacker,target_id=target,attacker_type=attacker_type,
            target_type=target_type,host_id=host,host_type=host_type,critical_probability=crit,hit_type=hit,
            attack_level=attack_level,attacker_level=level,physical_min=pmin,physical_max=pmax,
            magic_min=mmin,magic_max=mmax,skill_id=skill,skill_level=skill_level,
            original_extension_hex=body[83:87].hex(),
            effects=[dict(exec=body[i],value=struct.unpack_from('<I',body,i+1)[0]) for i in range(88,len(body),5)],
            handler='004158a0',evidence_scope='Observed effective attack snapshot; not base/weapon decomposition or a damage formula')
    if ident==BASE+0x11 and len(body)>=35:
        instance,template,level,max_hp,hp,max_mp,mp,x,y,z=struct.unpack_from('<IHBIIIIfff',body)
        return dict(kind='monster_appearance',instance_id=instance,template_id=template,level=level,
            max_hp=max_hp,hp=hp,max_mp=max_mp,mp=mp,x=x,y=y,z=z,
            semantic_scope='inspected_prefix',uninterpreted_tail_bytes=len(body)-35,
            handler='004129b0',position_scope='Observed appearance, not a proven spawn origin or respawn rule')
    if ident==BASE+0x13 and len(body)==22:
        instance,action,x,y,z,argument,flag=struct.unpack('<IBfffIB',body)
        return dict(kind='monster_motion',instance_id=instance,action=action,x=x,y=y,z=z,
            action_argument=argument,flag=flag,handler='0040ff90')
    if ident==BASE+0x12 and len(body)==5:
        instance,exit_map=struct.unpack('<IB',body)
        return dict(kind='monster_leave',instance_id=instance,exit_map=exit_map)
    if ident==BASE+0xa and len(body)==27:
        actor,x,y,z,yaw,pitch,flag1,flag2,action,speed=struct.unpack('<IfffHHBBBf',body)
        return dict(kind='actor_motion',actor_id=actor,x=x,y=y,z=z,yaw_code=yaw,pitch_code=pitch,
            flag1=flag1,flag2=flag2,action=action,speed=speed,handler='00411ed0')
    if ident==BASE+0x22 and len(body)==21:
        actor,kind,max_hp,hp,max_mp,mp=struct.unpack('<IBIIII',body)
        return dict(kind='hp_mp',actor_id=actor,actor_type=kind,max_hp=max_hp,hp=hp,max_mp=max_mp,mp=mp,handler='00416e90')
    if ident==BASE+0x25 and len(body)==5:
        actor,kind=struct.unpack('<IB',body)
        return dict(kind='death',actor_id=actor,actor_type=kind)
    if ident==BASE+0x83 and len(body)>=3:
        npc,npc_type=struct.unpack_from('<HB',body)
        return dict(kind='npc_catalogue',npc_id=npc,npc_type=npc_type,
            semantic_scope='inspected_prefix',uninterpreted_tail_bytes=len(body)-3,handler='0041c350')
    if ident==BASE+0x89 and len(body)>=19:
        from decode_capture import parse_item
        result,update,monster,gold,silver,copper,count=struct.unpack_from('<BBIIIIB',body)
        pos=19;items=[]
        for _ in range(count):
            if pos+35>len(body):raise ValueError('Truncated loot item')
            size=35+3*body[pos+34]
            items.append(parse_item(body[pos:pos+size],inventory=False));pos+=size
        if pos!=len(body):raise ValueError('Unexpected loot-list trailing bytes')
        return dict(kind='loot_list',result=result,update=update,monster_id=monster,gold=gold,silver=silver,
            copper=copper,copper_total=(gold*1000+silver)*1000+copper,items=items,handler='0041d810',
            evidence_scope='One offered loot outcome; not a drop-probability or money-distribution formula')
    return None
