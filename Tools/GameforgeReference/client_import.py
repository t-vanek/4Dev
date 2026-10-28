"""Prepare an additive import of missing original-client records and indexed resources, preserving current content."""
import argparse
import collections
import datetime
import hashlib
import json
import mmap
import os
from pathlib import Path
import re
import shutil
import struct
import uuid
import zlib

from client_records import FIXED,Record,fixed,info,merge,named,quest_items,quest_positions,quests,serialize,skills
from client_facts import Reader
from compare import SCHEMAS


def sha(data):return hashlib.sha256(data).hexdigest()
def write_atomic(path,data):
    path.parent.mkdir(parents=True,exist_ok=True)
    temporary=path.with_name(path.name+'.'+str(uuid.uuid4())+'.tmp')
    temporary.write_bytes(data);os.replace(temporary,path)


def index(data):
    reader=Reader(data,'latin1');count,total=reader.i32(),reader.i32()
    if not 0<=count<100000 or not 0<=total<1000000:raise ValueError("Invalid IDX counts")
    files=[reader.string(True).replace('\\','/') for _ in range(count)]
    entries=[struct.unpack('<IiI',reader.take(12)) for _ in range(total)]
    reader.done()
    if any(not 0<=row[1]<count for row in entries):raise ValueError("Invalid IDX file index")
    if len({row[0] for row in entries})!=len(entries):raise ValueError("Duplicate indexed resource ID")
    return files,entries


def index_bytes(files,entries):
    return struct.pack('<ii',len(files),len(entries))+b''.join(struct.pack('<i',len(raw))+raw for raw in
        (value.replace('/','\\').encode('latin1') for value in files))+b''.join(struct.pack('<IiI',*entry) for entry in entries)


class Preparation:
    def __init__(self,source,game,output,manifest):
        self.source,self.game,self.output=source.resolve(),game.resolve(),output.resolve()
        if self.output.exists():raise ValueError("The preparation output must be a new directory")
        if self.output.is_relative_to(self.source) or self.output.is_relative_to(self.game):raise ValueError("Output must be outside both clients")
        self.manifest=json.loads(manifest.read_text(encoding='utf-8'))
        self.pinned={row['path'].casefold():row for row in self.manifest['files']};self.checked=set()
        self.report={'source':str(self.source),'game':str(self.game),'prepared_utc':datetime.datetime.now(datetime.timezone.utc).isoformat(),
                     'policy':'Add absent record IDs only; existing records stay byte-identical or receive a reversible zero-extension for 9-slot quest previews',
                     'files':[],'tables':[],'resources':[],'copied_files':[],'deferred':[],'extra_quest_metadata':[]}

    def source_bytes(self,relative):
        path=(self.source/relative).resolve()
        if not path.is_relative_to(self.source):raise ValueError("Source path escapes the selected installation")
        key=path.relative_to(self.source).as_posix().casefold();data=path.read_bytes()
        if key not in self.pinned or sha(data)!=self.pinned[key]['sha256']:raise ValueError("Unpinned or changed source: "+relative)
        self.checked.add(key);return data

    def clone(self):
        self.output.mkdir(parents=True)
        # Static shared assets are linked for a cheap full staging install. Every changed file is replaced
        # atomically, never edited through the link. Root/config files are copied to isolate runtime writes.
        for source in self.game.rglob('*'):
            if not source.is_file():continue
            relative=source.relative_to(self.game);target=self.output/relative;target.parent.mkdir(parents=True,exist_ok=True)
            if len(relative.parts)>1 and relative.parts[0].casefold() in ('data','index','tcd','tmh','node','path'):
                try:os.link(source,target)
                except OSError:shutil.copy2(source,target)
            else:shutil.copy2(source,target)

    def save(self,relative,data,kind):
        target=self.game/relative;before=target.read_bytes() if target.is_file() else None
        if before==data:return
        write_atomic(self.output/relative,data)
        self.report['files']=[row for row in self.report['files'] if row['path'].casefold()!=relative.casefold()]
        self.report['files'].append({'path':relative,'kind':kind,'before_sha256':sha(before) if before is not None else None,
                                     'after_sha256':sha(data),'bytes':len(data)})

    def table_result(self,relative,before,source,after,added,layout):
        # Existing canonical records are retained in order, byte-for-byte; duplicate source groups are atomic.
        if [(r.key,r.raw) for r in after[:len(before)]]!=[(r.key,r.raw) for r in before]:raise ValueError("An existing chart record changed")
        self.report['tables'].append({'path':relative,'before_records':len(before),'source_records':len(source),
                                     'after_records':len(after),'added_records':len(added),'added_ids':[r.key for r in added],
                                     'layout':layout,'existing_records_preserved':True,
                                     'added_record_sha256':[{ 'id':r.key,'sha256':sha(r.raw)} for r in added]})

    def named_tables(self):
        special={'tskill.tcd','tquest.mpq'}
        for path in sorted((self.source/'Tcd').glob('*.tcd')):
            name=path.name;lower=name.lower();relative='Tcd/'+name
            eligible=lower in SCHEMAS or lower in ('tstring.tcd','tnpcglobal.tcd') or re.fullmatch(r'tnpc[0-9a-f]{8}\.tcd',lower)
            if not eligible or lower in special:continue
            data=self.source_bytes(relative)
            if not data:
                self.report['deferred'].append({'path':relative,'reason':'Source file is empty'});continue
            incoming,layout=named(path)
            before,_=named(self.game/relative) if (self.game/relative).exists() else ([],layout)
            after,added=merge(before,incoming)
            self.save(relative,serialize(after),'chart');self.table_result(relative,before,incoming,after,added,layout)

    def fixed_tables(self,new_skills):
        for filename,definition in FIXED.items():
            path=next((p for p in (self.source/'Tcd').iterdir() if p.name.casefold()==filename),None)
            if path is None:continue
            relative='Tcd/'+path.name;source=self.source_bytes(relative)
            incoming=fixed(source,*definition[:3],key=definition[3]);before=fixed((self.game/relative).read_bytes(),*definition[:3],key=definition[3])
            selected=incoming
            if filename in ('tskillfunction.tcd','tskillpoint.tcd','tarrowdir.tcd'):
                selected=[r for r in incoming if r.key in new_skills]
            after,added=merge(before,selected);self.save(relative,serialize(after),'chart')
            self.table_result(relative,before,incoming,after,added,'fixed:'+str(definition[0]))

    def skills(self):
        relative='Tcd/TSkill.tcd';incoming=skills(self.source_bytes(relative),True);before=skills((self.game/relative).read_bytes(),False)
        after,added=merge(before,incoming);data=serialize(after);decoded=skills(data,False)
        if [(r.key,r.raw) for r in decoded]!=[(r.key,r.raw) for r in after]:raise ValueError("Converted skills did not read back")
        self.save(relative,data,'chart');self.table_result(relative,before,incoming,after,added,'skill:modern166; converted from original144')
        return {row.key for row in added}

    def quests(self):
        relative='TQuest.mpq';classes,incoming,maximum=quests(self.source_bytes(relative),True)
        before_classes,before,_=quests((self.game/relative).read_bytes(),False)
        after_classes,new_classes=merge(before_classes,classes);after,added=merge(before,incoming)
        data=serialize(after_classes,False)+b''.join(row.raw for row in after)
        checked_classes,checked,_=quests(data,False)
        if [r.raw for r in checked]!=[r.raw for r in after] or [r.raw for r in checked_classes]!=[r.raw for r in after_classes]:
            raise ValueError("Converted quests did not read back")
        self.save(relative,data,'quest');self.table_result(relative,before,incoming,after,added,'quest:legacy; original parent/flag retained in metadata')
        self.report['quest_classes_added']=[row.key for row in new_classes];self.report['maximum_source_quest_text_bytes']=maximum
        self.report['extra_quest_metadata']=[{'id':row.key,'original_parent':row.extra['source_parent'],'original_flag':row.extra['source_flag']} for row in added]
        # Quest navigation contains 29-byte term positions. Only absent position groups for matching/new
        # mission identities are added; repurposed existing quest IDs do not receive unrelated objectives.
        relative='TQNode.qpd';positions=quest_positions(self.source_bytes(relative));old=quest_positions((self.game/relative).read_bytes())
        official={r.key:r for r in incoming};current={r.key:r for r in before};new_ids={r.key for r in added}
        selected=[r for r in positions if r.key in new_ids or (r.key in official and r.key in current and
                   (official[r.key].extra['type'],official[r.key].extra['terms'])==(current[r.key].extra['type'],current[r.key].extra['terms']))]
        merged,new_positions=merge(old,selected);self.save(relative,serialize(merged,False),'navigation')
        self.table_result(relative,old,positions,merged,new_positions,'quest positions:29-byte terms')

    def details(self):
        relative='Tcd/TInfo.tcd';incoming=info(self.source_bytes(relative));before=info((self.game/relative).read_bytes())
        after,added=merge(before,incoming);self.save(relative,serialize(after),'chart');self.table_result(relative,before,incoming,after,added,'detail:CString lines')
        relative='Tcd/TQuestItem.tcd';incoming=quest_items(self.source_bytes(relative),True);before=quest_items((self.game/relative).read_bytes(),False)
        if any(r.key>65535 for r in incoming):raise ValueError("Quest reward key exceeds the native lookup type")
        after,added=merge(before,incoming);data=serialize(after)
        if [r.raw for r in quest_items(data,True)]!=[r.raw for r in after]:raise ValueError("Quest reward conversion failed")
        self.save(relative,data,'quest reward previews');self.table_result(relative,before,incoming,after,added,'35-byte reward preview; all 9 magic slots retained')

    def resources(self):
        for path in sorted((self.source/'Index').glob('*.IDX')):
            relative='Index/'+path.name
            if not (self.game/relative).is_file():
                self.report['deferred'].append({'path':relative,'reason':'No matching native resource catalogue'});continue
            files,entries=index(self.source_bytes(relative));before_files,before=index((self.game/relative).read_bytes());present={r[0] for r in before}
            selected=[entry for entry in entries if entry[0] not in present]
            if not selected:continue
            if path.name.lower()=='tclientw.idx':
                for ident,file_id,flags in selected:
                    source_file=self.source/'Data'/files[file_id]
                    if not source_file.is_file():
                        self.report['deferred'].append({'path':relative,'id':ident,'source_file':files[file_id],
                                                       'reason':'The original installation itself lacks this media file'});continue
                    raise ValueError("Unexpected additional media needs explicit file/flag import")
                continue
            by_file=collections.defaultdict(list)
            for ident,file_id,offset in entries:by_file[file_id].append(offset)
            all_ends={}
            for file_id,positions in by_file.items():
                file_path=(self.source/'Data'/files[file_id]).resolve()
                if not file_path.is_relative_to((self.source/'Data').resolve()):raise ValueError("Unsafe source resource path")
                positions=sorted(set(positions));ends=positions[1:]+[file_path.stat().st_size]
                all_ends[file_id]=dict(zip(positions,ends))
            output_files=list(before_files);output_entries=list(before);pack=bytearray();rows=[];source_cache={}
            for ident,file_id,offset in selected:
                source_relative='Data/'+files[file_id]
                if file_id not in source_cache:source_cache[file_id]=self.source_bytes(source_relative)
                source_data=source_cache[file_id];end=all_ends[file_id][offset]
                if not 0<=offset<end<=len(source_data):raise ValueError("Resource offset out of bounds")
                raw=source_data[offset:end];decoded=raw
                compressed=path.name.lower().endswith(('tclients.idx','tclientx.idx','tclienti.idx'))
                if compressed:
                    expanded,packed=struct.unpack_from('<II',raw)
                    if not 0<expanded<100000000 or not 0<packed<=len(raw)-8:raise ValueError("Invalid native resource chunk")
                    decoded=zlib.decompress(raw[8:8+packed])
                    if len(decoded)!=expanded:raise ValueError("Resource expansion length mismatch")
                    raw=raw[:8+packed]
                rows.append({'id':ident,'offset':len(pack),'bytes':len(raw),'decoded_bytes':len(decoded),'sha256':sha(decoded),
                             'source_file':source_relative,'source_offset':offset,'stored_sha256':sha(raw)})
                pack.extend(raw)
            original_folder=Path(files[selected[0][1]]).parent.as_posix()
            extension=Path(files[selected[0][1]]).suffix
            pack_file=original_folder+'/GameforgeOriginal_'+path.stem+extension
            if any(value.casefold()==pack_file.casefold() for value in output_files):raise ValueError("Import pack already registered")
            file_index=len(output_files);output_files.append(pack_file)
            output_entries.extend((row['id'],file_index,row['offset']) for row in rows)
            result=index_bytes(output_files,output_entries);parsed_files,parsed_entries=index(result)
            if parsed_entries[:len(before)]!=before or parsed_files[:len(before_files)]!=before_files:raise ValueError("Existing IDX entries changed")
            self.save('Data/'+pack_file,bytes(pack),'resource pack');self.save(relative,result,'index')
            self.report['resources'].append({'path':relative,'before_ids':len(before),'after_ids':len(output_entries),'added':rows,'pack':'Data/'+pack_file})
            print(path.name+': '+str(len(rows))+' original resource IDs added',flush=True)

    def auxiliary_files(self):
        for key,row in self.pinned.items():
            relative=row['path'];top=relative.split('/')[0].casefold()
            if top not in ('data','tmh','node','path') or (self.game/relative).exists():continue
            data=self.source_bytes(relative);self.save(relative,data,'absent original asset/navigation file')
            self.report['copied_files'].append({'path':relative,'bytes':len(data),'sha256':sha(data)})
        handled={row['path'].casefold() for row in self.report['tables']}
        for path in (self.source/'Tcd').glob('*.tcd'):
            relative='Tcd/'+path.name
            if relative.casefold() not in handled and not (self.game/relative).exists():
                self.report['deferred'].append({'path':relative,'reason':'No current client loader/validated conversion; not activated by copying a file'})

    def image_pages(self):
        def read_pages(root,original):
            relative='Index/TClientList.LST';data=self.source_bytes(relative) if original else (root/relative).read_bytes()
            reader=Reader(data,'latin1');count,total=reader.i32(),reader.i32();files=[reader.string(True).replace('\\','/') for _ in range(count)];reader.done()
            pages={}
            for filename in files:
                relative='Data/'+filename;data=self.source_bytes(relative) if original else (root/relative).read_bytes();position=0
                while position<len(data):
                    expanded,packed=struct.unpack_from('<II',data,position);raw=zlib.decompress(data[position+8:position+8+packed])
                    if len(raw)!=expanded:raise ValueError('Invalid image buffer chunk')
                    ident=int.from_bytes(raw[:4],'little')
                    if ident in pages:raise ValueError('Duplicate image buffer ID')
                    pages[ident]={'raw':raw,'stored':data[position:position+8+packed],'source_file':relative,'source_offset':position}
                    position+=8+packed
                if position!=len(data):raise ValueError('Image buffer file not fully consumed')
            if len(pages)!=total:raise ValueError('Image page catalogue count does not reconcile')
            return files,pages
        files,current=read_pages(self.game,False);_,original=read_pages(self.source,True)
        pages=[];remap={};reserved=set(current)|set(original);next_id=max(reserved)+1
        for ident in sorted(original.keys()-current.keys()):
            row=original[ident];pages.append({'id':ident,'source_id':ident,**row})
        # New IMAGESET sprites must retain the original pixel page even when that page's ID already
        # belongs to a customized page in Game. Allocate aliases and patch only the new sprites.
        images=next((row for row in self.report['resources'] if row['path'].lower()=='index/tclienti.idx'),None)
        if images:
            source_pack=(self.output/images['pack']).read_bytes();pack=bytearray()
            for row in images['added']:
                raw=zlib.decompress(source_pack[row['offset']+8:row['offset']+row['bytes']])
                reader=Reader(raw);reader.take(reader.i32()*4);reader.take(reader.i32()*8);reader.take(5);length=reader.i32();payload=reader.p
                if length>=16:
                    ident=int.from_bytes(raw[payload+12:payload+16],'little')
                    if ident and ident not in original:raise ValueError('Original image sprite references a missing source page')
                    if ident in current and original[ident]['raw']!=current[ident]['raw']:
                        if ident not in remap:
                            while next_id in reserved:next_id+=1
                            remap[ident]=next_id;reserved.add(next_id);next_id+=1
                            page=original[ident];replacement=struct.pack('<I',remap[ident])+page['raw'][4:]
                            pages.append({'id':remap[ident],'source_id':ident,'raw':replacement,'stored':None,
                                          'source_file':page['source_file'],'source_offset':page['source_offset']})
                        row['source_decoded_sha256']=row['sha256'];row['source_stored_sha256']=row['stored_sha256']
                        raw=raw[:payload+12]+struct.pack('<I',remap[ident])+raw[payload+16:]
                        row['page_remap']={'from':ident,'to':remap[ident],'raw_offset':payload+12}
                stored=struct.pack('<II',len(raw),len(zlib.compress(raw)))+zlib.compress(raw)
                row.update(offset=len(pack),bytes=len(stored),decoded_bytes=len(raw),sha256=sha(raw),stored_sha256=sha(stored));pack.extend(stored)
            index_files,entries=index((self.output/images['path']).read_bytes());positions={r['id']:r['offset'] for r in images['added']}
            entries=[(ident,file_id,positions.get(ident,position)) for ident,file_id,position in entries]
            self.save(images['pack'],bytes(pack),'resource pack with isolated original image pages')
            self.save(images['path'],index_bytes(index_files,entries),'index')
        if pages:
            pack=bytearray();metadata=[]
            for row in pages:
                raw=row['raw'];packed=zlib.compress(raw);stored=row['stored'] or struct.pack('<II',len(raw),len(packed))+packed
                metadata.append({'id':row['id'],'source_id':row['source_id'],'sha256':sha(raw),'source_file':row['source_file'],'source_offset':row['source_offset']})
                pack.extend(stored)
            filename='Img/GameforgeOriginal.TIS'
            if any(f.casefold()==filename.casefold() for f in files):raise ValueError('Image page pack already present')
            files=files+[filename];data=struct.pack('<ii',len(files),len(current)+len(pages))+b''.join(struct.pack('<i',len(raw))+raw for raw in (f.replace('/','\\').encode('latin1') for f in files))
            self.save('Data/'+filename,bytes(pack),'original image pages');self.save('Index/TClientList.LST',data,'image page catalogue')
            self.report['image_pages']={'added':metadata,'cloned_conflicting_pages':remap,'existing_pages_preserved':True}
        else:self.report['image_pages']={'added':[],'cloned_conflicting_pages':{},'existing_pages_preserved':True}

    def run(self):
        self.clone();self.named_tables();new_skills=self.skills();self.fixed_tables(new_skills);self.quests();self.details();self.resources();self.image_pages();self.auxiliary_files()
        self.report['source_files_verified']=len(self.checked)
        self.report['summary']={'changed_or_added_files':len(self.report['files']),
            'chart_records_added':sum(row['added_records'] for row in self.report['tables']),
            'indexed_resources_added':sum(len(row['added']) for row in self.report['resources']),
            'auxiliary_files_added':len(self.report['copied_files']),'deferred':len(self.report['deferred'])}
        (self.output/'gameforge-import.json').write_text(json.dumps(self.report,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
        print(json.dumps(self.report['summary']),flush=True)


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source',type=Path,required=True);parser.add_argument('--game',type=Path,required=True)
    parser.add_argument('--output',type=Path,required=True);parser.add_argument('--manifest',type=Path,default=Path('Gameforge/reference.json'))
    args=parser.parse_args();Preparation(args.source,args.game,args.output,args.manifest).run()


if __name__=='__main__':main()
