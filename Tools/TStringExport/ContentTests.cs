using System.Text;

static class ContentTests
{
    public static void Run()
    {
        string legacy=Encoding.GetEncoding(1252).GetString(Encoding.GetEncoding(949).GetBytes("관문 경비대장"));
        if(LegacyNpc.Translate(legacy,"20811/1")!="Velitel stráže brány")throw new Exception("Legacy NPC encoding/role regression.");
        ContentContract.Initialize(["/ItemNormal","/Grade"]);
        if(!ContentContract.Technical("/ItemSet/SetItemName") || !ContentContract.Technical("/UseTime"))throw new Exception("Legacy tokens must remain technical.");
        if(!ContentContract.Safe("Bring 12 objects to %s.\n/UseTime","Přines 12 předmětů pro %s.\n/UseTime"))throw new Exception("Valid contract rejected.");
        foreach(string bad in new[]{"Přines 13 předmětů pro %s.\n/UseTime","Přines 12 předmětů pro %u.\n/UseTime","Přines 12 předmětů pro %s. /UseTime"})
            if(ContentContract.Safe("Bring 12 objects to %s.\n/UseTime",bad))throw new Exception("Broken text contract accepted.");
        foreach(bool wide in new[]{false,true})
        {
            using var stream=new MemoryStream();using var w=new BinaryWriter(stream,Encoding.UTF8,true);
            w.Write((ushort)1);w.Write(71u);
            if(wide){w.Write((byte)255);w.Write((ushort)65534);w.Write((byte)4);w.Write(Encoding.Unicode.GetBytes("Gate"));}
            else{w.Write((byte)4);w.Write(Encoding.ASCII.GetBytes("Gate"));}
            w.Write(Enumerable.Range(0,55).Select(i=>(byte)i).ToArray());
            byte[] original=stream.ToArray();var a=ContentLocalizer.Parse("TRegion.tcd",original,Encoding.GetEncoding(1252));
            string text=string.Concat(Enumerable.Repeat("Přízračná brána ",30));
            byte[] result=a.Rewrite(new(){{"71/0",text}});var b=ContentLocalizer.Parse("TRegion.tcd",result,ContentLocalizer.Czech,a.Layout);
            if(b.Fields.Single().Text!=text||b.Fields.Single().Wide!=wide||!a.Rewrite([],true).SequenceEqual(b.Rewrite([],true)))throw new Exception("CString length/encoding/structure regression.");
            if(!a.Bytes.SequenceEqual(original))throw new Exception("Source was mutated.");
        }
        using(var stream=new MemoryStream())
        {
            using var w=new BinaryWriter(stream,Encoding.UTF8,true);
            void Text(string value){byte[] b=Encoding.ASCII.GetBytes(value);w.Write(b.Length);w.Write(b);}
            w.Write(0);w.Write(123u);w.Write((byte)5);w.Write(17u);w.Write((byte)1);
            for(int i=0;i<8;i++)Text(i==0?"Bring 12 mushrooms to %s":"");
            w.Write((byte)9);w.Write(1);w.Write((byte)2);w.Write(700u);w.Write((byte)12);Text("Mushroom");Text("Missing mushrooms");
            w.Write(1);w.Write((byte)1);w.Write(777u);w.Write((byte)2);w.Write((byte)1);w.Write((byte)100);w.Write(7u);w.Write(93u);
            byte[] bytes=stream.ToArray();var a=ContentLocalizer.Parse("TQuest.mpq",bytes,Encoding.GetEncoding(1252));
            var b=ContentLocalizer.Parse("TQuest.mpq",a.Rewrite(new(){{"quest:123/0","Přines 12 hub pro %s"}}),ContentLocalizer.Czech,a.Layout);
            if(!a.Rewrite([],true).SequenceEqual(b.Rewrite([],true)))throw new Exception("Quest IDs/terms/rewards changed.");
            foreach(byte[] invalid in new[]{bytes[..^1],new byte[]{0xff,0xff,0xff,0xff}})
            {
                bool rejected=false;try{ContentLocalizer.Parse("TQuest.mpq",invalid,ContentLocalizer.Czech,a.Layout);}catch(Exception e)when(e is InvalidDataException or EndOfStreamException){rejected=true;}
                if(!rejected)throw new Exception("Truncated/invalid quest archive accepted.");
            }
        }
        Console.WriteLine("Content codec tests passed: CP1250, UTF-16, long CString lengths, quest rewards, token/quantity preservation, malformed input.");
    }
}
