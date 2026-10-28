using System.Text;
using System.Text.RegularExpressions;

sealed record ContentField(string Key, int Start, int End, string Text, bool IntLength, bool Wide);
sealed class ContentDocument(byte[] bytes, List<ContentField> fields, string layout)
{
    public byte[] Bytes = bytes;
    public List<ContentField> Fields = fields;
    public string Layout = layout;
    public byte[] Rewrite(Dictionary<string, string> replacements, bool mask = false)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        int position = 0;
        foreach (var field in Fields.OrderBy(f => f.Start))
        {
            writer.Write(Bytes.AsSpan(position, field.Start - position));
            if (!mask)
            {
                if (!replacements.TryGetValue(field.Key, out var text)) writer.Write(Bytes.AsSpan(field.Start, field.End - field.Start));
                else
                {
                    byte[] encoded = (field.Wide ? Encoding.Unicode : ContentLocalizer.Czech).GetBytes(text);
                    if (field.IntLength) writer.Write(encoded.Length);
                    else
                    {
                        if (field.Wide) { writer.Write((byte)255); writer.Write((ushort)65534); }
                        int length = field.Wide ? encoded.Length / 2 : encoded.Length;
                        if (length < 255) writer.Write((byte)length);
                        else if (length < 65534) { writer.Write((byte)255); writer.Write((ushort)length); }
                        else { writer.Write((byte)255); writer.Write((ushort)65535); writer.Write((uint)length); }
                    }
                    writer.Write(encoded);
                }
            }
            position = field.End;
        }
        writer.Write(Bytes.AsSpan(position));
        return stream.ToArray();
    }
}

static class ContentLocalizer
{
    public static readonly Encoding Czech = Encoding.GetEncoding(1250, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    public static ContentDocument Parse(string name,byte[] data,Encoding encoding,string? forced=null)
    {
        if(name.Equals("TQuest.mpq",StringComparison.OrdinalIgnoreCase))
        {
            if(forced is not null) return Quest(data,encoding,forced=="quest-retail");
            try { return Quest(data,encoding,false); }
            catch(Exception e) when(e is InvalidDataException or EndOfStreamException or ArgumentOutOfRangeException or OverflowException) { return Quest(data,encoding,true); }
        }
        if(name.Equals("TInfo.tcd",StringComparison.OrdinalIgnoreCase))
        {
            var c=new Cursor(data,encoding); int n=c.U16();
            for(int i=0;i<n;i++){uint id=c.U32();int count=c.U8();for(int j=0;j<count;j++)c.String($"{id}/{j}");}
            return c.Done("detail");
        }
        int prefix,strings,suffix,keySize=2;bool keyAfter=false;bool help=false;
        switch(name.ToLowerInvariant())
        {
            case "titem.tcd":prefix=6;strings=1;suffix=103;break;
            case "tmon.tcd":prefix=4;strings=2;suffix=125;break;
            case "tskill.tcd":
                // Our patched client serializes name before ID; the retail client uses ID before name.
                try { return Skill(data,encoding,0,166); }
                catch(Exception e) when(e is InvalidDataException or EndOfStreamException or ArgumentOutOfRangeException or OverflowException) { return Skill(data,encoding,2,144); }
            case "tregion.tcd":prefix=4;strings=1;suffix=55;keySize=4;break;
            case "tportal.tcd":prefix=2;strings=1;suffix=14;break;
            case "tchartitle.tcd":prefix=12;strings=4;suffix=0;break;
            case "tchartitlegroup.tcd":prefix=1;strings=1;suffix=0;keySize=1;break;
            case "tmonthrank.tcd":prefix=1;strings=1;suffix=2;keySize=1;break;
            case "thelp.tcd":prefix=8;strings=2;suffix=0;keySize=4;help=true;break;
            case "titemmagic.tcd":prefix=0;strings=1;suffix=8;keyAfter=true;break;
            case "tgodtower.tcd":prefix=0;strings=1;suffix=28;keySize=4;keyAfter=true;break;
            case "tgodball.tcd":prefix=0;strings=1;suffix=20;keySize=4;keyAfter=true;break;
            case "tmonshop.tcd":prefix=0;strings=1;suffix=12;keySize=4;keyAfter=true;break;
            case "tportalregion.tcd":prefix=2;strings=1;suffix=0;break;
            case "tnpcinfo.tcd":prefix=20;strings=2;suffix=0;keySize=4;break;
            case "tcountryinfo.tcd":case "tclassinfo.tcd":case "traceinfo.tcd":case "tdynamich.tcd":case "tauctiontree.tcd":
                prefix=0;strings=1;suffix=4;keySize=4;keyAfter=true;break;
            case "tmenuitem.tcd":prefix=0;strings=1;suffix=9;keySize=4;keyAfter=true;break;
            default:
                if(name.Equals("TNPCGlobal.tcd",StringComparison.OrdinalIgnoreCase)||Regex.IsMatch(name,@"^TNPC[0-9a-fA-F]{8}\.tcd$")){prefix=6;strings=2;suffix=75;keySize=4;}
                else throw new InvalidDataException("Unknown content schema: "+name);
                break;
        }
        if(forced?.StartsWith("fixed:")==true)suffix=int.Parse(forced[6..]);
        // These are the two observed NPC layouts. Never guess offsets from arbitrary binary bytes.
        bool worldNpc=name.Equals("TNPCGlobal.tcd",StringComparison.OrdinalIgnoreCase)||Regex.IsMatch(name,@"^TNPC[0-9a-fA-F]{8}\.tcd$");
        IEnumerable<int> trials=worldNpc?new[]{75,67}:new[]{suffix};
        if(forced is not null) trials=[suffix];
        foreach(int tail in trials)
        {
            try
            {
                var c=new Cursor(data,encoding);int n=c.U16();var ids=new HashSet<string>();
                for(int i=0;i<n;i++)
                {
                    int start=c.P;uint id=keyAfter?0:c.PeekKey(keySize);c.Skip(prefix);
                    int fieldStart=c.Fields.Count;for(int j=0;j<strings;j++)c.String($"pending/{j}");
                    if(keyAfter)id=c.PeekKey(keySize);
                    string key=help?id+":"+BitConverter.ToUInt16(data,start+4):id.ToString();
                    if(!ids.Add(key) && !name.StartsWith("TNPC",StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Duplicate row key.");
                    for(int j=fieldStart;j<c.Fields.Count;j++)c.Fields[j]=c.Fields[j] with {Key=key+"/"+(j-fieldStart)};
                    c.Skip(tail);
                }
                return c.Done("fixed:"+tail);
            }
            catch(Exception e)when(e is EndOfStreamException or InvalidDataException or ArgumentOutOfRangeException or OverflowException or IndexOutOfRangeException){}
        }
        throw new InvalidDataException("No validated record layout for "+name);
    }

    static ContentDocument Skill(byte[] data,Encoding encoding,int prefix,int suffix)
    {
        var c=new Cursor(data,encoding);int n=c.U16();var ids=new HashSet<uint>();
        for(int i=0;i<n;i++)
        {
            uint id=prefix==2?c.U16():0u;c.String("pending");if(prefix==0)id=c.PeekKey(2);
            if(!ids.Add(id))throw new InvalidDataException("Duplicate skill ID.");
            c.Fields[^1]=c.Fields[^1] with {Key=id+"/0"};c.Skip(suffix);
        }
        return c.Done($"skill:{prefix}:{suffix}");
    }

    static ContentDocument Quest(byte[] data,Encoding encoding,bool retail)
    {
        var c=new Cursor(data,encoding);int classes=c.I32();if(classes<0||classes>10000)throw new InvalidDataException("Invalid quest classes.");
        for(int i=0;i<classes;i++){uint id=c.U32();c.String("class:"+id,true);c.Skip(1);}
        while(c.P<data.Length)
        {
            // Retail adds a DWORD before every quest and one byte after the main-quest flag.
            if(retail)c.Skip(4);
            uint id=c.U32();c.Skip(retail?7:6);
            for(int j=0;j<8;j++)c.String($"quest:{id}/{j}",true);
            c.Skip(1);int terms=c.I32();if(terms<0||terms>1000)throw new InvalidDataException("Invalid quest terms.");
            for(int j=0;j<terms;j++){int type=c.U8();uint term=c.U32();c.Skip(1);c.String($"quest:{id}/term:{type}:{term}/0",true);c.String($"quest:{id}/term:{type}:{term}/1",true);}
            int rewards=c.I32();if(rewards<0||rewards>1000)throw new InvalidDataException("Invalid quest rewards.");c.Skip(rewards*8+8);
        }
        return c.Done(retail?"quest-retail":"quest");
    }

    sealed class Cursor(byte[] bytes,Encoding encoding)
    {
        public int P;public List<ContentField> Fields=[];
        public void Skip(int n){if(n<0||P>bytes.Length-n)throw new EndOfStreamException();P+=n;}
        public int U8(){int p=P;Skip(1);return bytes[p];}
        public ushort U16(){int p=P;Skip(2);return BitConverter.ToUInt16(bytes,p);}
        public uint U32(){int p=P;Skip(4);return BitConverter.ToUInt32(bytes,p);}
        public int I32()=>unchecked((int)U32());
        public uint PeekKey(int size){if(P>bytes.Length-size)throw new EndOfStreamException();return size==1?bytes[P]:size==2?BitConverter.ToUInt16(bytes,P):BitConverter.ToUInt32(bytes,P);}
        public void String(string key,bool intLength=false)
        {
            int start=P;bool wide=false;int length;
            if(intLength)length=I32();
            else{length=U8();if(length==255){length=U16();if(length==65534){wide=true;length=U8();if(length==255)length=U16();}if(length==65535)length=I32();}}
            if(length<0||length>1000000)throw new InvalidDataException("Invalid string length.");
            int count=checked(length*(wide?2:1));int textStart=P;Skip(count);
            string value=(wide?Encoding.Unicode:encoding).GetString(bytes,textStart,count);
            Fields.Add(new ContentField(key,start,P,value,intLength,wide));
        }
        public ContentDocument Done(string layout){if(P!=bytes.Length)throw new InvalidDataException("Unexpected trailing bytes.");return new(bytes,Fields,layout);}
    }
}
