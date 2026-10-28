// Read-only inventory of every compiled UI control and its command bindings.
import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import {parseTif,writeTif,type TifNode} from '../UiEditor/src/tif/tif.ts';

const [input,output]=process.argv.slice(2);
if(!output||fs.existsSync(output))throw new Error('Usage: audit-ui.ts Game/TClientCmd.tif NEW-report.json');
const bytes=fs.readFileSync(input),doc=parseTif(new Uint8Array(bytes));
if(!Buffer.from(writeTif(doc)).equals(bytes))throw new Error('UI codec changed the input.');
const cs=new TextDecoder('windows-1250');
const root=new URL('../../',import.meta.url);
const ids=fs.readFileSync(new URL('Client/History Files/TClientID.h',root),'utf8');
const names=new Map<number,string[]>();
for(const match of ids.matchAll(/^#define\s+(GM_\w+)\s+\(?(0x[0-9A-Fa-f]+|\d+)\)?/gm)) {
  const id=Number(match[2]);names.set(id,[...names.get(id)??[],match[1]]);
}
const sources=fs.readdirSync(new URL('Client/TClient/',root)).filter(n=>n.endsWith('.cpp')).map(name=>({name,
  source:fs.readFileSync(new URL('Client/TClient/'+name,root),'utf8').replace(/\/\/[^\n]*|\/\*[\s\S]*?\*\//g,'') }));
const bindings=new Set(sources.flatMap(({source})=>[...source.matchAll(/ON_GM_COMMAND\(\s*(GM_\w+)\s*\)/g)].map(m=>m[1])));
const boundIds=new Set([...names].filter(([,ns])=>ns.some(n=>bindings.has(n))).map(([id])=>id));
let nodes=0,buttons=0,commandFields=0;
const commandIds=new Set<number>();
const unbound:object[]=[],texts=new Map<string,number>();
function walk(list:TifNode[],frame:number,shared?:number) {
  for(const node of list) {
    nodes++;if(node.type===1)buttons++;
    for(const field of ['text','tooltip'] as const) {
      const text=cs.decode(node.raw[field]).trim();if(text)texts.set(text,(texts.get(text)??0)+1);
    }
    node.menu.forEach((command,event)=>{
      if(command===0||command===0xFFFFFFFF)return;
      commandFields++;commandIds.add(command);
      if(!boundIds.has(command))unbound.push({frame,shared,control:node.id,event,command,
        symbols:names.get(command)??[],text:cs.decode(node.raw.text)});
    });
    if(node.shared===undefined)walk(node.children,frame,shared);
  }
}
doc.shared.forEach((list,i)=>walk(list,0,i));
doc.frames.forEach(frame=>walk([frame],frame.id));
const labelComparisons=sources.flatMap(({name,source})=>[...source.matchAll(/m_strText\s*(?:==|!=)\s*(?:"(?:\\.|[^"\\])*"|CTChart::Localize\()/g)]
  .map(m=>({file:name,expression:m[0]})));
const mismatchedHitTests=sources.flatMap(({name,source})=>source.split('\n').flatMap((line,i)=>{
  const hits=[...line.matchAll(/(\w+\[[^\]]+\]|\w+)->HitTest\(/g)].map(m=>m[1]);
  const visibility=[...line.matchAll(/(\w+\[[^\]]+\]|\w+)->IsVisible\(/g)].map(m=>m[1]);
  return hits.length===1&&visibility.length===1&&hits[0]!==visibility[0]?[{file:name,line:i+1,expression:line.trim()}]:[];
}));
const untranslatedDisplayLiterals=sources.flatMap(({name,source})=>{
  const pattern=/(?:m_strText\s*=\s*|m_strText\.Format\(\s*|(?:ShowInfoMsgBox|ShowMsgBox|MessageBoxOK|AddString|SetText|SetTitle)\(\s*)("(?:\\.|[^"\\])*")/g;
  return [...source.matchAll(pattern)].flatMap(match=>{
    let text:string;try{text=JSON.parse(match[1]);}catch{return [];}
    const words=text.replace(/%[-+#0 ]*(?:\d+|\*)?(?:\.(?:\d+|\*))?(?:I64|I32|hh|ll|[hljztLw])?[diuoxXfFeEgGaAcCsSpn%]/g,'');
    // Brand names and pure numeric/punctuation formats deliberately remain the same.
    return (/[A-Za-z]{2}|[^\x00-\x7F]/.test(words)&&!['4Story','Battle Royale'].includes(text))?[{file:name,text}]:[];
  });
});
const report={input:path.resolve(input),sha256:crypto.createHash('sha256').update(bytes).digest('hex'),
  frames:doc.frames.length,sharedLists:doc.shared.length,nodes,buttons,commandFields,uniqueCommands:commandIds.size,
  knownCommandIds:names.size,registeredCommands:bindings.size,sourceFiles:sources.length,
  byteIdenticalRoundTrip:true,labelComparisons,mismatchedHitTests,untranslatedDisplayLiterals,unboundCommandFields:unbound,
  texts:[...texts].map(([text,count])=>({text,count})),
  limits:'Static inventory. Shared/unused templates and commands replaced at runtime require context; this is not a graphical playthrough.'};
fs.writeFileSync(output,JSON.stringify(report,null,2),{flag:'wx'});
console.log(JSON.stringify({frames:report.frames,nodes,buttons,commandFields,uniqueCommands:commandIds.size,
  unboundCommandFields:unbound.length,labelComparisons:labelComparisons.length,mismatchedHitTests:mismatchedHitTests.length}));
