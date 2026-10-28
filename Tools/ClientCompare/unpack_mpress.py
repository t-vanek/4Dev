"""Static MPRESS 2.1x/LZMA extraction for analysis, without executing the input.

The layout is checked against the local x86 loader instructions. Reference for
format interpretation: https://github.com/avast/retdec/tree/master/src/unpackertool/plugins/mpress
The generated PE is an analysis artifact, not a replacement game executable.
"""
import argparse, hashlib, json, pathlib, struct, subprocess, sys

ROOT = pathlib.Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / '.runtime/client-compare-tools/pylib'))
import pefile

def align(value, boundary=4096): return (value + boundary - 1) & -boundary

def unpack(source, output):
    output.mkdir(parents=True, exist_ok=True)
    pe = pefile.PE(str(source))
    assert pe.FILE_HEADER.Machine == 0x14c
    assert pe.sections[0].Name.rstrip(b'\0') == b'.MPRESS1'
    ep = pe.OPTIONAL_HEADER.AddressOfEntryPoint
    assert pe.get_data(ep, 7) == bytes.fromhex('60e80000000058')
    pointer = ep + 6 + 0xb5a
    payload_rva = pointer + struct.unpack('<i', pe.get_data(pointer, 4))[0]
    assert payload_rva == pe.sections[0].VirtualAddress
    packed = pe.sections[0].get_data()
    expected = struct.unpack_from('<H', packed)[0] << 12
    packed_size = struct.unpack_from('<I', packed, 2)[0]
    pb, lp, lc = packed[6] >> 4, packed[6] & 15, packed[7]
    assert pb <= 4 and lp <= 4 and lc <= 8 and packed_size <= len(packed) - 6
    archive = output / 'official-payload.lzma'
    archive.write_bytes(bytes([(pb * 5 + lp) * 9 + lc]) + struct.pack('<IQ', 1 << 24, expected) + packed[8:6 + packed_size])
    decoder = ROOT / '.runtime/client-compare-tools/7zr.exe'
    result = subprocess.run([str(decoder), 'x', '-so', str(archive)], capture_output=True, check=True)
    assert len(result.stdout) == expected, 'Incomplete decompression'
    body = bytearray(result.stdout)
    # Invert the packer's E8/E9 target transform, as implemented by its loader.
    position, limit, corrected = 0, len(body) - 4096, 0
    while position < limit:
        opcode = body[position]; position += 1
        if opcode & 254 != 232: continue
        field = position; target = struct.unpack_from('<i', body, field)[0]; position += 4
        if target >= 0:
            if target >= limit: continue
        else:
            target += field
            if target < 0: continue
            target += limit
        struct.pack_into('<I', body, field, (target - field) & 0xffffffff)
        corrected += 1
    base_rva = pe.sections[0].VirtualAddress
    jump_field = ep + 0xb5c
    stub_rva = jump_field + 4 + struct.unpack('<i', pe.get_data(jump_field, 4))[0]
    stub = stub_rva - base_rva
    assert body[stub + 7] == 0x35 and body[stub + 0x123] == 0xe9
    hints = stub + 0x128 + struct.unpack_from('<i', body, stub + 0x128)[0]
    original_ep = base_rva + stub + 0x128 + struct.unpack_from('<i', body, stub + 0x124)[0]
    imports, cursor, destination = [], hints, base_rva + hints
    def cstring():
        nonlocal cursor
        end = body.index(0, cursor)
        value = body[cursor:end].decode('ascii'); cursor = end + 1
        return value
    while (delta := struct.unpack_from('<i', body, cursor)[0]) != -1:
        destination += delta; cursor += 4
        dll = cstring(); symbols = []
        while body[cursor]:
            if body[cursor] <= 32:
                symbols.append(struct.unpack_from('<H', body, cursor + 1)[0]); cursor += 3
            else: symbols.append(cstring())
        cursor += 1
        assert 0 <= destination - base_rva < len(body) - 4 * len(symbols)
        imports.append(dict(dll=dll, iat_rva=destination, symbols=symbols))
        destination += 4 * len(symbols)
    rsrc = pe.sections[2]
    imports_rva = align(rsrc.VirtualAddress + rsrc.Misc_VirtualSize)
    idata = bytearray(20 * (len(imports) + 1))
    def append(data):
        off = len(idata); idata.extend(data)
        return imports_rva + off
    for index, imp in enumerate(imports):
        dll_rva = append(imp['dll'].encode() + b'\0')
        if len(idata) % 2: idata.append(0)
        entries = []
        for symbol in imp['symbols']:
            if isinstance(symbol, int): entries.append(symbol | 0x80000000)
            else:
                entries.append(append(b'\0\0' + symbol.encode() + b'\0'))
                if len(idata) % 2: idata.append(0)
        idata.extend(b'\0' * (-len(idata) % 4))
        thunk = b''.join(struct.pack('<I', x) for x in entries + [0])
        ilt_rva = append(thunk)
        struct.pack_into('<5I', idata, index * 20, ilt_rva, 0, 0, dll_rva, imp['iat_rva'])
        at = imp['iat_rva'] - base_rva
        body[at:at + len(thunk)] = thunk
    # Preserve decoded code/data and resources, and replace loader-only imports.
    header_size = 512
    header = bytearray(source.read_bytes()[:header_size])
    opt = pe.OPTIONAL_HEADER.get_file_offset()
    sect = pe.sections[0].get_file_offset()
    struct.pack_into('<I', header, opt + 16, original_ep)
    struct.pack_into('<I', header, opt + 56, align(imports_rva + len(idata)))
    struct.pack_into('<I', header, opt + 60, header_size)
    struct.pack_into('<I', header, opt + 64, 0)
    struct.pack_into('<H', header, opt + 70, pe.OPTIONAL_HEADER.DllCharacteristics & ~0x40)
    for i in range(16): struct.pack_into('<II', header, opt + 96 + i * 8, 0, 0)
    struct.pack_into('<II', header, opt + 96 + 8, imports_rva, len(idata))
    resource_dir = pe.OPTIONAL_HEADER.DATA_DIRECTORY[2]
    struct.pack_into('<II', header, opt + 96 + 16, resource_dir.VirtualAddress, resource_dir.Size)
    low = min(x['iat_rva'] for x in imports)
    high = max(x['iat_rva'] + 4 * (len(x['symbols']) + 1) for x in imports)
    struct.pack_into('<II', header, opt + 96 + 12 * 8, low, high - low)
    chunks, offset = [], header_size
    for i, (name, va, data, flags) in enumerate([
        (b'.unpack', base_rva, body, 0xe0000060),
        (b'.rsrc', rsrc.VirtualAddress, rsrc.get_data(), 0x40000040),
        (b'.idata', imports_rva, idata, 0xc0000040),
    ]):
        raw_size = align(len(data), 512)
        struct.pack_into('<8s6I2HI', header, sect + i * 40, name.ljust(8, b'\0'), len(data), va, raw_size, offset, 0, 0, 0, 0, flags)
        chunks.append(bytes(data) + b'\0' * (raw_size - len(data))); offset += raw_size
    target = output / 'TClient.official.analysis.exe'
    target.write_bytes(header + b''.join(chunks))
    verified = pefile.PE(str(target))
    assert sum(len(x.imports) for x in verified.DIRECTORY_ENTRY_IMPORT) == sum(len(x['symbols']) for x in imports)
    assert verified.get_data(original_ep, 16) == body[original_ep - base_rva:original_ep - base_rva + 16]
    metadata = dict(source=str(source.resolve()), source_sha256=hashlib.sha256(source.read_bytes()).hexdigest(),
                    output=str(target.resolve()), output_sha256=hashlib.sha256(target.read_bytes()).hexdigest(),
                    execution=False, purpose='Static analysis only; not validated as runnable',
                    decompressed_bytes=expected, lzma=dict(pb=pb, lp=lp, lc=lc), corrected_calls_jumps=corrected,
                    original_entry_rva=hex(original_ep), fix_stub_rva=hex(stub_rva), imports=imports,
                    limitations=['Original section names and boundaries are lost; .unpack contains both code and data.',
                                 'Relocations and original debug information are not restored.',
                                 'The MPRESS loader remains in decoded bytes but is not the analysis entry point.'])
    (output / 'unpack.json').write_text(json.dumps(metadata, indent=2), encoding='utf-8')
    print(json.dumps({k: v for k, v in metadata.items() if k != 'imports'}, indent=2))

if __name__ == '__main__':
    p = argparse.ArgumentParser(); p.add_argument('source', type=pathlib.Path); p.add_argument('output', type=pathlib.Path)
    args = p.parse_args(); unpack(args.source, args.output)
