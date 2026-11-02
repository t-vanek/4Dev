"""Lossless source capture and conservative extraction of all client chart inputs."""
import hashlib
import json
from pathlib import Path
import re
import struct

from client_facts import Reader, items, npcs, fixed
from client_records import FIXED, named, skills, quest_items
from compare import table, ui
from reconstruction_store import row, key


def read_stable(path):
    for _ in range(3):
        before=path.stat();data=path.read_bytes();after=path.stat()
        if (before.st_size,before.st_mtime_ns)==(after.st_size,after.st_mtime_ns) and len(data)==after.st_size:
            return data
    raise ValueError('Client input changed repeatedly during read: '+str(path))


def full_quests(data):
    r=Reader(data);classes=[];quests=[]
    count=r.i32()
    if not 0<=count<10000:raise ValueError('Invalid quest class count')
    for _ in range(count):
        start=r.p;ident=r.u32();name=r.string(True);main=r.u8()
        classes.append(dict(id=ident,name=name,main=main,offset=start,length=r.p-start))
    while r.p<len(data):
        start=r.p;parent=r.u32();ident=r.u32();kind=r.u8();cls=r.u32();main=r.u8();flag=r.u8()
        texts=[r.string(True) for _ in range(8)];level=r.u8();terms=[]
        count=r.i32()
        if not 0<=count<1000:raise ValueError('Invalid quest term count')
        for _ in range(count):
            terms.append(dict(type=r.u8(),id=r.u32(),count=r.u8(),texts=[r.string(True),r.string(True)]))
        count=r.i32()
        if not 0<=count<1000:raise ValueError('Invalid quest reward count')
        rewards=[dict(zip(('type','id','count','method','data'),struct.unpack('<BIBBB',r.take(8)))) for _ in range(count)]
        lower,upper=r.u32(),r.u32()
        quests.append(dict(id=ident,parent=parent,type=kind,quest_class=cls,main=main,unknown_flag=flag,
            texts=texts,title=texts[0],level=level,min_level=lower,max_level=upper,terms=terms,rewards=rewards,
            offset=start,length=r.p-start))
    r.done()
    if len({q['id'] for q in quests})!=len(quests):raise ValueError('Duplicate quest IDs')
    return classes,quests


def navigation(data):
    r=Reader(data);rows=[]
    for _ in range(r.u32()):
        quest,count=r.u32(),r.u32()
        if count>10000:raise ValueError('Too many quest nodes')
        for ordinal in range(count):
            pos=r.p
            values=struct.unpack('<BIIIIfff',r.take(29))
            rows.append(dict(quest_id=quest,ordinal=ordinal,offset=pos,length=29,
                **dict(zip(('type','term_id','monster_kind','npc_id','map_id','x','y','z'),values))))
    r.done();return rows


def resource_index(data,name):
    r=Reader(data);files,count=r.i32(),r.i32()
    if not 0<=files<100000 or not 0<=count<1000000:raise ValueError('Invalid resource index counts')
    paths=[r.string(True).replace('\\','/') for _ in range(files)]
    result=[]
    for ordinal in range(count):
        offset=r.p;ident,file_index,position=r.u32(),r.i32(),r.u32()
        if not 0<=file_index<len(paths):raise ValueError('Resource file index out of range')
        path=paths[file_index]
        if Path(path).is_absolute() or '..' in Path(path).parts or ':' in path:
            raise ValueError('Unsafe resource path')
        result.append(dict(id=ident,ordinal=ordinal,pack=path,file_index=file_index,index=name,
            **({'media_flags':position} if name.lower()=='tclientw.idx' else {'position':position}),
            offset=offset,length=12))
    r.done();return result


NUMERIC={
 'titemattr.tcd':('HBHHHHHHBB',('id','grade','min_ap','max_ap','dp','min_map','max_map','mdp','block_probability','client_speed_inc')),
 'tformula.tcd':('BIff',('id','init','rate_x','rate_y')),
 'tlevel.tcd':('BIII',('level','money','registration_cost','search_cost')),
 'titemgrade.tcd':('BB',('level','grade')),
 'tmount.tcd':('HHHH',('id','monster_id','saddle_monster_id','icon')),
}


def parse_source(path,data):
    name=path.name.lower()
    if name=='tquest.mpq':
        classes,quests=full_quests(data)
        return [('client.quest_class',str(q['id']),q) for q in classes]+[('client.quest',str(q['id']),q) for q in quests]
    if name=='tqnode.qpd':
        return [('client.quest_navigation',f'{q["quest_id"]}:{q["ordinal"]}',q) for q in navigation(data)]
    if path.suffix.lower()=='.idx':
        return [('client.resource',str(q['ordinal']),q) for q in resource_index(data,path.name)]
    if path.suffix.lower()=='.tif':
        records,meta=ui(path)
        return [('client.ui_frame',str(n),dict(q,parser=meta)) for n,q in enumerate(records)]
    if name=='titem.tcd':
        return [('client.item',str(q['wItemID']),q) for q in items(data)]
    if name=='tskill.tcd':
        records=skills(data,True)
        return [('client.skill',str(q.key),dict(id=q.key,texts=q.names,**q.extra)) for q in records]
    if name=='tquestitem.tcd':
        return [('client.quest_item',str(q.key),dict(id=q.key,raw_hex=q.raw.hex())) for q in quest_items(data,True)]
    if re.fullmatch(r'tnpc[0-9a-f]{8}\.tcd',name) or name=='tnpcglobal.tcd':
        map_id=int(name[4:12],16)>>16 if name!='tnpcglobal.tcd' else None
        return [('client.npc',str(n),q) for n,q in enumerate(npcs(data,map_id))]
    if name in NUMERIC:
        fmt,fields=NUMERIC[name]
        return [('client.numeric.'+path.stem.lower(),str(n),q) for n,q in enumerate(fixed(data,fmt,fields))]
    if name in FIXED:
        size,key_size,group,key_fn=FIXED[name]
        r=Reader(data);records=[]
        for ordinal in range(r.u16()):
            start=r.p;raw=r.take(size)
            ident=key_fn(raw) if key_fn else int.from_bytes(raw[:key_size],'little')
            records.append(('client.fixed.'+path.stem.lower(),str(ordinal),dict(id=ident,offset=start,length=size,raw_hex=raw.hex())))
        r.done();return records
    if path.suffix.lower()=='.tcd':
        records,meta=table(path)
        raw_records,layout=named(path)
        if len(records)!=len(raw_records):raise ValueError('Named chart reader disagreement')
        kind={'tmon.tcd':'client.monster','tportal.tcd':'client.portal','tregion.tcd':'client.region'}.get(name,'client.named.'+path.stem.lower())
        return [(kind,str(n),dict(id=raw.key,texts=raw.names,raw_hex=raw.raw.hex(),layout=layout)) for n,raw in enumerate(raw_records)]
    raise ValueError('No inspected semantic parser; raw source retained')


def client_rows(source,manifest_path,cache,known_sources=None,raw_sources=None):
    known_sources=set(known_sources or [])
    raw_sources=dict(raw_sources or {})
    parser_version=hashlib.sha256(Path(__file__).read_bytes()).hexdigest()
    manifest_data=manifest_path.read_bytes();manifest=json.loads(manifest_data.decode('utf-8-sig'))
    pins={r['path'].lower():r for r in manifest['files']}
    manifest_id=key('reference_manifest',hashlib.sha256(manifest_data).hexdigest())
    yield row('source.manifest',manifest_id,'Gameforge/reference.json',manifest,manifest_data)
    # The complete pinned inventory remains queryable, without pretending un-read packs were reverified.
    for pin in manifest['files']:
        yield row('client.inventory',manifest_id,pin['path'],dict(pin,current_content_reverified=False))
    candidates=[]
    for folder in ('Tcd','Index'):
        if (source/folder).is_dir():candidates.extend(p for p in (source/folder).iterdir() if p.is_file())
    for name in ('TQuest.mpq','TQNode.qpd','TClientCmd.tif','TClient.exe','4Story.txt','TClientMP.mpc'):
        if (source/name).is_file():candidates.append(source/name)
    for path in sorted(candidates):
        relative=path.relative_to(source).as_posix()
        if not path.resolve().is_relative_to(source.resolve()):raise ValueError('Source outside installation')
        data=read_stable(path);digest=hashlib.sha256(data).hexdigest();expected=pins.get(relative.lower(),{}).get('sha256')
        identity=key('client_source',relative.lower(),digest)
        if identity in known_sources:continue
        frozen=cache/digest/path.name
        frozen.parent.mkdir(parents=True,exist_ok=True)
        if not frozen.exists():frozen.write_bytes(data)
        elif hashlib.sha256(frozen.read_bytes()).hexdigest()!=digest:raise ValueError('Cached source hash mismatch: '+str(frozen))
        parsed=[];error=None
        try:parsed=parse_source(frozen,data)
        except (ValueError,struct.error,IndexError,UnicodeError) as exc:error=str(exc)
        status='matches_pinned' if expected==digest else 'changed_from_pin' if expected else 'not_in_pin'
        raw_key=raw_sources.get(identity)
        if raw_key is None:
            original=row('source.bytes',identity,relative,dict(path=relative,sha256=digest,bytes=len(data)),data)
            raw_key=original['key']
            yield original
        metadata=dict(path=relative,sha256=digest,bytes=len(data),expected_sha256=expected,reference_status=status,
            raw_record_key=raw_key,
            parser_version=parser_version,parsed_records=len(parsed),parser_status='parsed' if error is None else 'raw_preserved',parser_error=error)
        yield row('source.client',identity,relative,metadata)
        for kind,ident,record in parsed:
            yield row(kind,identity,ident,dict(record,reference_status=status,source_path=relative,source_sha256=digest))
        if error:
            yield row('gap.client_parser',identity,relative,dict(path=relative,reason=error,raw_source_preserved=True))
        # A source is skippable only after every preceding record was committed.
        yield row('import.client_complete',identity,relative,dict(parser_version=parser_version,parsed_records=len(parsed)))
