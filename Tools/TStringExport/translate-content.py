"""Fill missing game text locally; preserve data tokens and reuse the donor terminology."""
import collections, json, pathlib, re, sys, time
import ctranslate2
from transformers import MarianTokenizer

sys.stdout.reconfigure(encoding="utf-8")
plan_dir, model_dir, destination = map(pathlib.Path, sys.argv[1:4])
plan = json.loads((plan_dir / "plan.json").read_text(encoding="utf-8"))
pending = json.loads((plan_dir / "pending.json").read_text(encoding="utf-8"))
meta = json.loads((model_dir / "mt-model.json").read_text(encoding="utf-8"))
tokenizer = MarianTokenizer.from_pretrained(meta["Source"], local_files_only=True)
translator = ctranslate2.Translator(meta["Converted"], device="cpu", compute_type="int8", intra_threads=6)
protected = re.compile("|".join(map(re.escape, plan["Tokens"])) +
    r"|/[A-Za-z][A-Za-z0-9]*|\{[^}]*\}|<[^>]*>|\$[^$\r\n]+\$|%(?:[0-9.+\-]*(?:I64|I32|ll|hh|[hl])?[sdiufg])?|\\[nr]|\r\n|[\r\n]")
numbers = re.compile(r"\d+(?:[.,]\d+)?")
def signature(s): return protected.findall(s)
def quantities(s): return sorted(numbers.findall(protected.sub("", s)))

glossary_candidates = collections.defaultdict(set)
for f in plan["Fields"]:
    name = f["File"]
    is_entity = name == "Tcd/TItem.tcd" or "TNPC" in name or name == "Tcd/TMon.tcd"
    proper_npc = "TNPC" in name and f["Key"].endswith('/0')
    # A one-word object such as "Well" must not replace the conversational "Well, ...".
    explicit_entity = len(f['English'].split())>1 or proper_npc and f['Czech']==f['English']
    if is_entity and explicit_entity and f["Czech"] and not protected.search(f["English"]) and len(f["English"]) > 2 and '&&&' not in f['Czech']:
        glossary_candidates[f["English"]].add(f["Czech"])
glossary = {en: next(iter(cs)) for en, cs in glossary_candidates.items() if len(cs) == 1 and len(en) < 100}
# Ensure lore words unknown to a general-purpose model cannot silently disappear.
glossary.update({"gnoll": "gnol", "gnolls": "gnolové", "Rahu": "Rahu", "Iberia": "Iberie"})
entity_pattern = re.compile(r"(?<!\w)(?:" + "|".join(map(re.escape, sorted(glossary, key=len, reverse=True))) + r")(?!\w)")
all_protected = re.compile(protected.pattern + "|" + entity_pattern.pattern + r"|\d+(?:[.,]\d+)?")
past = {}
if destination.exists():
    for line in destination.read_text(encoding="utf-8").splitlines():
        value = json.loads(line)
        if '&&&' not in value['Czech'] and signature(value["English"])==signature(value["Czech"]) and quantities(value["English"])==quantities(value["Czech"]):
            past[value["English"]] = value

def encode(s): return tokenizer.convert_ids_to_tokens(tokenizer.encode(">>ces<< " + s))
def decode(result): return tokenizer.decode(tokenizer.convert_tokens_to_ids(result.hypotheses[0]), skip_special_tokens=True)
def normalize(s):
    return s.replace("\u202f", " ").replace("\u2212", "-").replace("\ufeff", "").replace("\u2011", "-")
def masked(text, entities=True):
    saved = []
    def protect(match):
        raw = match.group(); value = glossary.get(raw, raw)
        marker = "XQZ" + str(len(saved)) + "XQZ"; saved.append(value); return marker
    return (all_protected if entities else re.compile(protected.pattern+r"|\d+(?:[.,]\d+)?")).sub(protect, text), saved
def restore(text, saved):
    for i, value in enumerate(saved):
        pattern = r"X\s*Q\s*Z\s*" + r"\s*".join(str(i)) + r"\s*X\s*Q\s*Z"
        text, n = re.subn(pattern, lambda _: value, text, flags=re.I)
        if n != 1: raise ValueError("protected token missing/duplicated")
    if "XQZ" in text.upper(): raise ValueError("unresolved marker")
    return normalize(text)
def valid(en, cs):
    try: cs.encode("cp1250")
    except UnicodeEncodeError: return False
    return bool(cs.strip()) and signature(en) == signature(cs) and quantities(en) == quantities(cs)

def czech_number(number):
    n=int(number)
    small=['nula','jeden','dva','tři','čtyři','pět','šest','sedm','osm','devět','deset','jedenáct','dvanáct','třináct','čtrnáct','patnáct','šestnáct','sedmnáct','osmnáct','devatenáct']
    if n<20:return small[n]
    if n<100:return ['','','dvacet','třicet','čtyřicet','padesát','šedesát','sedmdesát','osmdesát','devadesát'][n//10]+(' '+small[n%10] if n%10 else '')
    if n<1000:return ['','sto','dvě stě','tři sta','čtyři sta','pět set','šest set','sedm set','osm set','devět set'][n//100]+(' '+czech_number(str(n%100)) if n%100 else '')
    if n<1000000:return ('' if n//1000==1 else czech_number(str(n//1000))+' ')+('tisíc' if n//1000==1 or n//1000>=5 else 'tisíce')+(' '+czech_number(str(n%1000)) if n%1000 else '')
    return number

def segmented(text, entities=True):
    # A conservative fallback: translate only literal spans between protected data.
    pattern=all_protected if entities else re.compile(protected.pattern+r"|\d+(?:[.,]\d+)?")
    matches = list(pattern.finditer(text)); chunks = []; spans = []; p = 0
    for match in matches:
        chunks.append(text[p:match.start()]); spans.append(glossary.get(match.group(),match.group())); p = match.end()
    chunks.append(text[p:])
    needs = [(i, s) for i, s in enumerate(chunks) if re.search(r"[A-Za-z]{2}", s)]
    if needs:
        results = translator.translate_batch([encode(s.strip()) for _, s in needs], beam_size=3,
            max_decoding_length=512, max_input_length=512)
        for (i, source), result in zip(needs, results):
            value = normalize(decode(result))
            # Number words sometimes become digits. Keep them as Czech words in literal spans.
            if re.search(r'\b\d{1,2}:\d{2}',value) and not re.search(r'\b\d{1,2}:\d{2}',source):
                raise ValueError('Unexpected timestamp in translation: '+repr(source))
            value = re.sub(r"\b\d+\b", lambda m: czech_number(m.group()), value)
            chunks[i] = (" " if source[:1].isspace() else "") + value + (" " if source[-1:].isspace() else "")
    result = "".join(chunk + (spans[i] if i < len(spans) else "") for i, chunk in enumerate(chunks))
    if not valid(text,result): raise ValueError("Segment fallback violated the source contract: " + repr(text) + " -> " + repr(result))
    return result

def sentences(text):
    parts=re.split(r'(?<=[.!?])([ \t]+)(?=[A-Z*"%])',text)
    translated=[]
    for i,part in enumerate(parts):
        if i%2 or not re.search('[A-Za-z]{2}',part): translated.append(part);continue
        value=None
        for entities,beam in [(True,3),(False,3),(False,1)]:
            source,saved=masked(part,entities)
            result=translator.translate_batch([encode(source)],beam_size=beam,max_decoding_length=512,max_input_length=512)[0]
            try:
                candidate=restore(decode(result),saved)
                if valid(part,candidate): value=candidate;break
            except ValueError:pass
        if value is None:value=segmented(part,False)
        translated.append(value)
    value=''.join(translated)
    if not valid(text,value):raise ValueError('Sentence translation violated the text contract: '+repr(text)+' -> '+repr(value))
    return value

todo = [v for v in pending if v["English"] not in past]
if '--sentences' in sys.argv[4:]:
    # OPUS is a sentence model. Translate paragraphs sentence by sentence so later sentences
    # cannot be silently omitted, while batching the sentences for efficient CPU inference.
    paragraphs={v['English']:re.split(r'(?<=[.!?])([ \t\r\n]+)(?=[A-Z*"%])',v['English'])
        for v in pending if any(u['File']=='TQuest.mpq' for u in v['Uses'])}
    paragraphs={en:parts for en,parts in paragraphs.items() if len(parts)>1}
    cache_path=destination.with_name('content-sentence-cache-v2.jsonl'); cache={}
    if cache_path.exists():
        for line in cache_path.read_text(encoding='utf-8').splitlines():
            v=json.loads(line)
            if valid(v['English'],v['Czech']):cache[v['English']]=v['Czech']
    units=sorted({s for parts in paragraphs.values() for i,s in enumerate(parts) if i%2==0 and re.search('[A-Za-z]{2}',s)}-cache.keys(),key=len)
    print(f'Sentence pass: {len(paragraphs)} paragraphs, {len(units)} unique sentences.',flush=True)
    started=time.time()
    with cache_path.open('a',encoding='utf-8',buffering=1) as output:
        for offset in range(0,len(units),64):
            batch=units[offset:offset+64];prepared=[masked(s) for s in batch]
            results=translator.translate_batch([encode(s) for s,_ in prepared],beam_size=3,max_decoding_length=512,max_input_length=512)
            for en,(_,saved),result in zip(batch,prepared,results):
                try:
                    cs=restore(decode(result),saved)
                    if not valid(en,cs):raise ValueError('contract')
                except ValueError:cs=sentences(en)
                cache[en]=cs;output.write(json.dumps({'English':en,'Czech':cs},ensure_ascii=False)+'\n')
            print(f'{min(offset+64,len(units))}/{len(units)} sentences; {time.time()-started:.1f}s',flush=True)
    with destination.open('a',encoding='utf-8',buffering=1) as output:
        for en,parts in paragraphs.items():
            cs=''.join(cache[s] if i%2==0 and re.search('[A-Za-z]{2}',s) else s for i,s in enumerate(parts))
            if not valid(en,cs):raise ValueError('Paragraph contract changed: '+repr(en))
            output.write(json.dumps({'English':en,'Czech':cs,'Method':'local OPUS-MT (sentence pass)'},ensure_ascii=False)+'\n')
    print('Sentence pass completed: every sentence retained, placeholders and quantities validated.',flush=True)
    sys.exit(0)
todo.sort(key=lambda v: len(v["English"]))
print(f"Local OPUS-MT: {len(todo)} unique texts; {len(past)} cached; {len(glossary)} glossary entries.", flush=True)
started = time.time(); fallback = 0
with destination.open("a",encoding="utf-8",buffering=1) as output:
    for offset in range(0,len(todo),32):
        batch = todo[offset:offset+32]
        prepared = [masked(v["English"]) for v in batch]
        inputs = [encode(s) for s,_ in prepared]
        if any(len(tokens)>500 for tokens in inputs):
            raise ValueError("Input exceeds the model's context; split at a sentence boundary before translating.")
        results = translator.translate_batch(inputs,beam_size=3,max_decoding_length=512,max_input_length=512)
        for row,(source,saved),result in zip(batch,prepared,results):
            en = row["English"]
            try:
                cs = restore(decode(result),saved)
                if not valid(en,cs): raise ValueError("validation")
            except ValueError:
                cs = sentences(en); fallback += 1
            value = {"English":en,"Czech":cs,"Method":"local OPUS-MT"}
            output.write(json.dumps(value,ensure_ascii=False)+"\n")
        print(f"{min(offset+32,len(todo))}/{len(todo)}; {time.time()-started:.1f}s; conservative fallbacks {fallback}",flush=True)
print("Completed local translation; all placeholders and numerical values validated.",flush=True)
