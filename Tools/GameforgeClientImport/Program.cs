// Validate imported records with the established target-layout resource readers and walk their references.
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using ClassicPort;

string root=Path.GetFullPath(args[0]);
using var doc=JsonDocument.Parse(File.ReadAllText(Path.Combine(root,"gameforge-import.json")));
var output=new List<object>();var roots=new List<(Kind,uint)>();
var kindOf=new Dictionary<string,Kind>(StringComparer.OrdinalIgnoreCase){["TClientO.IDX"]=Kind.Obj,["TClientM.IDX"]=Kind.Mesh,
 ["TClientA.IDX"]=Kind.Ani,["TClientX.IDX"]=Kind.Sfx,["TClientW.IDX"]=Kind.Media};
foreach(var catalogue in doc.RootElement.GetProperty("resources").EnumerateArray())
{
 string name=Path.GetFileName(catalogue.GetProperty("path").GetString()!);
 var idx=Idx.Load(Path.Combine(root,"Index",name));var entries=catalogue.GetProperty("added").EnumerateArray().ToArray();
 byte[] pack=File.ReadAllBytes(Path.Combine(root,catalogue.GetProperty("pack").GetString()!));
 foreach(var entry in entries)
 {
  uint id=entry.GetProperty("id").GetUInt32();var actual=idx.ById[id];
  if(actual.Pos!=entry.GetProperty("offset").GetUInt32())throw new InvalidDataException("Index position mismatch");
  byte[] stored=pack.AsSpan((int)actual.Pos,entry.GetProperty("bytes").GetInt32()).ToArray();
  if(Convert.ToHexString(SHA256.HashData(stored))!=entry.GetProperty("stored_sha256").GetString()!.ToUpperInvariant())throw new InvalidDataException("Stored source bytes changed");
  bool texture=name.EndsWith("TClientS.IDX",StringComparison.OrdinalIgnoreCase);
  byte[] raw=texture || name is "TClientX.IDX" or "TClientI.IDX"?Client.Unchunk(pack,(int)actual.Pos,out _):stored;
  if(Convert.ToHexString(SHA256.HashData(raw))!=entry.GetProperty("sha256").GetString()!.ToUpperInvariant())throw new InvalidDataException("Decoded source bytes changed");
  if(texture)Texture(raw);
  else if(name=="TClientM.IDX")Mesh(raw);
  else if(name=="TClientA.IDX")Animation(raw);
  else if(name=="TClientI.IDX")Image(raw);
  if(kindOf.TryGetValue(name,out var kind))roots.Add((kind,id));
 }
 output.Add(new {index=name,checkedRecords=entries.Length,storedAndDecodedHashesVerified=true});
}

var target=new Client(root,"2_TClientS.IDX",isClassic:false);
var tables=doc.RootElement.GetProperty("tables").EnumerateArray().ToArray();
HashSet<ushort> Added(string file)=>tables.Where(t=>Path.GetFileName(t.GetProperty("path").GetString())==file)
 .SelectMany(t=>t.GetProperty("added_ids").EnumerateArray()).Select(x=>x.GetUInt16()).ToHashSet();
var newMonsters=Added("TMon.tcd");int zeroModels=0;
foreach(var mon in TMon.Load(Path.Combine(root,"Tcd/TMon.tcd"),TMon.OurTail).Where(m=>newMonsters.Contains(m.Id)))
{if(mon.Obj!=0)roots.Add((Kind.Obj,mon.Obj));else zeroModels++;}
var newNpc=Added("TNPCTemp.tcd");byte[] npcs=File.ReadAllBytes(Path.Combine(root,"Tcd/TNPCTemp.tcd"));
for(int p=2;p<npcs.Length;p+=54)if(newNpc.Contains(BitConverter.ToUInt16(npcs,p)))roots.Add((Kind.Obj,BitConverter.ToUInt32(npcs,p+2)));
var visited=new HashSet<(Kind,uint)>();var absent=new List<object>();var stack=new Stack<(Kind,uint)>(roots);
while(stack.TryPop(out var key))
{
 if(key.Item2==0 || !visited.Add(key))continue;
 if(!target.Has(key.Item1,key.Item2)){absent.Add(new {kind=key.Item1.ToString(),id=key.Item2});continue;}
 if(key.Item1==Kind.Media)continue;
 var record=target.Get(key.Item1,key.Item2)!;
 foreach(var dependency in record.Refs)stack.Push(dependency);
}
var result=new {manifestSha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(root,"gameforge-import.json")))).ToLowerInvariant(),resourceChecks=output,graphNodes=visited.Count,unresolved=absent,originalMonsterPlaceholdersWithoutModel=zeroModels,
 note="Missing references must be classified against the original/baseline before deployment. A loaded chart is not proof of server quest activation."};
File.WriteAllText(args[1],JsonSerializer.Serialize(result,new JsonSerializerOptions{WriteIndented=true}));
Console.WriteLine(JsonSerializer.Serialize(new {validatedResourceCatalogues=output.Count,graphNodes=visited.Count,unresolved=absent.Count,originalMonsterPlaceholdersWithoutModel=zeroModels}));

static BinaryReader Reader(byte[] data)=>new(new MemoryStream(data));
static int Count(BinaryReader r){int n=r.ReadInt32();if(n<0||n>1000000)throw new InvalidDataException("Invalid record count");return n;}
static void Skip(BinaryReader r,long size){if(size<0 || size>r.BaseStream.Length-r.BaseStream.Position)throw new EndOfStreamException();r.BaseStream.Position+=size;}
static void End(BinaryReader r){if(r.BaseStream.Position!=r.BaseStream.Length)throw new InvalidDataException($"Trailing bytes: {r.BaseStream.Length-r.BaseStream.Position}");}
static void Mesh(byte[] data)
{
 using var r=Reader(data);int meshes=Count(r),nodes=Count(r);r.ReadByte();int levels=Count(r);
 if(levels<1||levels>100)throw new InvalidDataException("Invalid mesh detail levels");
 Skip(r,16L+nodes*64L);int vertices=Count(r);Skip(r,vertices*(nodes>0?48L:40L));
 for(int i=0;i<meshes*levels;i++){int buffers=Count(r);for(int j=0;j<buffers;j++){int indices=Count(r);r.ReadUInt16();for(int k=0;k<indices;k++)if(r.ReadUInt16()>=vertices)throw new InvalidDataException("Mesh vertex index out of bounds");}}
 Skip(r,4L*(levels-1));End(r);
}
static void Animation(byte[] data)
{
 using var r=Reader(data);Skip(r,Count(r)*5L);Skip(r,Count(r)*44L);Skip(r,9);Skip(r,Count(r));End(r);
}
static void Image(byte[] data)
{
 using var r=Reader(data);Skip(r,Count(r)*4L);Skip(r,Count(r)*8L);Skip(r,5);Skip(r,Count(r));End(r);
}
static void Texture(byte[] data)
{
 using var r=Reader(data);Skip(r,Count(r)*4L);Skip(r,Count(r)*24L);Skip(r,14);
 int expanded=Count(r),packed=Count(r);byte[] blob=r.ReadBytes(packed);if(blob.Length!=packed)throw new EndOfStreamException();End(r);
 if(packed>0){using var z=new ZLibStream(new MemoryStream(blob),CompressionMode.Decompress);using var output=new MemoryStream();z.CopyTo(output);if(output.Length!=expanded)throw new InvalidDataException("Texture payload length mismatch");}
}
