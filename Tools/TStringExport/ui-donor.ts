// Read-only comparison of the same frame/control IDs across the two UI versions.
import fs from 'node:fs';
import { parseTif, type TifNode } from '../UiEditor/src/tif/tif.ts';
const [source,donor,output]=process.argv.slice(2);
const a=parseTif(new Uint8Array(fs.readFileSync(source))),b=parseTif(new Uint8Array(fs.readFileSync(donor)));
const en=new TextDecoder('windows-1252'),cs=new TextDecoder('windows-1250');
const pairs:object[]=[],missing=new Map<string,number>(),all=new Map<string,number>();
function walk(ours:TifNode[],theirs:TifNode[],path:string) {
  const index=new Map<number,TifNode[]>();
  for(const n of theirs){const list=index.get(n.id)??[];list.push(n);index.set(n.id,list);}
  const counts=new Map<number,number>();
  for(const n of ours) {
    const occurrence=counts.get(n.id)??0;counts.set(n.id,occurrence+1);
    const d=index.get(n.id)?.[occurrence];
    for(const f of ['text','tooltip'] as const) {
      const english=en.decode(n.raw[f]);if(!/[A-Za-z]{2}/.test(english))continue;
      all.set(english,(all.get(english)??0)+1);
      const czech=d?.type===n.type?cs.decode(d.raw[f]):'';
      if(czech && czech!==english)pairs.push({English:english,Czech:czech,Path:path+'/'+n.id.toString(16),Field:f});
      else missing.set(english,(missing.get(english)??0)+1);
    }
    walk(n.children,d?.type===n.type?d.children:[],path+'/'+n.id.toString(16));
  }
}
walk(a.frames,b.frames,'');
fs.writeFileSync(output,JSON.stringify({Pairs:pairs,Missing:[...missing].map(([Text,Count])=>({Text,Count})),All:[...all].map(([Text,Count])=>({Text,Count}))},null,2),{flag:'wx'});
console.log(`Matched ${pairs.length} fields; ${all.size} unique English captions/tooltips, ${missing.size} without donor at same control ID.`);
