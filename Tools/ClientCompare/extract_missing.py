"""Extract indexed records missing from our client into an analysis directory.

Raw files retain record headers and are not installable standalone game assets.
Texture/SFX zlib envelopes are decoded; the CSV gives provenance and content hashes.
"""
import argparse, collections, json, mmap, pathlib, struct, zlib
from compare import Reader, sha, write_csv, ROOT

def extract(root, output, summary):
    manifest=[]
    for name, diff in summary['indices'].items():
        if name.lower()=='tclientw.idx': continue  # Missing catalog entries may also lack physical audio.
        wanted=set(diff['official_only'])
        if not wanted: continue
        r=Reader((root/'Index'/name).read_bytes(),'latin1');nf,ne=r.i32(),r.i32()
        files=[r.string(True).replace('\\','/')for _ in range(nf)]
        entries=[(r.u32(),r.i32(),r.u32())for _ in range(ne)];r.done()
        groups=collections.defaultdict(list)
        for ident,idx,pos in entries:groups[idx].append((ident,pos))
        for idx,group in groups.items():
            selected=[x for x in group if x[0]in wanted]
            if not selected:continue
            path=(root/'Data'/files[idx]).resolve()
            if not path.is_relative_to((root/'Data').resolve()):raise ValueError('Unsafe resource path')
            with path.open('rb')as source,mmap.mmap(source.fileno(),0,access=mmap.ACCESS_READ)as data:
                positions=sorted({pos for _,pos in group});ends=dict(zip(positions,positions[1:]+[len(data)]))
                for ident,pos in selected:
                    raw=data[pos:ends[pos]];was_compressed=False
                    if name.lower().endswith(('tclients.idx','tclientx.idx')):
                        expanded,packed=struct.unpack_from('<II',raw)
                        raw=zlib.decompress(raw[8:8+packed]);assert len(raw)==expanded;was_compressed=True
                    rel=pathlib.Path(name)/(f'{ident:08X}.bin');target=output/rel;target.parent.mkdir(parents=True,exist_ok=True)
                    target.write_bytes(raw)
                    assert sha(target.read_bytes())==sha(raw)
                    manifest.append(dict(index=name,id=ident,hex_id=f'{ident:08X}',source_file=files[idx],source_offset=pos,
                                         bytes=len(raw),zlib_decoded=was_compressed,sha256=sha(raw),extracted_file=rel.as_posix()))
    write_csv(output/'manifest.csv',manifest,['index','id','hex_id','source_file','source_offset','bytes','zlib_decoded','sha256','extracted_file'])
    print(json.dumps(dict(records=len(manifest),bytes=sum(x['bytes']for x in manifest)),indent=2))

if __name__=='__main__':
    p=argparse.ArgumentParser();p.add_argument('--output',type=pathlib.Path,required=True);a=p.parse_args()
    summary=json.loads((a.output/'summary.json').read_text(encoding='utf8'))
    extract(pathlib.Path(summary['official']),a.output/'official-only-resources',summary)
