using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

sealed record TranslationFile(string File, string SourcePath, string Sha256, string Layout);
sealed record TranslationField(string File, string Key, string English, string? Czech, string Method);
sealed record TranslationPlan(string[] Tokens, List<TranslationFile> Files, List<TranslationField> Fields);
sealed record TranslatedText(string English, string Czech, string Method);

static class ContentTranslation
{
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    static readonly Encoding English = Encoding.GetEncoding(1252);
    public static void Prepare(string game, string donor, string output)
    {
        if (Directory.Exists(output)) throw new IOException("Choose a new output directory.");
        var tokens = new[] { "Client/TClient/TDetailInfo.cpp", "Client/TClient/TDetailInfo.h" }
            .SelectMany(path => Regex.Matches(File.ReadAllText(path), "\"(/[^\"\\r\\n]+)\""))
            .SelectMany(m => Regex.Matches(m.Groups[1].Value, @"/[A-Za-z][A-Za-z0-9]*"))
            .Select(m => m.Value).Distinct().OrderByDescending(t => t.Length).ToArray();
        ContentContract.Initialize(tokens);
        string[] tables = ["TItem", "TMon", "TSkill", "TInfo", "TRegion", "TPortal", "TCharTitle", "TCharTitleGroup", "TMonthRank", "TItemMagic", "TGodTower", "TGodBall", "TMonShop", "TPortalRegion"];
        var names = tables.Select(n => "Tcd/" + n + ".tcd").ToList();
        names.Add("Tcd/TNPCINFO.tcd");
        names.AddRange(Directory.EnumerateFiles(Path.Combine(game, "Tcd"), "TNPC*.tcd")
            .Where(p => Regex.IsMatch(Path.GetFileName(p), @"^TNPC(?:Global|[0-9a-fA-F]{8})\.tcd$"))
            .Select(p => "Tcd/" + Path.GetFileName(p)));
        names.Add("TQuest.mpq");
        var files = new List<TranslationFile>();
        var originals = new Dictionary<string, ContentDocument>();
        var donors = new Dictionary<string, ContentDocument>();
        foreach (string name in names.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string source = Path.GetFullPath(Path.Combine(game, name));
            if (!File.Exists(source) || new FileInfo(source).Length == 0) continue;
            var doc = Unique(ContentLocalizer.Parse(Path.GetFileName(name), File.ReadAllBytes(source), English));
            if (!doc.Rewrite([]).AsSpan().SequenceEqual(doc.Bytes)) throw new InvalidDataException("Source round-trip failed: " + name);
            originals[name] = doc;
            files.Add(new(name, source, Convert.ToHexString(SHA256.HashData(doc.Bytes)), doc.Layout));
            string from = Path.Combine(donor, name);
            if (File.Exists(from) && new FileInfo(from).Length != 0)
                donors[name] = Unique(ContentLocalizer.Parse(Path.GetFileName(name), File.ReadAllBytes(from), ContentLocalizer.Czech));
        }
        var custom = new Dictionary<string, HashSet<uint>>(StringComparer.OrdinalIgnoreCase);
        if(originals["Tcd/TItem.tcd"].Fields.Single(f=>f.Key=="1/0").Text!="Gold")
            throw new InvalidDataException("Expected original English content. Use the verified English backup, not already localized charts.");
        foreach (string marker in Directory.EnumerateFiles(Path.Combine(game,"Tcd"),"*.applied"))
            foreach (string line in File.ReadLines(marker))
                if (Regex.Match(line,@"^(item|monster)=(\d+)$") is { Success:true } m)
                {
                    string table = m.Groups[1].Value=="item"?"Tcd/TItem.tcd":"Tcd/TMon.tcd";
                    if (!custom.TryGetValue(table,out var set)) custom[table]=set=[];
                    set.Add(uint.Parse(m.Groups[2].Value));
                }
        // Imported item descriptions can reuse retail IDs for unrelated assets.
        custom["Tcd/TInfo.tcd"] = [];
        foreach (var field in originals["Tcd/TItem.tcd"].Fields)
            if (custom.TryGetValue("Tcd/TItem.tcd",out var ids) && ids.Contains(uint.Parse(field.Key.Split('/')[0])))
                custom["Tcd/TInfo.tcd"].Add(BitConverter.ToUInt32(originals["Tcd/TItem.tcd"].Bytes,field.End+60));
        var fields = new List<TranslationField>();
        foreach (var (name,doc) in originals)
        {
            donors.TryGetValue(name,out var translated);
            var byKey = translated?.Fields.ToDictionary(f=>f.Key) ?? [];
            var counts = doc.Fields.GroupBy(f=>f.Key.Split('/')[0]).ToDictionary(g=>g.Key,g=>g.Count());
            var donorCounts = translated?.Fields.GroupBy(f=>f.Key.Split('/')[0]).ToDictionary(g=>g.Key,g=>g.Count()) ?? [];
            foreach (var field in doc.Fields)
            {
                string row = field.Key.Split('/')[0];
                bool ported = uint.TryParse(row,out uint id) && custom.TryGetValue(name,out var ids) && ids.Contains(id);
                string? cs = !ported && byKey.TryGetValue(field.Key,out var candidate) ? candidate.Text : null;
                string method = "Gameforge";
                if (ContentContract.Technical(field.Text)) { cs=field.Text;method="technical"; }
                else if (cs==field.Text) method="donor invariant";
                else if (string.IsNullOrWhiteSpace(cs) || cs=="_" || cs.Contains("&&&") || ContentContract.Technical(cs) || !ContentContract.Safe(field.Text,cs)
                    || name=="Tcd/TInfo.tcd" && counts[row]!=donorCounts.GetValueOrDefault(row)) { cs=null;method=ported?"custom asset":"missing translation"; }
                fields.Add(new(name,field.Key,field.Text,cs,method));
            }
        }
        var memory = fields.Where(f=>f.Czech is not null && f.Method=="Gameforge")
            .GroupBy(f=>f.English).Select(g=>new { English=g.Key, Choices=g.GroupBy(f=>f.Czech!).Select(c=>new{Czech=c.Key,Count=c.Count()}).OrderByDescending(c=>c.Count).ToArray() })
            .Where(g=>g.Choices.Length==1 || g.Choices[0].Count>=5 && g.Choices[0].Count>=g.Choices.Sum(c=>c.Count)*0.9)
            .ToDictionary(g=>g.English,g=>g.Choices[0].Czech);
        var overrides = JsonSerializer.Deserialize<Dictionary<string,string>>(File.ReadAllText("Tools/TStringExport/ui-overrides.json"))!;
        foreach(var pair in JsonSerializer.Deserialize<Dictionary<string,string>>(File.ReadAllText("Tools/TStringExport/content-overrides.json"))!) overrides[pair.Key]=pair.Value;
        foreach(var pair in JsonSerializer.Deserialize<Dictionary<string,string>>(File.ReadAllText("Tools/TStringExport/dialog-overrides.json"))!) overrides[pair.Key]=pair.Value;
        // Fixed captions are translated explicitly in code, separately from network/player text.
        string chart = File.ReadAllText("Lib/Own/TChart/TChart/TChart.cpp");
        foreach (Match match in Regex.Matches(chart,"m_strNAME\\s*=\\s*(?:(?:CTChart::)?Localize\\()?\\s*(\"(?:\\\\.|[^\"\\\\])*\")"))
        {
            string text = JsonSerializer.Deserialize<string>(match.Groups[1].Value)!;
            fields.Add(new("runtime","name:"+fields.Count,text,null,"fixed client caption"));
        }
        foreach (string text in new[]{"Give Up","Accept","Next","Run","Complete","Normal Quest","Repeatable Quest","Event Quest","Lv %s %s","Title : %s","Companion Transformation Rune (","Special Refines : %d/4","Formula of Transformation"})
            fields.Add(new("runtime","caption:"+fields.Count,text,null,"fixed client caption"));
        fields=fields.Select(f=>overrides.TryGetValue(f.English,out var manual)?f with{Czech=manual,Method="reviewed"}
            : (f.Czech is null || f.File=="Tcd/TInfo.tcd") && memory.TryGetValue(f.English,out var reused) && ContentContract.Safe(f.English,reused)?f with{Czech=reused,Method="translation memory"}:f).ToList();
        fields=fields.Select(f=>Regex.IsMatch(f.English,@"^(?:/Item(?:Normal|Grade|Magic|Rare))?Gameforge item \d+")
            ? f with{Czech=Regex.Replace(f.English,"Gameforge item (\\d+)","Předmět Gameforge $1"),Method="reviewed pattern"}:f).ToList();
        fields=fields.Select(f=>ContentNames.Translate(f.English) is { } translated?f with{Czech=translated,Method="reviewed name"}:f).ToList();
        // Older map copies still store Korean names. Resolve the same NPC's stable ID from the
        // localized copies, rather than interpreting Korean bytes as Western-European text.
        bool Encodable(string s){try{ContentLocalizer.Czech.GetBytes(s);return true;}catch(EncoderFallbackException){return false;}}
        var npcById=fields.Where(f=>f.File.Contains("TNPC",StringComparison.OrdinalIgnoreCase)&&f.Czech is not null&&Encodable(f.Czech)&&!f.Czech.Contains("&&&"))
            .GroupBy(f=>f.Key).Where(g=>g.Select(f=>f.Czech).Distinct().Count()==1)
            .ToDictionary(g=>g.Key,g=>g.First().Czech!);
        fields=fields.Select(f=>f.File.Contains("TNPC",StringComparison.OrdinalIgnoreCase)&&!Encodable(f.English)&&npcById.TryGetValue(f.Key,out var caption)&&ContentContract.Safe(f.English,caption)
            ?f with{Czech=caption,Method="canonical NPC ID"}:f).ToList();
        fields=fields.Select(f=>f.File.Contains("TNPC",StringComparison.OrdinalIgnoreCase)&&!Encodable(f.Czech??f.English)&&LegacyNpc.Translate(f.English,f.Key) is { } legacy
            ?f with{Czech=legacy,Method="legacy NPC localization"}:f).ToList();
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output,"plan.json"),JsonSerializer.Serialize(new TranslationPlan(tokens,files,fields),Json));
        var pending=fields.Where(f=>f.Czech is null).GroupBy(f=>f.English).Select(g=>new{English=g.Key,Uses=g.Select(f=>new{f.File,f.Key}).ToArray()}).ToArray();
        File.WriteAllText(Path.Combine(output,"pending.json"),JsonSerializer.Serialize(pending,Json));
        File.WriteAllText(Path.Combine(output,"memory.json"),JsonSerializer.Serialize(memory,Json));
        File.WriteAllText(Path.Combine(output,"invariants.json"),JsonSerializer.Serialize(fields.Where(f=>f.Method=="donor invariant").GroupBy(f=>f.English).Select(g=>new{English=g.Key,Uses=g.Select(f=>new{f.File,f.Key})}),Json));
        Console.WriteLine($"Parsed {files.Count} files, {fields.Count} fields; {pending.Length} distinct texts require translation.");
    }

    public static void Apply(string planPath,string translationsPath,string output)
    {
        if(Directory.Exists(output))throw new IOException("Choose a new output directory.");
        var plan=JsonSerializer.Deserialize<TranslationPlan>(File.ReadAllText(planPath))!;
        ContentContract.Initialize(plan.Tokens);
        var additional=File.ReadLines(translationsPath).Where(s=>!string.IsNullOrWhiteSpace(s)).Select(s=>JsonSerializer.Deserialize<TranslatedText>(s)!).GroupBy(t=>t.English).ToDictionary(g=>g.Key,g=>g.Last());
        var fields=plan.Fields.Select(f=>f.Czech is null && additional.TryGetValue(f.English,out var t)?f with{Czech=t.Czech,Method=t.Method}:f).ToList();
        var dialogues=JsonSerializer.Deserialize<Dictionary<string,string>>(File.ReadAllText("Tools/TStringExport/dialog-overrides.json"))!;
        fields=fields.Select(f=>dialogues.TryGetValue(f.English,out var text)?f with{Czech=text,Method="reviewed dialogue"}:f).ToList();
        fields=fields.Select(f=>ContentNames.Translate(f.English) is { } name?f with{Czech=name,Method="reviewed name"}:f).ToList();
        var itemNames=fields.Where(f=>f.File=="Tcd/TItem.tcd"&&f.Czech is not null).GroupBy(f=>f.English)
            .Where(g=>g.Select(f=>f.Czech).Distinct().Count()==1).ToDictionary(g=>g.Key,g=>g.First().Czech!);
        fields=fields.Select(f=>
        {
            if(f.File!="Tcd/TInfo.tcd")return f;
            var match=Regex.Match(f.English,@"^(/Item(?:Normal|Grade|Magic|Rare|Unique))(.+?)( \(\+/Grade\))?$");
            return match.Success&&itemNames.TryGetValue(match.Groups[2].Value,out var title)
                ? f with{Czech=match.Groups[1].Value+title+match.Groups[3].Value,Method="item name reuse"}:f;
        }).ToList();
        var missing=fields.Where(f=>f.Czech is null).ToArray();
        if(missing.Length!=0)throw new InvalidDataException($"Untranslated fields: {missing.Length}. First: {missing[0].English}");
        foreach(var f in fields)
        {
            if(!ContentContract.Safe(f.English,f.Czech!))throw new InvalidDataException($"Text contract changed: {f.File} {f.Key}: {f.English} -> {f.Czech}");
            ContentLocalizer.Czech.GetBytes(f.Czech!);
        }
        Directory.CreateDirectory(output);var manifest=new List<object>();
        foreach(var file in plan.Files)
        {
            byte[] bytes=File.ReadAllBytes(file.SourcePath);
            if(Convert.ToHexString(SHA256.HashData(bytes))!=file.Sha256)throw new InvalidDataException("Source changed: "+file.File);
            var old=Unique(ContentLocalizer.Parse(Path.GetFileName(file.File),bytes,English,file.Layout));
            var replacements=fields.Where(f=>f.File==file.File&&f.English!=f.Czech).ToDictionary(f=>f.Key,f=>f.Czech!);
            byte[] result=old.Rewrite(replacements);
            var check=Unique(ContentLocalizer.Parse(Path.GetFileName(file.File),result,ContentLocalizer.Czech,file.Layout));
            if(!old.Rewrite([],true).AsSpan().SequenceEqual(check.Rewrite([],true)))throw new InvalidDataException("Non-text data changed: "+file.File);
            if(!old.Fields.Select(f=>f.Key).SequenceEqual(check.Fields.Select(f=>f.Key)))throw new InvalidDataException("Field identities changed: "+file.File);
            var expected=fields.Where(f=>f.File==file.File).ToDictionary(f=>f.Key);
            foreach(var f in check.Fields)if(f.Text!=expected[f.Key].Czech)throw new InvalidDataException("Text read-back mismatch: "+f.Key);
            int maxTextBytes=check.Fields.Select(f=>ContentLocalizer.Czech.GetByteCount(f.Text)).DefaultIfEmpty(0).Max();
            if(file.File=="TQuest.mpq"&&maxTextBytes>1536)throw new InvalidDataException("Quest text exceeds the client's 1536-byte reader limit.");
            string path=Path.Combine(output,file.File);Directory.CreateDirectory(Path.GetDirectoryName(path)!);File.WriteAllBytes(path,result);
            manifest.Add(new{file.File,OriginalSha256=file.Sha256,ResultSha256=Convert.ToHexString(SHA256.HashData(result)),Changed=replacements.Count,Fields=old.Fields.Count,NonTextBytesVerified=true,MaxTextBytes=maxTextBytes});
        }
        File.WriteAllText(Path.Combine(output,"manifest.json"),JsonSerializer.Serialize(manifest,Json));
        File.WriteAllText(Path.Combine(output,"runtime.json"),JsonSerializer.Serialize(fields.Where(f=>f.File=="runtime"),Json));
        File.WriteAllText(Path.Combine(output,"methods.json"),JsonSerializer.Serialize(fields.GroupBy(f=>f.Method).Select(g=>new{Method=g.Key,Fields=g.Count()}),Json));
        File.WriteAllText(Path.Combine(output,"language-review.json"),JsonSerializer.Serialize(fields.Where(f=>f.Method.StartsWith("local OPUS-MT") &&
            (f.Czech==f.English&&f.English.Contains(' ') || f.English.Length>150&&f.Czech!.Length<f.English.Length*0.55)).GroupBy(f=>f.English).Select(g=>g.First()),Json));
        Console.WriteLine($"Verified {plan.Files.Count} translated files; all original IDs, statistics, quest terms and rewards preserved.");
    }

    static ContentDocument Unique(ContentDocument doc)
    {
        var counts=new Dictionary<string,int>();
        for(int i=0;i<doc.Fields.Count;i++)
        {
            var f=doc.Fields[i];int occurrence=counts.GetValueOrDefault(f.Key);counts[f.Key]=occurrence+1;
            if(occurrence!=0)doc.Fields[i]=f with{Key=f.Key+"#"+occurrence};
        }
        return doc;
    }
}

static class ContentContract
{
    static Regex Protected = new("$^");
    public static void Initialize(string[] tokens) => Protected=new Regex(string.Join('|',tokens.Select(Regex.Escape))+@"|/[A-Za-z][A-Za-z0-9]*|\{[^}]*\}|<[^>]*>|\$[^$\r\n]+\$|%(?:[0-9.+\-]*(?:I64|I32|ll|hh|[hl])?[sdiufg])?|\\[nr]|\r\n|[\r\n]");
    public static string[] Signature(string s)=>Protected.Matches(s).Select(m=>m.Value).ToArray();
    public static string[] Numbers(string s)=>Regex.Matches(Protected.Replace(s,""),@"\d+(?:[.,]\d+)?").Select(m=>m.Value).Order().ToArray();
    public static bool Safe(string en,string cs)=>Signature(en).SequenceEqual(Signature(cs))&&Numbers(en).SequenceEqual(Numbers(cs));
    public static bool Technical(string s)=>string.IsNullOrWhiteSpace(s)||s.Trim()=="_"||!Regex.IsMatch(Protected.Replace(s,""),"[A-Za-z]{2}")||s.StartsWith(".\\")||s.StartsWith("http",StringComparison.OrdinalIgnoreCase);
}
