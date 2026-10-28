// Translate only matching captions and tooltips; preserve the complete UI structure and binary fields.
// The existing UI editor codec retains raw bytes. This script supplies CP1250 bytes explicitly.
import fs from 'node:fs';
import { parseTif, writeTif, type TifNode } from '../UiEditor/src/tif/tif.ts';

const [source, dictionaryFile, destination, reportFile] = process.argv.slice(2);
if (!reportFile) throw new Error('Usage: localize-ui.ts source.tif ui-dictionary.json new-output.tif report.json');
if (fs.existsSync(destination) || fs.existsSync(reportFile)) throw new Error('Output files already exist.');
const bytes = new Uint8Array(fs.readFileSync(source));
const document = parseTif(bytes);
if (!Buffer.from(writeTif(document)).equals(bytes)) throw new Error('UI codec failed a byte-identical round trip.');
const originalShape = shape(document);
const dictionary: { English: string; Czech: string }[] = JSON.parse(fs.readFileSync(dictionaryFile, 'utf8'));
const candidates = new Map<string, Set<string>>();
const normalize = (s: string) => s.replaceAll('\\n', '\n').replaceAll('\r\n', '\n').trim();
for (const pair of dictionary) {
  if (pair.English === pair.Czech) continue;
  const key = normalize(pair.English);
  const values = candidates.get(key) ?? new Set<string>();
  values.add(normalize(pair.Czech)); candidates.set(key, values);
}
const translations = new Map<string, string>();
for (const [key, values] of candidates) if (values.size === 1) translations.set(key, [...values][0]);
// Captions in the UI resource are independent of TString; these short labels have no string ID.
for (const [en, cs] of Object.entries({
  'Name': 'Jméno', 'Password': 'Heslo', 'Log-in': 'Přihlásit', 'Login': 'Přihlásit',
  'Create Account': 'Vytvořit účet', 'Quit': 'Ukončit', 'Security Code': 'Ověřovací kód',
  'Security code': 'Ověřovací kód', 'Confirm': 'Potvrdit',
})) translations.set(en, cs);
const cp1252 = new TextDecoder('windows-1252');
const cp1250 = new TextDecoder('windows-1250');
const encoding = new Map<string, number>();
for (let byte = 0; byte < 256; byte++) {
  const character = cp1250.decode(Uint8Array.of(byte));
  if (!encoding.has(character)) encoding.set(character, byte);
}
function encode(text: string) {
  return Uint8Array.from([...text].map(character => {
    const byte = encoding.get(character);
    if (byte === undefined) throw new Error(`Not representable in CP1250: ${character}`);
    return byte;
  }));
}
const changed: { id: number; field: string; english: string; czech: string }[] = [];
const unchanged = new Map<string,number>();
function translate(list: TifNode[]) {
  for (const node of list) {
    for (const field of ['text', 'tooltip'] as const) {
      const english = cp1252.decode(node.raw[field]);
      const czech = translations.get(normalize(english));
      if (!czech || czech === english) {
        if (/[A-Za-z]{2}/.test(english)) unchanged.set(english,(unchanged.get(english)??0)+1);
        continue;
      }
      node[field] = czech;
      node.raw[field] = encode(czech);
      node.raw[field === 'text' ? 'textStr' : 'tooltipStr'] = czech;
      changed.push({ id: node.id, field, english, czech });
    }
    if (node.shared === undefined) translate(node.children);
  }
}
document.shared.forEach(translate); translate(document.frames);
const result = writeTif(document);
// Read it back and verify that all dimensions, IDs, commands, images, fonts and bytes other than
// text fields stayed identical. The codec decodes CP949; inspect its retained raw text as CP1250.
const reread = parseTif(result);
function shape(doc: typeof document) {
  function nodes(list: TifNode[]): unknown[] {
    return list.map(({ text, tooltip, raw, children, ...rest }) => ({
      ...rest, children: rest.shared === undefined ? nodes(children) : undefined,
    }));
  }
  return JSON.stringify({ shared: doc.shared.map(nodes), frames: nodes(doc.frames), fonts: doc.fonts });
}
if (originalShape !== shape(reread)) throw new Error('UI structure changed.');
if (!Buffer.from(writeTif(reread)).equals(Buffer.from(result))) throw new Error('Localized UI did not round-trip.');
let verified = 0;
function verify(a: TifNode[], b: TifNode[]) {
  for (let i = 0; i < a.length; i++) {
    for (const field of ['text', 'tooltip'] as const)
      if (!Buffer.from(a[i].raw[field]).equals(Buffer.from(b[i].raw[field]))) throw new Error('Text bytes changed.');
    if (a[i].shared === undefined) verify(a[i].children, b[i].children);
    verified++;
  }
}
document.shared.forEach((list, i) => verify(list, reread.shared[i])); verify(document.frames, reread.frames);
fs.writeFileSync(destination, result, { flag: 'wx' });
fs.writeFileSync(reportFile, JSON.stringify({ translatedFields: changed.length, verifiedNodes: verified,
  structurePreserved: true, codePage: 1250, changes: changed,
  unchanged: [...unchanged].map(([text,count])=>({text,count})) }, null, 2), { flag: 'wx' });
console.log(`Localized ${changed.length} UI captions/tooltips; verified ${verified} nodes with unchanged structure.`);
