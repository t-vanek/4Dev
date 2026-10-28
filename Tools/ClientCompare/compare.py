"""Read-only inventory, PE, IDX resource, validated TCD/quest and TIF comparison.

Usage: python Tools/ClientCompare/compare.py --official <directory> --ours Game --output <new-directory>
Data layouts follow the existing ClassicPort, TStringExport and UiEditor readers.
No game files are rewritten; all extracted text and manifests go to --output.
"""
import argparse, collections, concurrent.futures, csv, datetime, hashlib, json, mmap, pathlib, re, struct, sys, zlib

ROOT = pathlib.Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / '.runtime/client-compare-tools/pylib'))
import pefile

def sha(data): return hashlib.sha256(data).hexdigest()
def write_json(path, data):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(data, ensure_ascii=False, indent=2), encoding='utf-8')
def write_csv(path, rows, columns):
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open('w', encoding='utf-8-sig', newline='') as file:
        writer = csv.DictWriter(file, fieldnames=columns, extrasaction='ignore')
        writer.writeheader(); writer.writerows(rows)

class Reader:
    def __init__(self, data, encoding='cp1250'): self.data, self.p, self.encoding = data, 0, encoding
    def take(self, size):
        if size < 0 or size > len(self.data) - self.p: raise ValueError(f'Out of bounds at {self.p}, size {size}')
        start = self.p; self.p += size
        return self.data[start:self.p]
    def number(self, fmt): return struct.unpack('<' + fmt, self.take(struct.calcsize('<' + fmt)))[0]
    def u8(self): return self.number('B')
    def u16(self): return self.number('H')
    def u32(self): return self.number('I')
    def i32(self): return self.number('i')
    def string(self, int_length=False):
        wide = False
        if int_length: length = self.i32()
        else:
            length = self.u8()
            if length == 255:
                length = self.u16()
                if length == 65534:
                    wide = True; length = self.u8()
                    if length == 255: length = self.u16()
                if length == 65535:
                    length = self.u32()
                    if length == 0xffffffff: length = self.number('Q')
        if length < 0 or length > 1000000: raise ValueError('Invalid text length')
        return self.take(length * (2 if wide else 1)).decode('utf-16le' if wide else self.encoding, errors='replace')
    def done(self):
        if self.p != len(self.data): raise ValueError(f'Trailing {len(self.data) - self.p} bytes')

def inventory(root):
    def inspect(path):
        stat = path.stat()
        with path.open('rb') as file: digest = hashlib.file_digest(file, 'sha256').hexdigest()
        if path.stat().st_mtime_ns != stat.st_mtime_ns: raise RuntimeError('File changed during inventory: ' + str(path))
        return dict(path=path.relative_to(root).as_posix(), size=stat.st_size, sha256=digest, mtime_ns=stat.st_mtime_ns)
    files = sorted(x for x in root.rglob('*') if x.is_file())
    with concurrent.futures.ThreadPoolExecutor(max_workers=6) as pool: rows = list(pool.map(inspect, files))
    return {x['path'].lower(): x for x in rows}

def pe_analysis(path, output, label):
    data = path.read_bytes(); pe = pefile.PE(data=data)
    strings = []
    for regex, encoding in [(rb'[\x20-\x7e]{5,}', 'ascii'), (rb'(?:[\x20-\x7e]\x00){5,}', 'utf-16le')]:
        for match in re.finditer(regex, data):
            try: rva = pe.get_rva_from_offset(match.start()); address = hex(pe.OPTIONAL_HEADER.ImageBase + rva)
            except pefile.PEFormatError: address = ''
            strings.append(dict(offset=hex(match.start()), address=address, text=match.group().decode(encoding)))
    classes = sorted(set(x for row in strings for x in re.findall(r'\.\?AV([A-Za-z0-9_]+)@@', row['text'])))
    imports = {x.dll.decode(): [y.name.decode(errors='replace') if y.name else '#' + str(y.ordinal) for y in x.imports]
               for x in getattr(pe, 'DIRECTORY_ENTRY_IMPORT', [])}
    write_csv(output / (label + '-strings.csv'), strings, ['offset', 'address', 'text'])
    write_json(output / (label + '-classes.json'), classes)
    return dict(path=str(path.resolve()), sha256=sha(data), size=len(data), machine=hex(pe.FILE_HEADER.Machine),
                image_base=hex(pe.OPTIONAL_HEADER.ImageBase), entry_rva=hex(pe.OPTIONAL_HEADER.AddressOfEntryPoint),
                linker_timestamp_utc=datetime.datetime.fromtimestamp(pe.FILE_HEADER.TimeDateStamp, datetime.timezone.utc).isoformat(),
                sections=[dict(name=s.Name.rstrip(b'\0').decode(), rva=hex(s.VirtualAddress), raw_bytes=s.SizeOfRawData,
                               virtual_bytes=s.Misc_VirtualSize, entropy=round(s.get_entropy(), 4)) for s in pe.sections],
                imports=imports, classes=classes, strings=len(strings))

def index_resources(root, name):
    r = Reader((root / 'Index' / name).read_bytes(), 'latin1')
    count, total = r.i32(), r.i32()
    if not 0 <= count < 100000 or not 0 <= total < 1000000: raise ValueError('Unsupported IDX layout')
    files = [r.string(True).replace('\\', '/') for _ in range(count)]
    entries = [(r.u32(), r.i32(), r.u32()) for _ in range(total)]; r.done()
    # LoadMEDIA uses the final DWORD as media type/volume, not as a file offset.
    if name.lower() == 'tclientw.idx':
        records, errors = {}, []
        for ident, file_index, flags in entries:
            if not 0 <= file_index < len(files): raise ValueError('Invalid media file index')
            rel = files[file_index]; path = (root / 'Data' / rel).resolve()
            if not path.is_relative_to((root / 'Data').resolve()): raise ValueError('Unsafe media path')
            raw = path.read_bytes() if path.is_file() else None
            if raw is None: errors.append('Missing media ' + rel)
            records.setdefault(ident, dict(id=ident,hex_id=f'{ident:08X}',file=rel,offset=0,bytes=len(raw) if raw is not None else 0,
                                          sha256=sha(raw) if raw is not None else None,media_flags=flags,available=raw is not None))
        return records, dict(files=count,entries=total,unique_ids=len(records),errors=errors,final_dword='media_type_and_volume')
    groups = collections.defaultdict(list)
    for ident, file_index, position in entries:
        if not 0 <= file_index < len(files): raise ValueError('Invalid file index')
        groups[file_index].append((ident, position))
    records, errors = {}, []
    for file_index, group in groups.items():
        rel = files[file_index]; path = (root / 'Data' / rel).resolve()
        if not path.is_relative_to((root / 'Data').resolve()): raise ValueError('Unsafe resource path')
        if not path.is_file():
            errors.append('Missing resource pack ' + rel); continue
        with path.open('rb') as file:
            if path.stat().st_size == 0:
                errors.append('Empty resource pack ' + rel); continue
            with mmap.mmap(file.fileno(), 0, access=mmap.ACCESS_READ) as data:
                positions = sorted({p for _, p in group})
                ends = dict(zip(positions, positions[1:] + [len(data)]))
                for ident, position in group:
                    if position > len(data): errors.append(f'{rel}: out-of-bounds {ident}'); continue
                    raw = data[position:ends[position]]
                    if name.lower().endswith(('tclients.idx', 'tclientx.idx')) and len(raw) >= 8:
                        expanded, packed = struct.unpack_from('<II', raw)
                        if 0 < expanded < 100000000 and packed <= len(raw) - 8:
                            try:
                                decoded = zlib.decompress(raw[8:8 + packed])
                                if len(decoded) != expanded: raise ValueError('Wrong expanded size')
                                raw = decoded
                            except (zlib.error, ValueError) as exc: errors.append(f'{rel}/{ident}: {exc}')
                    record = dict(id=ident, hex_id=f'{ident:08X}', file=rel, offset=position, bytes=len(raw), sha256=sha(raw))
                    if ident in records: errors.append(f'Duplicate ID {ident}; first record retained')
                    else: records[ident] = record
    return records, dict(files=count, entries=total, unique_ids=len(records), errors=errors)

SCHEMAS = {
    'titem.tcd': [(6, 1, 103, 2, False)], 'tmon.tcd': [(4, 2, 125, 2, False), (4, 2, 128, 2, False)],
    'tregion.tcd': [(4, 1, 55, 4, False)], 'tportal.tcd': [(2, 1, 14, 2, False)],
    'tchartitle.tcd': [(12, 4, 0, 2, False)], 'tchartitlegroup.tcd': [(1, 1, 0, 1, False)],
    'tmonthrank.tcd': [(1, 1, 2, 1, False)], 'thelp.tcd': [(8, 2, 0, 4, False)],
    'titemmagic.tcd': [(0, 1, 8, 2, True)], 'tgodtower.tcd': [(0, 1, 28, 4, True)],
    'tgodball.tcd': [(0, 1, 20, 4, True)], 'tmonshop.tcd': [(0, 1, 12, 4, True)],
    'tportalregion.tcd': [(2, 1, 0, 2, False)], 'tnpcinfo.tcd': [(20, 2, 0, 4, False)],
    'tcountryinfo.tcd': [(0, 1, 4, 4, True)], 'tclassinfo.tcd': [(0, 1, 4, 4, True)],
    'traceinfo.tcd': [(0, 1, 4, 4, True)], 'tdynamich.tcd': [(0, 1, 4, 4, True)],
    'tauctiontree.tcd': [(0, 1, 4, 4, True)], 'tmenuitem.tcd': [(0, 1, 9, 4, True)],
    'tmount.tcd': [(2, 0, 6, 2, False)],
}

def table(path):
    data = path.read_bytes(); name = path.name.lower()
    schemas = SCHEMAS.get(name, [])
    if name == 'tnpcglobal.tcd' or re.fullmatch(r'tnpc[0-9a-f]{8}\.tcd', name): schemas = [(6, 2, n, 4, False) for n in (75, 67)]
    if name == 'tskill.tcd': schemas = [(0, 1, 166, 2, True), (2, 1, 144, 2, False)]
    # TPet is the companion format (17 bytes here), not the 8-byte mount table.
    # Its changed field order needs a separate semantic decoder; do not guess.
    if name == 'tpet.tcd': raise ValueError('Unsupported companion schema; TMount is parsed separately')
    if name == 'tstring.tcd': schemas = [(2, 1, 0, 2, False)]
    failures = []
    for prefix, strings, suffix, key_size, key_after in schemas:
        try:
            r = Reader(data); count = r.u16(); rows = []
            for index in range(count):
                start = r.p; head = r.take(prefix)
                ident = int.from_bytes(head[:key_size], 'little') if prefix else 0
                texts = [r.string() for _ in range(strings)]
                tail = r.take(suffix)
                if key_after: ident = int.from_bytes(tail[:key_size], 'little')
                if name == 'thelp.tcd': ident = str(ident) + ':' + str(int.from_bytes(head[4:6], 'little'))
                rows.append(dict(id=str(ident), name=' | '.join(texts), sha256=sha(data[start:r.p]),
                                 payload_sha256=sha(head + tail), texts=texts))
            r.done()
            ids = [x['id'] for x in rows]
            if name not in ('tstring.tcd',) and not name.startswith('tnpc') and len(set(ids)) != len(ids): raise ValueError('Duplicate IDs')
            return rows, dict(layout=f'prefix={prefix}; strings={strings}; suffix={suffix}; key={key_size}; key_after={key_after}',
                              count=count, unique_ids=len(set(ids)), duplicates=count - len(set(ids)), fully_consumed=True)
        except (ValueError, struct.error, IndexError) as exc: failures.append(str(exc))
    raise ValueError('; '.join(failures) if failures else 'Schema not supported')

def quests(path):
    data = path.read_bytes()
    for retail in (False, True):
        try:
            r = Reader(data); class_count = r.i32()
            if not 0 <= class_count < 10000: raise ValueError('Invalid quest class count')
            for _ in range(class_count): r.u32(); r.string(True); r.take(1)
            rows = []
            while r.p < len(data):
                start = r.p
                if retail: r.take(4)
                ident = r.u32(); r.take(7 if retail else 6)
                texts = [r.string(True) for _ in range(8)]
                r.take(1); terms = r.i32()
                if not 0 <= terms < 1000: raise ValueError('Invalid quest terms')
                for _ in range(terms):
                    r.take(6); texts.extend([r.string(True), r.string(True)])
                rewards = r.i32()
                if not 0 <= rewards < 1000: raise ValueError('Invalid quest rewards')
                r.take(rewards * 8 + 8)
                rows.append(dict(id=str(ident), name=texts[0], sha256=sha(data[start:r.p]), payload_sha256='', texts=texts))
            r.done()
            return rows, dict(layout='quest-retail' if retail else 'quest', classes=class_count, count=len(rows), fully_consumed=True)
        except (ValueError, struct.error, IndexError): pass
    raise ValueError('No validated quest layout')

def ui(path):
    r = Reader(path.read_bytes()); magic = r.i32(); shared = []; total_nodes = 0
    def node(depth=0):
        nonlocal total_nodes
        if depth > 128: raise ValueError('Excessive TIF depth')
        header = r.take(175); ident = struct.unpack_from('<I', header)[0]; kind = header[4]
        tooltip, text = r.string(True), r.string(True); children = r.i32(); total_nodes += 1
        digest = hashlib.sha256(header); texts = {x for x in (text, tooltip) if x}; count = 1
        if children < 0:
            sh = shared[-children - 1]; digest.update(sh[0]); texts.update(sh[1]); count += sh[2]
        else:
            if children > 100000: raise ValueError('Invalid TIF child count')
            for _ in range(children):
                child = node(depth + 1); digest.update(child[2]); texts.update(child[3]); count += child[4]
        return ident, kind, digest.digest(), texts, count
    if magic == -2:
        for _ in range(r.i32()):
            digest = hashlib.sha256(); texts = set(); count = 0
            for _ in range(r.i32()):
                child = node(); digest.update(child[2]); texts.update(child[3]); count += child[4]
            shared.append((digest.digest(), texts, count))
        frame_count = r.i32()
    else: frame_count = magic
    if not 0 <= frame_count < 10000: raise ValueError('Invalid TIF frame count')
    frames = []
    for _ in range(frame_count):
        ident, kind, digest, texts, count = node()
        frames.append(dict(id=str(ident), hex_id=f'{ident:08X}', type=kind, structure_sha256=digest.hex(), nodes=count,
                           texts=sorted(texts)))
    font_count = r.i32(); r.take(font_count * 72); r.done()
    ids=[x['id'] for x in frames]
    return frames, dict(version=2 if magic == -2 else 1, frame_count=frame_count, unique_ids=len(set(ids)),
                        duplicate_ids=sorted(x for x,n in collections.Counter(ids).items() if n>1), fonts=font_count,
                        physical_nodes=total_nodes, shared_lists=len(shared), fully_consumed=True)

def compare_records(a, b):
    ak, bk = set(a), set(b)
    return dict(official=len(a), ours=len(b), shared=len(ak & bk), official_only=sorted(ak - bk), ours_only=sorted(bk - ak))

def main(args):
    official, ours, output = args.official.resolve(), args.ours.resolve(), args.output.resolve()
    if any(output.is_relative_to(root) or root.is_relative_to(output) for root in (official, ours)):
        raise ValueError('Output must be separate from input directories')
    output.mkdir(parents=True, exist_ok=True)
    report = dict(created_utc=datetime.datetime.now(datetime.timezone.utc).isoformat(), official=str(official), ours=str(ours), errors=[])
    print('Hashing complete file inventories...', flush=True)
    inventories = [inventory(root) for root in (official, ours)]
    for label, inv in zip(('official', 'ours'), inventories): write_csv(output / (label + '-files.csv'), inv.values(), ['path','size','sha256','mtime_ns'])
    a, b = inventories; rows = []
    for key in sorted(a.keys() | b.keys()):
        status = 'ours_only' if key not in a else 'official_only' if key not in b else 'identical' if a[key]['sha256'] == b[key]['sha256'] else 'different'
        rows.append(dict(path=(a.get(key) or b[key])['path'], status=status, official_bytes=a.get(key,{}).get('size',''),
                         ours_bytes=b.get(key,{}).get('size',''), official_sha256=a.get(key,{}).get('sha256',''), ours_sha256=b.get(key,{}).get('sha256','')))
    write_csv(output / 'files-diff.csv', rows, ['path','status','official_bytes','ours_bytes','official_sha256','ours_sha256'])
    report['files'] = dict(statuses=dict(collections.Counter(x['status'] for x in rows)),
                           official_bytes=sum(x['size'] for x in a.values()), ours_bytes=sum(x['size'] for x in b.values()))
    report['file_categories'] = {}
    for category in sorted({x['path'].split('/')[0] if '/' in x['path'] else '(root)' for x in rows}):
        report['file_categories'][category] = dict(collections.Counter(x['status'] for x in rows if (x['path'].split('/')[0] if '/' in x['path'] else '(root)') == category))
    write_json(output / 'summary.json', report)
    print('Analyzing PE metadata and class evidence...', flush=True)
    analysis_pe = output / 'TClient.official.analysis.exe'
    report['pe'] = {label:pe_analysis(path,output,label) for label,path in
                    [('official-packed',official/'TClient.exe'),('ours',ours/'TClient.exe')] + ([('official-unpacked', analysis_pe)] if analysis_pe.exists() else [])}
    if 'official-unpacked' in report['pe']:
        ca, cb = (set(report['pe'][x]['classes']) for x in ('official-unpacked', 'ours'))
        report['classes'] = compare_records({x:None for x in ca}, {x:None for x in cb})
        source = '\n'.join(x.read_text(encoding='utf-8',errors='replace') for base in (ROOT/'Client',ROOT/'Lib/Own') for x in base.rglob('*.h'))
        report['classes']['official_only_absent_from_our_headers'] = [x for x in sorted(ca-cb) if x.startswith(('CT','T')) and not re.search(r'\b'+re.escape(x)+r'\b',source)]
    print('Comparing indexed resources by ID and decoded content...', flush=True)
    report['indices'] = {}
    names = sorted({x.name for root in (official,ours) for x in (root/'Index').glob('*.IDX')})
    for name in names:
        try:
            ra, ma = index_resources(official,name); rb, mb = index_resources(ours,name)
            diff = compare_records(ra,rb); common = ra.keys() & rb.keys()
            available={x for x in common if ra[x]['sha256'] is not None and rb[x]['sha256'] is not None}
            hashes_a=collections.defaultdict(list);hashes_b=collections.defaultdict(list)
            for ident,row in ra.items():
                if row['sha256'] is not None:hashes_a[row['sha256']].append(ident)
            for ident,row in rb.items():
                if row['sha256'] is not None:hashes_b[row['sha256']].append(ident)
            equivalent_a=[dict(id=x,equivalent_ours_ids=hashes_b[ra[x]['sha256']]) for x in diff['official_only'] if ra[x]['sha256'] is not None and ra[x]['sha256'] in hashes_b]
            equivalent_b=[dict(id=x,equivalent_official_ids=hashes_a[rb[x]['sha256']]) for x in diff['ours_only'] if rb[x]['sha256'] is not None and rb[x]['sha256'] in hashes_a]
            diff.update(identical=sum(ra[x]['sha256']==rb[x]['sha256'] for x in available), different=sum(ra[x]['sha256']!=rb[x]['sha256'] for x in available),
                        shared_unavailable=len(common-available),
                        official_only_byte_identical_under_other_id=equivalent_a,ours_only_byte_identical_under_other_id=equivalent_b,
                        official_metadata=ma, ours_metadata=mb)
            report['indices'][name] = diff
            write_csv(output/'indices'/(name+'.csv'), [dict(id=x,status='official_only' if x not in rb else 'ours_only' if x not in ra else 'identical' if ra[x]['sha256']==rb[x]['sha256'] else 'different',
                      official_file=ra.get(x,{}).get('file',''),ours_file=rb.get(x,{}).get('file',''),official_sha256=ra.get(x,{}).get('sha256',''),ours_sha256=rb.get(x,{}).get('sha256',''))
                      for x in sorted(ra.keys()|rb.keys())],['id','status','official_file','ours_file','official_sha256','ours_sha256'])
            print(f'  {name}: official {len(ra)}, ours {len(rb)}, missing {len(diff["official_only"])}, extra {len(diff["ours_only"])}',flush=True)
        except (OSError,ValueError,struct.error,IndexError) as exc: report['errors'].append(dict(stage='index',file=name,error=str(exc)))
    write_json(output/'summary.json', report)
    print('Extracting validated data-table records...',flush=True)
    report['tables'] = {}; report['table_headers_unvalidated'] = {}
    table_paths = sorted({x.name for root in (official,ours) for x in (root/'Tcd').glob('*.tcd')} | {'TQuest.mpq'})
    for name in table_paths:
        paths = [root/('' if name=='TQuest.mpq' else 'Tcd')/name for root in (official,ours)]
        if not all(x.exists() for x in paths): continue
        if name.lower() not in SCHEMAS and name.lower() not in ('tstring.tcd','tskill.tcd','tpet.tcd','tquest.mpq') and not re.fullmatch(r'tnpc(?:global|[0-9a-f]{8})\.tcd',name,re.I):
            report['table_headers_unvalidated'][name]=[int.from_bytes(p.read_bytes()[:2],'little') for p in paths]; continue
        try:
            parser = quests if name=='TQuest.mpq' else table
            (ra, ma), (rb, mb) = [parser(p) for p in paths]
            for label, records in [('official',ra),('ours',rb)]: write_json(output/'tables'/f'{name}.{label}.json',records)
            da,db = [{x['id']:x for x in rows} for rows in (ra,rb)]
            diff=compare_records(da,db); common=da.keys()&db.keys()
            diff.update(official_metadata=ma,ours_metadata=mb,
                        identical=sum(da[x]['sha256']==db[x]['sha256'] for x in common),
                        identical_payload_ignoring_text=(sum(da[x]['payload_sha256']==db[x]['payload_sha256'] for x in common) if ma['layout']==mb['layout'] and name!='TQuest.mpq' else None))
            report['tables'][name]=diff
            write_csv(output/'tables'/(name+'.diff.csv'),[dict(id=x,status='official_only' if x not in db else 'ours_only' if x not in da else 'identical' if da[x]['sha256']==db[x]['sha256'] else 'different',
                      official_name=da.get(x,{}).get('name',''),ours_name=db.get(x,{}).get('name','')) for x in sorted(da.keys()|db.keys(),key=str)],['id','status','official_name','ours_name'])
        except (ValueError,struct.error,IndexError) as exc: report['errors'].append(dict(stage='table',file=name,error=str(exc)))
    print('Extracting and comparing compiled UI frames...',flush=True)
    report['ui']={}
    frames=[]
    for label,root in [('official',official),('ours',ours)]:
        try:
            records,meta=ui(root/'TClientCmd.tif');frames.append({x['id']:x for x in records})
            write_json(output/f'ui-{label}.json',records);report['ui'][label]=meta
        except (ValueError,struct.error,IndexError) as exc: report['errors'].append(dict(stage='ui',file=label,error=str(exc)))
    if len(frames)==2:
        diff=compare_records(*frames);report['ui']['diff']=diff
        write_csv(output/'ui-diff.csv',[dict(id=x,status='official_only' if x not in frames[1] else 'ours_only' if x not in frames[0] else 'shared',
                  official_text=' | '.join(frames[0].get(x,{}).get('texts',[])),ours_text=' | '.join(frames[1].get(x,{}).get('texts',[])))
                  for x in sorted(frames[0].keys()|frames[1].keys(),key=int)],['id','status','official_text','ours_text'])
    report['limitations']=['This is the installed snapshot, not a claim about the current online Gameforge release.',
        'Same IDs can be repurposed between versions. Record presence is not proof of working gameplay or server support.',
        'RTTI/class absence alone is not proof of absent functionality; classes can be renamed, merged or compiled out.',
        'Raw unknown-table header words are unvalidated and are never used as record counts in conclusions.',
        'All parsers validate bounds and full file consumption; unsupported layouts appear in errors.',
        'Source code and deployed EXE are separate evidence; uncompiled source changes are not treated as deployed features.']
    write_json(output/'summary.json',report)
    print(json.dumps(dict(files=report['files'],indices=len(report['indices']),tables=len(report['tables']),errors=report['errors'],ui=report['ui']),ensure_ascii=True,indent=2),flush=True)

if __name__=='__main__':
    parser=argparse.ArgumentParser();parser.add_argument('--official',type=pathlib.Path,required=True);parser.add_argument('--ours',type=pathlib.Path,required=True);parser.add_argument('--output',type=pathlib.Path,required=True)
    main(parser.parse_args())
