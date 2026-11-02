"""Decode passive Gameforge CZ captures with the hash-pinned original client.

No network traffic is sent. The capture should be a stable copy of a live file.
Unknown application messages remain opaque, even after wire decryption succeeds.
"""
import argparse
import bisect
import collections
import csv
from datetime import datetime, timezone
import hashlib
import html
import io
import json
from pathlib import Path
import struct
import subprocess
import sys
from zoneinfo import ZoneInfo

# Optional Windows zoneinfo data, kept isolated from the user's Python environment.
sys.path.insert(0, str(Path(__file__).resolve().parents[2]/'.runtime/capture-python'))

ANALYSIS_SHA256 = 'edd5e5c7abd6597c07c36d73429d551fe242ae1add981f20cc969dac80f1d243'
ORIGINAL_SHA256 = '320c557910c890b4c17b5311ab5c343fd4ee6d8fed3b5490db278fc5221111e1'
BODY_TABLE_VA, HEADER_TABLE_VA = 0x9fe758, 0x9fe7a0
BASE = 0x20c9
UTC = timezone.utc


def digest(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def write_json(path, value):
    Path(path).write_text(json.dumps(value, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')


def read_keys(path):
    """Read only the two tables from the already unpacked analysis PE, never execute it."""
    data = Path(path).read_bytes()
    if hashlib.sha256(data).hexdigest() != ANALYSIS_SHA256:
        raise ValueError('Unsupported analysis client hash; table addresses must not be reused across builds')
    nt = struct.unpack_from('<I', data, 0x3c)[0]
    if data[nt:nt+4] != b'PE\0\0':
        raise ValueError('Not a PE')
    section_count = struct.unpack_from('<H', data, nt+6)[0]
    optional_size = struct.unpack_from('<H', data, nt+20)[0]
    opt = nt+24
    if struct.unpack_from('<H', data, opt)[0] != 0x10b:
        raise ValueError('Expected x86 PE32')
    image_base = struct.unpack_from('<I', data, opt+28)[0]
    def at_va(va, size):
        rva = va-image_base
        for i in range(section_count):
            off = opt+optional_size+40*i
            virtual_size, virtual_addr, raw_size, raw_addr = struct.unpack_from('<IIII', data, off+8)
            if virtual_addr <= rva and rva+size <= virtual_addr+raw_size:
                return raw_addr+rva-virtual_addr
        raise ValueError(f'Unmapped table at {va:x}')
    return (struct.unpack_from('<9Q', data, at_va(BODY_TABLE_VA, 72)),
            struct.unpack_from('<9H', data, at_va(HEADER_TABLE_VA, 18)))


def decrypt_packet(packet, number, direction, body_keys, header_keys):
    """Reference: 69b180/69b320/6a5380/6a5570 in this exact analysis PE."""
    if len(packet) < 16 or struct.unpack_from('<H', packet)[0] != len(packet):
        raise ValueError('Packet size mismatch')
    index = (number + (3 if direction == 's2c' else 0)) % 9
    key, hkey = body_keys[index], header_keys[index]
    data = bytearray(packet)
    for off in (2, 4, 6):
        value = struct.unpack_from('<H', data, off)[0] ^ ((hkey+len(data)) & 65535)
        struct.pack_into('<H', data, off, value)
    ident, observed_number = struct.unpack_from('<HI', data, 2)
    if observed_number != number:
        raise ValueError(f'Application sequence mismatch, expected {number}, got {observed_number}')
    for off in (8, 10, 12, 14):
        value = struct.unpack_from('<H', data, off)[0] ^ ((hkey+ident) & 65535)
        struct.pack_into('<H', data, off, value)
    expected = struct.unpack_from('<Q', data, 8)[0]
    checksum, blocks = 0, (len(data)-16)//8
    for off in range(16, 16+blocks*8, 8):
        value = struct.unpack_from('<Q', data, off)[0] ^ key
        struct.pack_into('<Q', data, off, value)
        checksum ^= value
    key_bytes = struct.pack('<Q', key)
    for i in range((len(data)-16) % 8):
        off = 16+blocks*8+i
        data[off] ^= key_bytes[i]
        checksum ^= data[off]
    if checksum != expected:
        raise ValueError(f'Checksum mismatch at application sequence {number}')
    return ident, bytes(data[16:])


def reassemble(segments, syn_sequence):
    """Require contiguous captured TCP bytes, verify duplicates rather than filling gaps."""
    data, ranges, overlap_bytes = bytearray(), [], 0
    base = (syn_sequence+1) & 0xffffffff
    for segment in sorted(segments, key=lambda s: ((s['sequence']-base) & 0xffffffff, s['frame'])):
        position = (segment['sequence']-base) & 0xffffffff
        payload = segment['payload']
        if position > len(data):
            raise ValueError(f'TCP capture gap: {len(data)}..{position}, frame {segment["frame"]}')
        overlap = min(len(data)-position, len(payload))
        if data[position:position+overlap] != payload[:overlap]:
            raise ValueError(f'Conflicting TCP retransmission at frame {segment["frame"]}')
        overlap_bytes += overlap
        if overlap < len(payload):
            start = len(data)
            data.extend(payload[overlap:])
            ranges.append(dict(start=start, end=len(data), frame=segment['frame'], time=segment['time']))
    return bytes(data), ranges, overlap_bytes


def read_tcp(capture, tshark, server):
    fields = ['frame.number', 'frame.time_epoch', 'tcp.stream', 'ip.src', 'tcp.srcport',
              'ip.dst', 'tcp.dstport', 'tcp.seq_raw', 'tcp.flags.syn', 'tcp.payload']
    cmd = [str(tshark), '-n', '-r', str(capture), '-Y', f'ip.addr == {server} && (tcp.len > 0 || tcp.flags.syn == 1)', '-T', 'fields']
    for field in fields:
        cmd += ['-e', field]
    result = subprocess.run(cmd, capture_output=True, check=True, encoding='utf-8')
    streams = {}
    for row in csv.reader(io.StringIO(result.stdout), delimiter='\t'):
        if len(row) != len(fields):
            raise ValueError('Unexpected tshark field count')
        frame, ts, stream, src, sport, dst, dport, seq, syn, payload = row
        direction = 's2c' if src == server else 'c2s'
        state = streams.setdefault((int(stream), direction), dict(segments=[], syn=None,
            server_port=int(sport if direction == 's2c' else dport)))
        is_syn = syn.lower() in ('true', '1')
        if is_syn:
            state['syn'] = int(seq)
        if payload:
            state['segments'].append(dict(sequence=(int(seq)+is_syn) & 0xffffffff,
                frame=int(frame), time=float(ts), payload=bytes.fromhex(payload.replace(':', ''))))
    return streams


def parse_item(body, inventory=True):
    """Normal-world item block: original 4180c0 -> 527640/527660/527770."""
    off = int(inventory)
    if len(body) < 35+off:
        raise ValueError('Truncated item block')
    magic_count = body[34+off]
    if len(body) != 35+off+3*magic_count:
        raise ValueError('Item magic count does not consume the body exactly')
    item = dict(slot=body[off], template_id=struct.unpack_from('<H', body, off+1)[0],
        appearance_id=struct.unpack_from('<H', body, off+3)[0], level=body[off+5], count=body[off+6],
        durability_max=struct.unpack_from('<I', body, off+7)[0], durability=struct.unpack_from('<I', body, off+11)[0],
        refine_max=body[off+15], refine=body[off+16], grade_level=body[off+17],
        expiry=struct.unpack_from('<q', body, off+18)[0],
        extension_bytes=body[off+26:off+34].hex(), magic=[])
    if inventory:
        item['inventory'] = body[0]
    for i in range(magic_count):
        pos=35+off+3*i
        item['magic'].append(dict(id=body[pos], value=struct.unpack_from('<H', body, pos+1)[0]))
    return item


def semantic(ident, body, direction):
    """Only explicit, inspected handlers are interpreted. Other payloads stay opaque."""
    if direction == 'c2s':
        if ident == BASE+0x52 and len(body) == 9:
            quest, reward_type, reward_id = struct.unpack('<IBI', body)
            return dict(kind='quest_execute', quest_id=quest, reward_type=reward_type, reward_id=reward_id)
        if ident == BASE+0xd7 and len(body) == 4:
            npc, portal = struct.unpack('<HH', body)
            return dict(kind='teleport_request', npc_id=npc, portal_id=portal)
        if ident == BASE+0x84 and len(body) == 13:
            # Original 44cda0: WORD NPC, DWORD quest, DWORD item, three BYTEs.
            npc, quest, item, count, inventory, slot = struct.unpack('<HIIBBB', body)
            return dict(kind='npc_item_request', npc_id=npc, quest_id=quest, item_id=item, count=count,
                destination_inventory=inventory, destination_slot=slot)
        if ident == BASE+0x88 and len(body) == 4:
            return dict(kind='loot_open_request', monster_id=struct.unpack('<I', body)[0])
        return None
    if ident == BASE+0x50 and len(body) == 6:
        quest, qtype, auxiliary = struct.unpack('<IBB', body)
        return dict(kind='quest_added', quest_id=quest, quest_type=qtype, auxiliary_byte=auxiliary,
            auxiliary_semantics='Unproven; not interpreted as a completion cap')
    if ident == BASE+0x53 and len(body) == 11:
        quest, term, qtype, count, status = struct.unpack('<IIBBB', body)
        return dict(kind='quest_progress', quest_id=quest, term_id=term, term_type=qtype, count=count, status=status)
    if ident == BASE+0x5a:
        # Original 41f520: byte NPC count, WORD NPC, BYTE country/count,
        # then DWORD quest and BYTE condition for each offer.
        if not body:
            raise ValueError('Truncated quest offers')
        pos, npcs = 1, []
        for _ in range(body[0]):
            if pos+4 > len(body):
                raise ValueError('Truncated NPC quest offer header')
            npc, country, count = struct.unpack_from('<HBB', body, pos)
            pos += 4
            quests=[]
            for _ in range(count):
                if pos+5 > len(body):
                    raise ValueError('Truncated NPC quest entry')
                quest, condition = struct.unpack_from('<IB',body,pos)
                pos += 5
                quests.append(dict(quest_id=quest,condition=condition))
            npcs.append(dict(npc_id=npc,country=country,quests=quests))
        if pos != len(body):
            raise ValueError('Unexpected extra bytes in NPC quest offers')
        return dict(kind='npc_quest_offers',npcs=npcs)
    if ident == BASE+0x54 and len(body) == 14:
        result, quest, term, term_type, drop = struct.unpack('<BIIBI', body)
        return dict(kind='quest_complete', result=result, quest_id=quest, term_id=term, term_type=term_type, drop_id=drop)
    if ident == BASE+0x49 and len(body) == 12:
        gold, silver, copper = struct.unpack('<III', body)
        return dict(kind='money', gold=gold, silver=silver, copper=copper,
            copper_total=(gold*1000+silver)*1000+copper)
    if ident == BASE+0x24 and len(body) == 12:
        total, floor, ceiling = struct.unpack('<III', body)
        return dict(kind='experience', total=total, floor=floor, ceiling=ceiling)
    if ident == 0x2355 and len(body) == 12:
        capacity, total, source = struct.unpack('<III', body)
        return dict(kind='soul', capacity=capacity, total=total, source_id=source,
            handler='004388a0', semantics='Independent soul update, not the EXP packet')
    if ident in (BASE+0x2a, BASE+0x2b):
        return dict(kind='item_updated' if ident == BASE+0x2a else 'item_added', **parse_item(body))
    if ident == BASE+0x2c and len(body) == 2:
        return dict(kind='item_removed', inventory=body[0], slot=body[1])
    if ident == BASE+0x23 and len(body) == 5:
        char_id, level = struct.unpack('<IB', body)
        return dict(kind='level', char_id=char_id, level=level,handler='00416b40')
    if ident == BASE+0xd8 and len(body) == 24:
        result, actor, actor_type, distance, map_id, x, y, z = struct.unpack('<BIBIHfff', body)
        return dict(kind='teleport', result=result, actor_id=actor, actor_type=actor_type,
            range=distance, map_id=map_id, x=x, y=y, z=z)
    from reconstruction_protocol import semantic_world
    return semantic_world(ident,body,direction)


def decode(capture, client, output, tshark, server, include_wire=False, tolerate_semantic_errors=False):
    output.mkdir(parents=True, exist_ok=True)
    body_keys, header_keys = read_keys(client)
    streams = read_tcp(capture, tshark, server)
    metadata, packets = [], []
    for (stream_id, direction), state in sorted(streams.items()):
        if not state['segments']:
            continue
        meta = dict(stream=stream_id, direction=direction, server_port=state['server_port'])
        if state['syn'] is None:
            meta.update(status='skipped_mid_connection', reason='No captured SYN; starting application sequence unknown')
            metadata.append(meta)
            continue
        raw, ranges, duplicates = reassemble(state['segments'], state['syn'])
        ends = [r['end'] for r in ranges]
        off, number = 0, 1
        while off+16 <= len(raw):
            size=struct.unpack_from('<H', raw, off)[0]
            if size < 16:
                raise ValueError(f'Invalid frame size at {stream_id}/{direction}/{off}')
            if off+size > len(raw):
                break
            ident, body = decrypt_packet(raw[off:off+size], number, direction, body_keys, header_keys)
            timestamp = ranges[bisect.bisect_right(ends, off+size-1)]
            packets.append(dict(stream=stream_id, direction=direction, server_port=state['server_port'],
                sequence=number, id=ident, id_hex=f'0x{ident:04x}', offset=off, size=size,
                frame=timestamp['frame'], time=timestamp['time'], body_hex=body.hex()))
            if include_wire:
                packets[-1]['wire_hex']=raw[off:off+size].hex()
            number += 1
            off += size
        meta.update(status='checksum_and_sequence_verified', bytes=len(raw), packets=number-1,
            trailing_bytes=len(raw)-off, duplicate_bytes=duplicates)
        metadata.append(meta)
    packets.sort(key=lambda p: (p['time'], p['frame'], p['stream'], p['sequence']))
    events, inventory, previous = [], {}, {}
    for packet in packets:
        try:
            event = semantic(packet['id'], bytes.fromhex(packet['body_hex']), packet['direction'])
        except (ValueError,struct.error,IndexError) as exc:
            if not tolerate_semantic_errors:raise
            event=dict(kind='schema_gap',reason=str(exc),semantic_scope='uninterpreted',
                payload_preserved=True,original_message_id=packet['id_hex'])
        if event is None:
            continue
        event.update({k:packet[k] for k in ('stream','direction','sequence','id_hex','frame','time')})
        event['time_utc'] = datetime.fromtimestamp(event['time'], UTC).isoformat()
        # Keeping UTC as canonical avoids local/host timezone ambiguity.
        event['time_prague'] = datetime.fromtimestamp(event['time'], UTC).astimezone(ZoneInfo('Europe/Prague')).isoformat()
        key = event['stream']
        if event['kind'] in ('item_added','item_updated'):
            inventory[(key,event['inventory'],event['slot'])] = dict(event)
        elif event['kind'] == 'item_removed':
            item = inventory.pop((key,event['inventory'],event['slot']), None)
            if item:
                event['removed_template_id'] = item['template_id']
                event['removed_count'] = item['count']
                event['prior_add_sequence'] = item['sequence']
        elif event['kind'] in ('money','experience','soul'):
            prop = dict(money='copper_total', experience='total', soul='total')[event['kind']]
            prior = previous.get((key,event['kind']))
            if prior is not None:
                event['previous'] = prior
                event['delta'] = event[prop]-prior
            previous[(key,event['kind'])] = event[prop]
        events.append(event)
    transactions=[]
    for i, event in enumerate(events):
        if event['kind'] != 'quest_complete' or event['result'] != 0:
            continue
        request_index=next((j for j in range(i-1,-1,-1) if events[j]['stream']==event['stream'] and events[j]['kind']=='quest_execute'),None)
        if request_index is None:
            continue
        window=[e for e in events[request_index:i+1] if e['stream']==event['stream']]
        transactions.append(dict(quest_id=event['quest_id'], request_quest_id=events[request_index]['quest_id'],
            request_frame=events[request_index]['frame'], completion_frame=event['frame'],
            completion_sequence=event['sequence'], time_prague=event['time_prague'],
            money_deltas=[e.get('delta') for e in window if e['kind']=='money'],
            exp_deltas=[e.get('delta') for e in window if e['kind']=='experience'],
            soul_deltas=[e.get('delta') for e in window if e['kind']=='soul'],
            removed_items=[{k:e[k] for k in ('removed_template_id','removed_count','sequence','frame') if k in e} for e in window if e['kind']=='item_removed'],
            added_items=[dict(template_id=e['template_id'], count=e['count'], sequence=e['sequence'],frame=e['frame']) for e in window if e['kind']=='item_added'],
            attribution='Captured request-to-success interval; no server transaction ID is transmitted'))
    with (output/'packets.jsonl').open('w',encoding='utf8') as file:
        for packet in packets:
            file.write(json.dumps(packet)+'\n')
    write_json(output/'events.json',events)
    write_json(output/'quest-transactions.json',transactions)
    summary=dict(schema=1, captured_file=str(capture.resolve()), capture_sha256=digest(capture),
        analysis_client_sha256=digest(client), original_client_sha256=ORIGINAL_SHA256,
        algorithm_evidence=dict(body_decrypt='0069b180',header_decrypt='0069b320',client_send='006a5380',client_receive='006a5570',
            body_table_va=hex(BODY_TABLE_VA),header_table_va=hex(HEADER_TABLE_VA),key_count=9,rc4_layer=False),
        streams=metadata, decrypted_packets=len(packets), interpreted_events=len(events),
        event_counts=dict(collections.Counter(e['kind'] for e in events)),
        successful_quest_completions=[t['quest_id'] for t in transactions],
        limitations=['Only inspected application handlers are interpreted; other decrypted payloads remain opaque',
            'Capture begins during the login connection; that partial connection is skipped',
            'No drop probability, completion cap, daily reset or cross-restart persistence can be proven from this single journey',
            'No game server or client files were modified; this is a passive reference decode'])
    write_json(output/'summary.json',summary)
    render_report(output,summary,transactions,events)
    return summary


def render_report(output, summary, transactions, events):
    esc=html.escape
    lines=['<!doctype html><html lang="cs"><meta charset="utf-8"><title>Gameforge CZ — rozkódovaný průchod</title>',
        '<style>body{font:16px system-ui;max-width:1200px;margin:36px auto;padding:0 20px;color:#18202a}table{border-collapse:collapse;width:100%;margin:20px 0}th,td{text-align:left;padding:8px;border-bottom:1px solid #ccd2da}code{font-size:13px}small{color:#526070}</style>',
        '<h1>Gameforge CZ — rozkódovaný průchod</h1>',
        f'<p>{summary["decrypted_packets"]:,} dešifrovaných zpráv s ověřeným kontrolním součtem a pořadím; {summary["interpreted_events"]} významově zpracovaných událostí.</p>',
        '<p>Výsledky platí pro zachycený průchod a klienta s uvedeným otiskem. Pravděpodobnosti dropů, opakovatelnost a SQL persistence tím nejsou ověřené.</p>',
        '<h2>Dokončení questů</h2><table><tr><th>Quest</th><th>Akce dokončení</th><th>Čas Praha</th><th>Měď Δ</th><th>BZ Δ</th><th>Duše Δ</th><th>Odebrané předměty</th><th>Přidané předměty</th></tr>']
    for t in transactions:
        cells=[t['quest_id'],t['request_quest_id'],t['time_prague'][11:19],t['money_deltas'],t['exp_deltas'],t['soul_deltas'],
            [(i.get('removed_template_id'),i.get('removed_count')) for i in t['removed_items']],[(i['template_id'],i['count']) for i in t['added_items']]]
        lines.append('<tr>'+''.join('<td>'+esc(str(c))+'</td>' for c in cells)+'</tr>')
    lines+=['</table><h2>Ověření TCP proudů</h2><pre>'+esc(json.dumps(summary['streams'],ensure_ascii=False,indent=2))+'</pre>',
        '<h2>Časová osa interpretovaných událostí</h2><table><tr><th>Čas Praha</th><th>Rámec / zpráva</th><th>Událost</th><th>Data</th></tr>']
    for e in events:
        detail={k:v for k,v in e.items() if k not in ('time','time_utc','time_prague','stream','direction','sequence','id_hex','frame','kind')}
        lines.append('<tr><td>'+esc(e['time_prague'][11:23])+'</td><td>'+esc(f'{e["frame"]} / {e["direction"]} #{e["sequence"]}')+'</td><td>'+esc(e['kind'])+'</td><td><code>'+esc(json.dumps(detail,ensure_ascii=False))+'</code></td></tr>')
    lines+=['</table><p><a href="summary.json">Souhrn JSON</a> · <a href="events.json">Události JSON</a> · <a href="quest-transactions.json">Dokončení questů JSON</a></p>',
        '<small>SHA-256 záznamu: '+esc(summary['capture_sha256'])+'</small></html>']
    (output/'report.html').write_text('\n'.join(lines),encoding='utf8')


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--capture',type=Path,required=True)
    parser.add_argument('--client-analysis',type=Path,required=True)
    parser.add_argument('--output',type=Path,required=True)
    parser.add_argument('--tshark',type=Path,default=Path(r'C:\Program Files\Wireshark\tshark.exe'))
    parser.add_argument('--server',default='79.110.88.11')
    args=parser.parse_args()
    summary=decode(args.capture,args.client_analysis,args.output,args.tshark,args.server)
    print(json.dumps({k:summary[k] for k in ('decrypted_packets','interpreted_events','event_counts','successful_quest_completions')},ensure_ascii=False))


if __name__=='__main__':
    main()
