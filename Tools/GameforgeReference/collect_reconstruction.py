"""Collect lossless, versioned reconstruction evidence; default storage is SQL Server.

Examples from the repository root:
 python Tools/GameforgeReference/collect_reconstruction.py import-client
 python Tools/GameforgeReference/collect_reconstruction.py capture-once
 python Tools/GameforgeReference/collect_reconstruction.py watch --interval 30
 python Tools/GameforgeReference/collect_reconstruction.py status
 python Tools/GameforgeReference/collect_reconstruction.py entity --kind client.quest --entity 11015
 python Tools/GameforgeReference/collect_reconstruction.py export --output .runtime/reconstruction/export

Snapshots/reports are artifacts. Authoritative collected records and source bytes go
to the isolated FourDevReference database, never into a gameplay database.
"""
import argparse
import collections
from contextlib import contextmanager
from datetime import datetime,timezone
import hashlib
import html
import json
import os
from pathlib import Path
import shutil
import struct
import subprocess
import sys
import time

from decode_capture import decode, ORIGINAL_SHA256
from reconstruction_client import client_rows, read_stable
from reconstruction_store import SqlServerStore, SQLiteStore, batches, canonical, key, row
from reconstruction_publisher import publisher_rows

ROOT=Path(__file__).resolve().parents[2]
DEFAULT_WORK=ROOT/'.runtime/reconstruction'
PROFILE=key('GameforgeCZ',ORIGINAL_SHA256,'word-header-nine-keys')


def load(path):return json.loads(Path(path).read_text(encoding='utf-8-sig'))


def complete_pcapng_prefix(data):
    """Keep complete verified blocks if the live writer was in the middle of its last block."""
    offset=0;endian=None
    while offset+12<=len(data):
        if data[offset:offset+4]==b'\x0a\x0d\x0d\x0a':
            bom=data[offset+8:offset+12]
            endian='<' if bom==b'\x4d\x3c\x2b\x1a' else '>' if bom==b'\x1a\x2b\x3c\x4d' else None
            if endian is None:raise ValueError('Invalid pcapng byte order magic')
        if endian is None:raise ValueError('Capture does not start with a pcapng section')
        size=struct.unpack_from(endian+'I',data,offset+4)[0]
        if size<12 or size%4:raise ValueError('Invalid pcapng block length')
        if offset+size>len(data):break
        if struct.unpack_from(endian+'I',data,offset+size-4)[0]!=size:raise ValueError('pcapng block trailer disagrees')
        offset+=size
    if offset==0:raise ValueError('No complete pcapng blocks')
    return data[:offset],len(data)-offset


def snapshot_capture(path,work):
    # Bound the read to the size observed at open, even as the capture keeps growing.
    with path.open('rb') as stream:
        data=stream.read(os.fstat(stream.fileno()).st_size)
    complete,trimmed=complete_pcapng_prefix(data)
    digest=hashlib.sha256(complete).hexdigest()
    dest=work/'snapshots'/digest/'capture.pcapng'
    dest.parent.mkdir(parents=True,exist_ok=True)
    if not dest.exists():dest.write_bytes(complete)
    return dest,digest,trimmed


def discard_committed_snapshot(snapshot,work,digest):
    expected=(work/'snapshots').resolve()
    target=snapshot.parent.resolve()
    # Recursive cleanup is restricted to this tool's exact hash-named snapshot child.
    if target.parent!=expected or target.name!=digest or snapshot.parent.is_symlink():
        raise ValueError('Refusing snapshot cleanup outside the verified cache target')
    if len(digest)!=64 or any(c not in '0123456789abcdef' for c in digest):raise ValueError('Invalid snapshot digest')
    shutil.rmtree(target)


def session_identity(path):
    # Stable across snapshots, different for rotated/recreated capture files.
    return key('capture_session',str(path.resolve()).lower(),path.stat().st_ctime_ns)


def process_alive(pid):
    if os.name=='nt':
        # Read-only process lookup; no signal is sent to Windows processes.
        found=subprocess.run(['powershell','-NoProfile','-Command',
            'if(Get-Process -Id '+str(int(pid))+' -ErrorAction SilentlyContinue){exit 0}else{exit 1}'],capture_output=True,timeout=15)
        return found.returncode==0
    try:os.kill(pid,0)
    except OSError:return False
    return True


def import_client(args,store,include_code=True):
    version=hashlib.sha256((ROOT/'Tools/GameforgeReference/reconstruction_client.py').read_bytes()).hexdigest()
    source_rows=store.rows(kind='source.client')
    known={r['source'] for r in store.rows(kind='import.client_complete')
        if json.loads(r['payload']).get('parser_version')==version}
    raw_sources={r['source']:json.loads(r['payload']).get('raw_record_key',r['key']) for r in source_rows}
    raw_sources.update({r['source']:r['key'] for r in store.rows(kind='source.bytes')})
    inserted=batches(store,client_rows(args.source,args.manifest,args.work/'client-sources',known,raw_sources))
    if include_code:inserted+=batches(store,static_code_rows(args.decompiler))
    return inserted


def connection_rows(decoded,session,watermarks):
    for line in (decoded/'packets.jsonl').open(encoding='utf8'):
        packet=json.loads(line)
        identity=(packet['stream'],packet['direction'])
        if packet['sequence']<=watermarks.get(identity,0):continue
        packet['profile']=PROFILE
        ident=f'{packet["stream"]}:{packet["direction"]}:{packet["sequence"]}'
        yield row('packet',session,ident,packet,stable=True)


def event_rows(decoded,session):
    version=semantics_version()
    for event in load(decoded/'events.json'):
        ident=f'{event["stream"]}:{event["direction"]}:{event["sequence"]}:{event["kind"]}'
        # Each semantic version coexists; changing a parser never overwrites its earlier evidence.
        yield row('event.'+event['kind'],session,ident,dict(event,semantic_version=version))
    for transaction in load(decoded/'quest-transactions.json'):
        yield row('fact.quest_transaction',session,str(transaction['quest_id'])+':'+str(transaction['completion_sequence']),
            dict(transaction,semantic_version=version,evidence_class='observed_request_success_interval',publisher_formula_verified=False))


def semantics_version():
    return key('decoder_semantics',*[hashlib.sha256((ROOT/'Tools/GameforgeReference'/name).read_bytes()).hexdigest()
        for name in ('decode_capture.py','reconstruction_protocol.py')])


def observations_rows(session_path,known_images=None):
    known_images=set(known_images or [])
    for path in sorted((session_path/'observations').glob('*/observation.json')):
        data=read_stable(path);source=key('observation',str(path.resolve()),hashlib.sha256(data).hexdigest())
        meta=json.loads(data.decode('utf-8-sig'))
        yield row('observation',source,path.parent.name,meta,data)
        for image in meta.get('Screenshots',[]):
            photo=path.parent/image['File']
            if not photo.resolve().is_relative_to(path.parent.resolve()):raise ValueError('Unsafe observation image path')
            raw=read_stable(photo);digest=hashlib.sha256(raw).hexdigest()
            if digest!=image['Sha256']:raise ValueError('Observation screenshot hash changed')
            if digest in known_images:continue
            known_images.add(digest)
            yield row('source.image',digest,image['File'],dict(path=str(photo),sha256=digest,observation=source),raw)


def static_code_rows(path):
    """Index existing decompiler output as inferred evidence, never as original C++ source."""
    import re
    if not path.is_file():return
    data=read_stable(path);digest=hashlib.sha256(data).hexdigest();source=key('client_decompiler',digest,PROFILE)
    text=data.decode('utf8',errors='replace')
    dispatch={}
    for match in re.finditer(r'case (0x[0-9a-f]+):\s*(?:\{\s*)?uVar\d+ = (FUN_[0-9a-f]+)\(param_1,param_2\);',text):
        ident=int(match[1],16)
        if 0x2000<=ident<=0x3000:dispatch.setdefault(match[2],set()).add(ident)
    yield row('source.analysis',source,'client-decompiler',dict(path=str(path),sha256=digest,
        analysis_client_profile=PROFILE,classification='inferred_pseudocode',packet_dispatch_handlers=len(dispatch)))
    for chunk in re.split(r'(?=/\* Address: )',text):
        match=re.match(r'/\* Address: ([0-9a-f]+); (FUN_[0-9a-f]+)',chunk)
        if match and match[2] in dispatch:
            yield row('client.packet_handler',source,match[1],dict(address=match[1],message_ids=sorted(dispatch[match[2]]),
                pseudocode=chunk,classification='inferred_unreviewed',field_meanings_verified=False))


def packet_inventory(store):
    packets=store.rows(kind='packet')
    events=[r for r in store.rows(prefix='event.') if json.loads(r['payload']).get('semantic_version')==semantics_version()]
    known=set()
    for record in events:
        data=json.loads(record['payload'])
        if data['kind']!='schema_gap':known.add((record['source'],data['stream'],data['direction'],data['sequence']))
    partial=set()
    for record in events:
        data=json.loads(record['payload'])
        if data.get('semantic_scope')=='inspected_prefix':partial.add((record['source'],data['stream'],data['direction'],data['sequence']))
    counts=collections.Counter();unknown=collections.Counter();partial_counts=collections.Counter();examples={}
    for record in packets:
        data=json.loads(record['payload']);ident=(data['direction'],data['id_hex']);counts[ident]+=1
        pk=(record['source'],data['stream'],data['direction'],data['sequence'])
        if pk in partial:partial_counts[ident]+=1
        if pk not in known:
            unknown[ident]+=1
            examples.setdefault(ident,dict(source=record['source'],entity=record['entity'],frame=data['frame'],
                payload_bytes=len(data['body_hex'])//2))
    return [dict(direction=k[0],message_id=k[1],packets=n,uninterpreted_packets=unknown[k],
        partially_interpreted_packets=partial_counts[k],example=examples.get(k)) for k,n in counts.most_common()]


def report(store,output):
    output.mkdir(parents=True,exist_ok=True)
    counts=store.counts();inventory=packet_inventory(store)
    sources_by_key={r['source']:json.loads(r['payload']) for r in store.rows(kind='source.client')}
    sources=list(sources_by_key.values())
    client_gaps=[json.loads(r['payload']) for r in store.rows(kind='gap.client_parser')]
    transactions=[json.loads(r['payload']) for r in store.rows(kind='fact.quest_transaction')
        if json.loads(r['payload']).get('semantic_version')==semantics_version()]
    data=dict(schema=1,generated_utc=datetime.now(timezone.utc).isoformat(),counts=counts,message_coverage=inventory,
        source_files=len(sources),changed_sources=[s for s in sources if s['reference_status']!='matches_pinned'],
        current_semantic_version=semantics_version(),counts_include_archival_semantic_versions=True,
        raw_preserved_client_gaps=client_gaps,quest_transactions=transactions,
        missing_evidence=['Unvisited content and server-only rules','Drop and rare-spawn probability distributions',
            'Quest repeatability, reset schedules and level/class eligibility not exercised',
            'Persistence across logout/restart of the official and local servers','Meaning of opaque messages and unsupported client table fields'],
        gameplay_server_modified=False,baseline_complete=False)
    (output/'coverage.json').write_text(json.dumps(data,ensure_ascii=False,indent=2)+'\n',encoding='utf8')
    esc=html.escape
    content=['<!doctype html><html lang="cs"><meta charset="utf-8"><title>Podklady pro rekonstrukci 4Story</title>',
        '<style>body{font:16px system-ui;margin:32px auto;max-width:1200px;padding:0 24px}table{border-collapse:collapse;width:100%}td,th{padding:8px;text-align:left;border-bottom:1px solid #ccc}.note{background:#fff2cf;padding:16px}</style>',
        '<h1>Podklady pro rekonstrukci 4Story</h1>',
        '<p class="note">Samostatná evidence v SQL. Záznam nebo přítomnost dat neprokazuje přesnou původní pravděpodobnost ani funkční implementaci serveru. Odlišné zdroje a verze zůstávají oddělené.</p>',
        '<h2>Uložené záznamy</h2><table><tr><th>Druh</th><th>Počet</th></tr>']
    for kind,count in sorted(counts.items()):content.append(f'<tr><td>{esc(kind)}</td><td>{count:,}</td></tr>')
    content+=['</table><h2>Pokrytí síťových zpráv</h2><table><tr><th>Směr</th><th>ID</th><th>Celkem</th><th>Bez výkladu</th><th>Částečný výklad</th></tr>']
    for entry in inventory:
        content.append(f'<tr><td>{entry["direction"]}</td><td>{entry["message_id"]}</td><td>{entry["packets"]}</td><td>{entry["uninterpreted_packets"]}</td><td>{entry["partially_interpreted_packets"]}</td></tr>')
    content+=['</table><h2>Neinterpretované klientské zdroje</h2><ul>']
    for gap in client_gaps:content.append('<li>'+esc(gap['path']+': '+gap['reason'])+'</li>')
    content+=['</ul><p>Původní bajty těchto zdrojů jsou uchované v SQL pro pozdější doplnění parseru.</p>',
        '<h2>Zbývající důkazy</h2><ul>'+''.join('<li>'+esc(v)+'</li>' for v in data['missing_evidence'])+'</ul>',
        '<p><a href="coverage.json">Strojový přehled a dokončení questů</a></p></html>']
    (output/'report.html').write_text('\n'.join(content),encoding='utf8')
    return data


@contextmanager
def collection_lock(work):
    work.mkdir(parents=True,exist_ok=True)
    lock=work/'cycle.lock'
    if lock.exists():
        previous=int(lock.read_text(encoding='ascii').strip())
        if process_alive(previous):raise RuntimeError('Another collection cycle is using this work directory')
        lock.unlink()
    with lock.open('x',encoding='ascii') as file:file.write(str(os.getpid()))
    try:yield
    finally:
        if lock.exists() and lock.read_text(encoding='ascii').strip()==str(os.getpid()):lock.unlink()


def capture_once(args,store):
    with collection_lock(args.work):return capture_cycle(args,store)


def capture_cycle(args,store):
    capture=args.capture
    if capture is None:
        session=Path((ROOT/'.runtime/official-captures/latest-session.txt').read_text(encoding='utf-8-sig').strip())
        capture=session/'gameforge-cz.pcapng'
    else:session=capture.parent
    session_key=session_identity(capture)
    snapshot,digest,trimmed=snapshot_capture(capture,args.work)
    decoded=snapshot.parent/'decoded'
    inserted=batches(store,[row('source.capture',session_key,str(capture.resolve()),dict(session_key=session_key,
        capture_path=str(capture.resolve()),profile=PROFILE))])
    try:
        summary=decode(snapshot,args.analysis,decoded,args.tshark,args.server,include_wire=True,tolerate_semantic_errors=True)
    except Exception as exc:
        batches(store,[row('source.failed_capture',session_key,digest,
            dict(snapshot_sha256=digest,error=str(exc),integrity_verified=False,profile=PROFILE),snapshot.read_bytes())])
        raise
    watermarks=store.watermarks(session_key)
    inserted+=batches(store,connection_rows(decoded,session_key,watermarks))
    inserted+=batches(store,event_rows(decoded,session_key))
    inserted+=batches(store,observations_rows(session,{r['source'] for r in store.rows(kind='source.image')}))
    inserted+=batches(store,[row('source.capture',session_key,str(capture.resolve()),dict(session_key=session_key,
        capture_path=str(capture.resolve()),profile=PROFILE)),row('source.snapshot',session_key,digest,
        dict(summary,trimmed_partial_block_bytes=trimmed,snapshot_sha256=digest,snapshot_bytes=snapshot.stat().st_size,
            retained_snapshot_file=args.keep_snapshots,raw_capture_source=str(capture.resolve())))])
    result=dict(inserted=inserted,session_key=session_key,packets=summary['decrypted_packets'],
        events=summary['interpreted_events'],snapshot_sha256=digest,time_utc=datetime.now(timezone.utc).isoformat())
    args.work.mkdir(parents=True,exist_ok=True)
    (args.work/'last-cycle.json').write_text(json.dumps(result,indent=2)+'\n',encoding='utf8')
    if not args.keep_snapshots:discard_committed_snapshot(snapshot,args.work,digest)
    return result


def main():
    parser=argparse.ArgumentParser(description=__doc__,formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('action',choices=('import-client','import-publisher','capture-once','watch','status','entity','export','restore-source'))
    parser.add_argument('--publisher',type=Path,default=ROOT/'.runtime/gameforge-normalization/public')
    parser.add_argument('--source',type=Path,default=Path(r'C:\Program Files (x86)\4Story'))
    parser.add_argument('--manifest',type=Path,default=ROOT/'Gameforge/reference.json')
    parser.add_argument('--analysis',type=Path,default=ROOT/'.runtime/client-comparison-2026-10-27/TClient.official.analysis.exe')
    parser.add_argument('--decompiler',type=Path,default=ROOT/'.runtime/client-comparison-2026-10-27/decompilation-official/decompiled.c')
    parser.add_argument('--capture',type=Path)
    parser.add_argument('--keep-snapshots',action='store_true',help='Retain redundant per-cycle disk snapshots; SQL records and the original Wireshark file are always independent')
    parser.add_argument('--work',type=Path,default=DEFAULT_WORK)
    parser.add_argument('--output',type=Path,default=DEFAULT_WORK/'export')
    parser.add_argument('--tshark',type=Path,default=Path(r'C:\Program Files\Wireshark\tshark.exe'))
    parser.add_argument('--server',default='79.110.88.11')
    parser.add_argument('--container',default='araz-mssql')
    parser.add_argument('--sqlite',type=Path,help='Explicit alternate/test SQL backend; default is isolated SQL Server database')
    parser.add_argument('--interval',type=int,default=30)
    parser.add_argument('--cycles',type=int,default=0,help='0 means continuously until a stop file or Ctrl+C')
    parser.add_argument('--report-every',type=int,default=10,help='Watch report frequency in collection cycles; 0 disables automatic export')
    parser.add_argument('--client-every',type=int,default=20,help='Refresh client charts every N watch cycles; 0 disables it')
    parser.add_argument('--kind')
    parser.add_argument('--entity')
    args=parser.parse_args()
    if not 5<=args.interval<=3600:parser.error('Interval must be 5..3600 seconds')
    if min(args.cycles,args.report_every,args.client_every)<0:parser.error('Cycle counts must be nonnegative')
    store=SQLiteStore(args.sqlite) if args.sqlite else SqlServerStore(args.container)
    if args.action=='import-client':
        inserted=import_client(args,store)
        print(canonical(dict(inserted=inserted,counts=store.counts())))
    elif args.action=='import-publisher':
        inserted=batches(store,publisher_rows(args.publisher,args.work/'publisher-snapshots',
            {r['source'] for r in store.rows(kind='import.publisher_complete')}))
        print(canonical(dict(inserted=inserted,counts=store.counts())))
    elif args.action=='capture-once':print(canonical(capture_once(args,store)))
    elif args.action=='status':print(canonical(dict(backend='SQLite' if args.sqlite else 'SQL Server FourDevReference',counts=store.counts())))
    elif args.action=='entity':
        # Some source tables use an ordinal record identity while their typed ID is in the payload.
        for record in store.rows(kind=args.kind):
            payload=json.loads(record['payload'])
            identity=next((payload[k] for k in ('id','wID','wItemID','dwQuestID','quest_id') if k in payload),record['entity'])
            if args.entity is None or str(identity)==args.entity:print(canonical(record))
    elif args.action=='restore-source':
        if not args.entity:parser.error('--entity must be the 64-character source record key')
        data=store.read_blob(args.entity)
        if data is None:raise ValueError('Selected evidence record has no source blob')
        # An explicit export destination is required and existing files are preserved.
        if args.output==DEFAULT_WORK/'export':parser.error('Choose an explicit file with --output')
        args.output.parent.mkdir(parents=True,exist_ok=True)
        with args.output.open('xb') as file:file.write(data)
        print(canonical(dict(path=str(args.output),bytes=len(data),sha256=hashlib.sha256(data).hexdigest())))
    elif args.action=='export':
        data=report(store,args.output);print(canonical(dict(report=str(args.output/'report.html'),counts=data['counts'])))
    elif args.action=='watch':
        args.work.mkdir(parents=True,exist_ok=True)
        stop=args.work/'STOP';pid=args.work/'watcher.pid'
        if stop.exists():raise RuntimeError('STOP file exists; remove it explicitly before starting a new watch')
        if pid.exists():
            old=int(pid.read_text().strip())
            if process_alive(old):raise RuntimeError('A collector process is already running')
            pid.unlink()
        with pid.open('x',encoding='ascii') as file:file.write(str(os.getpid()))
        cycle=0
        try:
            while not stop.exists() and (args.cycles==0 or cycle<args.cycles):
                try:
                    print(canonical(capture_once(args,store)),flush=True)
                    if args.client_every>0 and cycle%args.client_every==0:
                        imported=import_client(args,store,include_code=False)
                        if imported:print(canonical(dict(client_records_added=imported)),flush=True)
                    if args.report_every>0 and cycle%args.report_every==0:
                        report(store,args.output)
                except Exception as exc:
                    # A failed cycle never advances packet watermarks. Existing SQL records survive.
                    print(canonical(dict(error=str(exc),time_utc=datetime.now(timezone.utc).isoformat())),flush=True)
                cycle+=1
                for _ in range(args.interval):
                    if stop.exists():break
                    time.sleep(1)
        finally:
            if pid.exists() and pid.read_text().strip()==str(os.getpid()):pid.unlink()


if __name__=='__main__':main()
