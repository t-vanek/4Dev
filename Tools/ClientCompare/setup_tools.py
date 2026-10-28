"""Download official, portable Ghidra and Temurin into ignored .runtime. No PATH changes."""
import concurrent.futures, hashlib, json, pathlib, urllib.request, zipfile

ROOT = pathlib.Path(__file__).resolve().parents[2]
DEST = ROOT / '.runtime/client-compare-tools'
DEST.mkdir(parents=True, exist_ok=True)

def get_json(url):
    return json.load(urllib.request.urlopen(urllib.request.Request(url, headers={'User-Agent': '4Dev-local-comparison'}), timeout=60))

def download_extract(spec):
    name, url, digest = spec
    target = DEST / name
    marker = DEST / (name + '.complete')
    if marker.exists():
        return json.loads(marker.read_text())
    print('Downloading ' + name, flush=True)
    with urllib.request.urlopen(url, timeout=120) as response, target.open('wb') as output:
        while chunk := response.read(1024 * 1024): output.write(chunk)
    actual = hashlib.file_digest(target.open('rb'), 'sha256').hexdigest()
    if actual != digest: raise ValueError('SHA256 mismatch: ' + name)
    print('Verified ' + name + '; extracting', flush=True)
    with zipfile.ZipFile(target) as archive:
        roots = sorted({x.filename.split('/')[0] for x in archive.infolist()})
        archive.extractall(DEST)
    data = dict(url=url, sha256=actual, roots=roots)
    marker.write_text(json.dumps(data, indent=2))
    print('Ready: ' + ', '.join(roots), flush=True)
    return data

gh = get_json('https://api.github.com/repos/NationalSecurityAgency/ghidra/releases/latest')
asset = next(a for a in gh['assets'] if a['name'].endswith('.zip'))
jdk = get_json('https://api.adoptium.net/v3/assets/latest/25/hotspot?architecture=x64&image_type=jdk&os=windows')[0]['binary']['package']
specs = [(asset['name'], asset['browser_download_url'], asset['digest'].split(':')[1]),
         (jdk['name'], jdk['link'], jdk['checksum'])]
with concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool:
    results = list(pool.map(download_extract, specs))
seven = get_json('https://api.github.com/repos/ip7z/7zip/releases/latest')
seven_asset = next(a for a in seven['assets'] if a['name'] == '7zr.exe')
seven_path = DEST / '7zr.exe'
if not seven_path.exists():
    with urllib.request.urlopen(seven_asset['browser_download_url'], timeout=60) as response:
        seven_path.write_bytes(response.read())
seven_digest = hashlib.sha256(seven_path.read_bytes()).hexdigest()
if seven_asset.get('digest') and seven_digest != seven_asset['digest'].split(':')[1]:
    raise ValueError('7zr.exe SHA256 mismatch')
results.append(dict(url=seven_asset['browser_download_url'], sha256=seven_digest))
(DEST / 'tool-provenance.json').write_text(json.dumps(results, indent=2))
