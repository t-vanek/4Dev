"""Validate conservation, original inputs, extracted hashes and decompiler outputs."""
import collections,csv,hashlib,json,pathlib,re,sys
from compare import ROOT,Reader,table,write_json

def verify(out):
    d=json.loads((out/'summary.json').read_text(encoding='utf8'));checks=[]
    for label,root in [('official',pathlib.Path(d['official'])),('ours',pathlib.Path(d['ours']))]:
        manifest=list(csv.DictReader((out/(label+'-files.csv')).open(encoding='utf-8-sig')))
        for row in manifest:
            p=root/row['path'];stat=p.stat()
            assert stat.st_size==int(row['size']) and stat.st_mtime_ns==int(row['mtime_ns']), 'Input changed: '+str(p)
        for relative in ['TClient.exe','TClientCmd.tif','TQuest.mpq','Tcd/TItem.tcd','Tcd/TSkill.tcd']:
            p=root/relative;expected=next(r['sha256']for r in manifest if r['path'].lower()==relative.lower())
            with p.open('rb')as source:actual=hashlib.file_digest(source,'sha256').hexdigest()
            assert actual==expected,'Input hash changed: '+str(p)
        checks.append(f'{label}: {len(manifest)} input size/mtime records unchanged; five principal files rehashed')
        dec=out/('decompilation-'+label);meta=json.loads((dec/'summary.json').read_text())
        functions=list(csv.DictReader((dec/'functions.tsv').open(encoding='utf8'),delimiter='\t'))
        counts=collections.Counter(x['status']for x in functions)
        assert counts['ok']==meta['completed'] and counts['failed']==meta['failed'] and counts['thunk']==meta['thunks']
        text=(dec/'decompiled.c').read_text(encoding='utf8')
        assert len(re.findall(r'/\* Address:',text))==meta['completed']
        assert 'Analysis succeeded' in (out/('ghidra-'+label+'.log')).read_text(encoding='utf8',errors='replace')
        checks.append(f'{label}: {meta["completed"]} pseudocode bodies and function statuses reconciled')
    for name,t in d['indices'].items():
        assert t['official']==t['shared']+len(t['official_only'])
        assert t['ours']==t['shared']+len(t['ours_only'])
        assert t['shared']==t['identical']+t['different']+t['shared_unavailable']
    for name,t in d['tables'].items():
        assert t['official']==t['shared']+len(t['official_only']) and t['ours']==t['shared']+len(t['ours_only'])
        assert t['official_metadata']['fully_consumed'] and t['ours_metadata']['fully_consumed']
    checks.append('All index/table ID counts reconcile; validated table files are fully consumed')
    manifest=list(csv.DictReader((out/'official-only-resources/manifest.csv').open(encoding='utf-8-sig')))
    for row in manifest:
        p=out/'official-only-resources'/row['extracted_file'];payload=p.read_bytes()
        assert len(payload)==int(row['bytes']) and hashlib.sha256(payload).hexdigest()==row['sha256']
        diff=list(csv.DictReader((out/'indices'/(row['index']+'.csv')).open(encoding='utf-8-sig')))
        expected=next(r for r in diff if r['id']==row['id'])
        assert expected['status']=='official_only' and expected['official_sha256']==row['sha256']
    checks.append(f'{len(manifest)} extracted records match manifest and comparison hashes')
    # An independent malformed-file check ensures truncated real records are rejected.
    data=(pathlib.Path(d['official'])/'Tcd/TItem.tcd').read_bytes()
    try:Reader(data).take(len(data)+1)
    except ValueError:pass
    else:raise AssertionError('Bounds check failed')
    from tempfile import TemporaryDirectory
    with TemporaryDirectory(dir=out)as temp:
        p=pathlib.Path(temp)/'TItem.tcd';p.write_bytes(data[:-1])
        try:table(p)
        except ValueError:pass
        else:raise AssertionError('Truncated real TItem archive accepted')
    checks.append('Bounds and truncated TItem archive rejected')
    markup=(out/'REPORT.html').read_text(encoding='utf8')
    for rel in re.findall(r'href="([^"]+)"',markup):assert (out/rel).exists(),'Broken report link: '+rel
    checks.append('All HTML report artifact links exist')
    write_json(out/'validation.json',dict(passed=True,checks=checks,known_parser_gaps=d['errors']))
    print('\n'.join(checks))

if __name__=='__main__':verify(pathlib.Path(sys.argv[1]).resolve())
