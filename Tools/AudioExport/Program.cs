using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

Console.OutputEncoding = new UTF8Encoding(false);
if(args.Length!=2){Console.Error.WriteLine("Usage: AudioExport <installed-client-folder> <NEW-output-folder>");return 2;}
string source=Path.GetFullPath(args[0]),output=Path.GetFullPath(args[1]);
if(Directory.Exists(output)||File.Exists(output))throw new IOException("Choose a new output folder; existing exports are never overwritten.");
if(output.StartsWith(source.TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new IOException("Output cannot be inside the installed original client.");
string dataRoot=Path.Combine(source,"Data"),indexPath=Path.Combine(source,"Index","TClientW.IDX");
byte[] indexBytes=File.ReadAllBytes(indexPath);
var files=new List<string>();var resources=new List<Resource>();
using(var reader=new BinaryReader(new MemoryStream(indexBytes)))
{
    int fileCount=reader.ReadInt32(),resourceCount=reader.ReadInt32();
    if(fileCount<0||fileCount>100000||resourceCount<0||resourceCount>1000000)throw new InvalidDataException("Invalid media index counts.");
    for(int i=0;i<fileCount;i++)
    {
        int length=reader.ReadInt32();if(length<=0||length>32768)throw new InvalidDataException("Invalid media path length.");
        byte[] name=reader.ReadBytes(length);if(name.Length!=length)throw new EndOfStreamException();
        files.Add(Encoding.Latin1.GetString(name));
    }
    for(int i=0;i<resourceCount;i++)
    {
        uint id=reader.ReadUInt32();int file=reader.ReadInt32();uint flags=reader.ReadUInt32();
        if(file<0||file>=files.Count)throw new InvalidDataException("Invalid file reference.");
        // This is a media-type/volume word, NOT a byte offset into a packed file.
        resources.Add(new(id,files[file],(ushort)(flags&65535),(ushort)(flags>>16)));
    }
    if(reader.BaseStream.Position!=reader.BaseStream.Length)throw new InvalidDataException("Trailing media index bytes.");
}
string Resolve(string name)
{
    string path=Path.GetFullPath(Path.Combine(dataRoot,name.Replace('\\',Path.DirectorySeparatorChar)));
    if(!path.StartsWith(dataRoot+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Media path leaves the source data folder.");
    return path;
}
// Validate all paths before creating output. Include loose/unreferenced audio as well.
var sources=resources.Select(r=>Resolve(r.File)).Concat(Directory.EnumerateFiles(Path.Combine(dataRoot,"Media"),"*",SearchOption.AllDirectories))
    .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p=>p,StringComparer.OrdinalIgnoreCase).ToArray();
var missing=resources.Where(r=>!File.Exists(Resolve(r.File))).ToArray();
Directory.CreateDirectory(output);Directory.CreateDirectory(Path.Combine(output,"Music"));Directory.CreateDirectory(Path.Combine(output,"Sounds"));
var exported=new List<Export>();var used=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
foreach(string path in sources.Where(File.Exists))
{
    byte[] bytes=File.ReadAllBytes(path);string extension=AudioExtension(bytes);
    string folder=extension==".mp3"?"Music":"Sounds";
    string relative=folder+"/"+Path.GetFileNameWithoutExtension(path)+extension;
    if(!used.Add(relative))throw new InvalidDataException("Duplicate export filename: "+relative);
    string target=Path.Combine(output,relative);string hash=Convert.ToHexString(SHA256.HashData(bytes));
    using(var stream=new FileStream(target,FileMode.CreateNew,FileAccess.Write,FileShare.None))stream.Write(bytes);
    if(!SHA256.HashData(File.ReadAllBytes(target)).AsSpan().SequenceEqual(SHA256.HashData(bytes)))throw new IOException("Export verification failed: "+relative);
    // Catch the original launcher patching assets while this export runs.
    if(!SHA256.HashData(File.ReadAllBytes(path)).AsSpan().SequenceEqual(SHA256.HashData(bytes)))throw new IOException("Source changed during export: "+path);
    var aliases=resources.Where(r=>string.Equals(Resolve(r.File),path,StringComparison.OrdinalIgnoreCase)).ToArray();
    exported.Add(new(relative,Path.GetRelativePath(source,path).Replace('\\','/'),bytes.LongLength,hash,extension[1..],WaveInfo(bytes),aliases));
}
if(!File.ReadAllBytes(indexPath).AsSpan().SequenceEqual(indexBytes))throw new IOException("Media index changed during export.");
var json=new JsonSerializerOptions{WriteIndented=true,Encoder=System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping};
File.WriteAllText(Path.Combine(output,"manifest.json"),JsonSerializer.Serialize(new{
    Source=source,CreatedUtc=DateTimeOffset.UtcNow,IndexSha256=Convert.ToHexString(SHA256.HashData(indexBytes)),
    Files=exported.Count,Music=exported.Count(f=>f.Codec=="mp3"),Sounds=exported.Count(f=>f.Codec=="wav"),
    Bytes=exported.Sum(f=>f.Bytes),BitExactCopies=true,MissingResources=missing,Audio=exported},json));
File.WriteAllText(Path.Combine(output,"missing-resources.json"),JsonSerializer.Serialize(missing,json));
foreach(string folder in new[]{"Music","Sounds"})
{
    var playlist=new List<string>{"#EXTM3U"};
    foreach(var file in exported.Where(f=>f.File.StartsWith(folder+"/"))){playlist.Add("#EXTINF:-1,"+Path.GetFileNameWithoutExtension(file.File));playlist.Add(file.File);}
    File.WriteAllLines(Path.Combine(output,folder.ToLowerInvariant()+".m3u8"),playlist,new UTF8Encoding(false));
}
File.WriteAllText(Path.Combine(output,"README.txt"),$"Zvuky z originálního 4Story\n\nMusic/: {exported.Count(f=>f.Codec=="mp3")} původních MP3 skladeb.\nSounds/: {exported.Count(f=>f.Codec=="wav")} původních WAV souborů.\n\nSoubory .TMD obsahovaly běžná audio data. Export je přesnou kopií se správnou příponou; žádná rekomprese ani změna hlasitosti.\nHerní ID, typ média, původní hlasitost, cesty a SHA-256 jsou v manifest.json.\nPro přehrávání lze otevřít music.m3u8 nebo sounds.m3u8.\n\nV indexu bylo {missing.Length} odkazů na soubory, které v instalaci chybí. Jsou uvedeny v missing-resources.json.\nOriginální instalace i náš klient zůstaly tímto exportem nezměněné.\n",new UTF8Encoding(false));
Console.WriteLine($"Exported {exported.Count} byte-verified audio files: {exported.Count(f=>f.Codec=="wav")} WAV + {exported.Count(f=>f.Codec=="mp3")} MP3; {exported.Sum(f=>f.Bytes):N0} bytes.");
Console.WriteLine($"Missing source references: {missing.Length}. Output: {output}");
return 0;

static string AudioExtension(byte[] bytes)
{
    if(bytes.Length>=12&&bytes.AsSpan(0,4).SequenceEqual("RIFF"u8)&&bytes.AsSpan(8,4).SequenceEqual("WAVE"u8))return ".wav";
    if(bytes.Length>=3&&bytes.AsSpan(0,3).SequenceEqual("ID3"u8))return ".mp3";
    if(bytes.Length>=4&&bytes[0]==255&&(bytes[1]&0xe0)==0xe0&&((bytes[1]>>1)&3)==1)return ".mp3";
    throw new InvalidDataException("Unsupported audio signature; refusing to guess a file extension.");
}
static WaveFormat? WaveInfo(byte[] bytes)
{
    if(bytes.Length<12||!bytes.AsSpan(0,4).SequenceEqual("RIFF"u8))return null;
    ushort tag=0,channels=0,bits=0;uint rate=0,byteRate=0;long data=0;uint? samples=null;
    for(int offset=12;offset<=bytes.Length-8;)
    {
        uint length=BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset+4));
        int start=offset+8;if(length>(uint)(bytes.Length-start))throw new InvalidDataException("WAV chunk exceeds its file.");
        if(bytes.AsSpan(offset,4).SequenceEqual("fmt "u8)&&length>=16)
        {
            tag=BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(start));channels=BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(start+2));
            rate=BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(start+4));byteRate=BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(start+8));bits=BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(start+14));
        }
        if(bytes.AsSpan(offset,4).SequenceEqual("fact"u8)&&length>=4)samples=BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(start));
        if(bytes.AsSpan(offset,4).SequenceEqual("data"u8))data+=length;
        long next=(long)start+length+(length&1);if(next>bytes.Length)break;offset=(int)next;
    }
    if(channels==0||rate==0||data==0)throw new InvalidDataException("Incomplete WAV header/data.");
    double? seconds=samples is { } count?(double)count/rate:byteRate>0?(double)data/byteRate:null;
    return new(tag,channels,rate,bits,seconds);
}
sealed record Resource(uint Id,string File,ushort MediaType,ushort Volume);
sealed record WaveFormat(ushort FormatTag,ushort Channels,uint SampleRate,ushort BitsPerSample,double? Seconds);
sealed record Export(string File,string Source,long Bytes,string Sha256,string Codec,WaveFormat? Wave,Resource[] Resources);
