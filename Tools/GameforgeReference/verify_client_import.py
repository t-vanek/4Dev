"""Independently verify original-ID coverage, pre-existing records, resource hashes and preserved image pages."""
import argparse
import collections
import hashlib
import json
from pathlib import Path
import struct
import zlib

from client_facts import Reader
from client_records import FIXED,fixed,info,quest_items,quest_positions
from compare import table,quests


def sha(data):return hashlib.sha256(data).hexdigest()
def pages(root):
    reader=Reader((root/'Index/TClientList.LST').read_bytes(),'latin1');count,total=reader.i32(),reader.i32()
    files=[reader.string(True).replace('\\','/') for _ in range(count)];reader.done();result={}
    for filename in files:
        data=(root/'Data'/filename).read_bytes();offset=0
        while offset<len(data):
            expanded,packed=struct.unpack_from('<II',data,offset);raw=zlib.decompress(data[offset+8:offset+8+packed]);offset+=8+packed
            if len(raw)!=expanded:raise ValueError('Image page decompression mismatch')
            ident=int.from_bytes(raw[:4],'little')
            if ident in result:raise ValueError('Duplicate image page ID')
            result[ident]=raw
        if offset!=len(data):raise ValueError('Image file has trailing/truncated data')
    if len(result)!=total:raise ValueError('Image page total mismatch')
    return result


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source',type=Path,required=True);parser.add_argument('--before',type=Path,required=True)
    parser.add_argument('--prepared',type=Path,required=True);parser.add_argument('--output',type=Path,required=True)
    args=parser.parse_args();report=json.loads((args.prepared/'gameforge-import.json').read_text(encoding='utf-8'))
    for row in report['files']:
        if sha((args.prepared/row['path']).read_bytes())!=row['after_sha256']:raise ValueError('Prepared file hash mismatch')
        old=args.before/row['path']
        if row['before_sha256'] is None:
            if old.exists():raise ValueError('A new file would overwrite current work: '+row['path'])
        elif not old.exists() or sha(old.read_bytes())!=row['before_sha256']:raise ValueError('The current input changed: '+row['path'])
    checked=[]
    for entry in report['tables']:
        rel=entry['path'];name=Path(rel).name.lower();source=args.source/rel;current=args.before/rel;target=args.prepared/rel
        if name=='tquest.mpq':
            incoming,_=quests(source);before,_=quests(current);after,meta=quests(target)
        elif name=='tinfo.tcd':incoming,before,after=(info(p.read_bytes()) for p in (source,current,target))
        elif name=='tquestitem.tcd':incoming=quest_items(source.read_bytes(),True);before=quest_items(current.read_bytes(),False);after=quest_items(target.read_bytes(),True)
        elif name=='tqnode.qpd':incoming,before,after=(quest_positions(p.read_bytes()) for p in (source,current,target))
        elif name in FIXED:
            definition=FIXED[name]
            incoming,before,after=(fixed(p.read_bytes(),*definition[:3],key=definition[3]) for p in (source,current,target))
        else:
            incoming,_=table(source);before,_=table(current) if current.exists() else ([],{});after,meta=table(target)
        def key(row):return row['id'] if isinstance(row,dict) else row.key
        def signature(row):return row['sha256'] if isinstance(row,dict) else sha(row.raw)
        if [signature(r) for r in before]!=[signature(r) for r in after[:len(before)]]:raise ValueError('An existing record changed: '+rel)
        missing={key(row) for row in incoming}-{key(row) for row in after}
        if missing:raise ValueError('Original IDs still missing in '+rel+': '+str(sorted(missing)[:10]))
        if len(after)!=entry['after_records'] or len(after)-len(before)!=entry['added_records']:raise ValueError('Chart counts do not reconcile')
        checked.append({'path':rel,'original_ids_covered':True,'existing_records_preserved':True,'after_records':len(after)})
    before_pages,after_pages,original_pages=pages(args.before),pages(args.prepared),pages(args.source)
    if any(after_pages.get(ident)!=raw for ident,raw in before_pages.items()):raise ValueError('An existing image page changed')
    if not original_pages.keys()<=after_pages.keys():raise ValueError('Original image page IDs still missing')
    for row in report['image_pages']['added']:
        if after_pages[row['id']][4:]!=original_pages[row['source_id']][4:]:raise ValueError('Copied image pixels/metadata changed')
    for resource in report['resources']:
        data=(args.prepared/resource['pack']).read_bytes()
        for row in resource['added']:
            raw=data[row['offset']:row['offset']+row['bytes']]
            if sha(raw)!=row['stored_sha256']:raise ValueError('Stored resource hash mismatch')
            if row.get('page_remap'):
                decoded=bytearray(zlib.decompress(raw[8:]));m=row['page_remap'];struct.pack_into('<I',decoded,m['raw_offset'],m['from'])
                if sha(decoded)!=row['source_decoded_sha256']:raise ValueError('Image remapping changed an unrelated field')
    result={'passed':True,'manifest_sha256':sha((args.prepared/'gameforge-import.json').read_bytes()),'tables':checked,'total_chart_records':sum(row['after_records'] for row in checked),
            'original_image_pages':len(original_pages),'after_image_pages':len(after_pages),
            'assertions':['All original IDs covered in every supported chart, including TString',
                          'Every pre-existing chart record retained; legacy quest previews reversibly extended with three zero magic slots',
                          'Existing image pages unchanged; isolated original pages retain all pixel bytes',
                          'New image sprite remapping changes only its page ID', 'All prepared file and resource hashes match']}
    args.output.write_text(json.dumps(result,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
    print(json.dumps({key:value for key,value in result.items() if key not in ('tables','assertions')}))


if __name__=='__main__':main()
