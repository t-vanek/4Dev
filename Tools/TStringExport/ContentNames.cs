using System.Text.RegularExpressions;

static class ContentNames
{
    static readonly Dictionary<string,string> Names = File.ReadLines("Tools/TStringExport/reviewed-names.tsv")
        .Where(s=>s.Length!=0&&!s.StartsWith('#')).Select(s=>s.Split('\t',2)).ToDictionary(s=>s[0],s=>s[1]);
    public static string? Translate(string source)
    {
        if(Names.TryGetValue(source,out var exact))return exact;
        var dummyQuest=Regex.Match(source,@"^Quest( \(\d+\)| \d+)$");
        if(dummyQuest.Success)return "Úkol"+dummyQuest.Groups[1].Value;
        var seal=Regex.Match(source,@"^Jacklyn's Seal (\d+)$");
        if(seal.Success)return "Jacklynina pečeť "+seal.Groups[1].Value;
        var requirement=Regex.Match(source,@"^Total honour higher than (\d+)$");
        if(requirement.Success)return "Celková pověst vyšší než "+requirement.Groups[1].Value;
        requirement=Regex.Match(source,@"^Gold ownership (less|more) than (\d+) gold$");
        if(requirement.Success)return "Vlastnit "+(requirement.Groups[1].Value=="less"?"méně":"více")+" než "+requirement.Groups[2].Value+" zlata";
        requirement=Regex.Match(source,@"^Activity (less|more) than (\d+) hours$");
        if(requirement.Success)return "Herní doba "+(requirement.Groups[1].Value=="less"?"kratší":"delší")+" než "+requirement.Groups[2].Value+" hodin";
        requirement=Regex.Match(source,@"^This title is awared after (\d+) hours of the characters play time\.$");
        if(requirement.Success)return "Tento titul získáš po odehrání "+requirement.Groups[1].Value+" hodin s touto postavou.";
        var m=Regex.Match(source,@"^Costume (Dress|Hat) (\d+)$");
        if(m.Success)return (m.Groups[1].Value=="Dress"?"Kostýmní šaty ":"Kostýmní čepice ")+m.Groups[2].Value;
        m=Regex.Match(source,@"^([\d,]+) Bonus Points$");
        if(m.Success)return m.Groups[1].Value+" bonusových bodů";
        m=Regex.Match(source,@"^Credit \(([\d,]+)\)$");
        if(m.Success)return "Body úspěchu ("+m.Groups[1].Value+")";
        const string gift="Gift of the Gods - ";
        if(source.StartsWith(gift) && Translate(source[gift.Length..]) is { } part)return "Dar bohů - "+part;
        m=Regex.Match(source,@"^(.+?)\s+\(?(\s*\d[\d,]*)\s+(days?|hours?)\)$",RegexOptions.IgnoreCase);
        if(m.Success && Translate(m.Groups[1].Value) is { } root)
        {
            string number=m.Groups[2].Value.Trim();bool hours=m.Groups[3].Value.StartsWith("hour",StringComparison.OrdinalIgnoreCase);
            string unit=number=="1"?(hours?"hodina":"den"):new[]{"2","3","4"}.Contains(number)?(hours?"hodiny":"dny"):(hours?"hodin":"dní");
            return root+" ("+number+" "+unit+")";
        }
        m=Regex.Match(source,@"^(.+) \(Permanent\)$",RegexOptions.IgnoreCase);
        if(m.Success && Translate(m.Groups[1].Value) is { } permanent)return permanent+" (trvale)";
        m=Regex.Match(source,@"^(.+) \(([\d,]+)\)$");
        if(m.Success && Translate(m.Groups[1].Value) is { } amount)return amount+" ("+m.Groups[2].Value+")";
        // Item tooltips repeat the item name after a conditional colour/type marker.
        m=Regex.Match(source,@"^(/Item(?:Normal|Grade|Magic|Rare|Unique))(.+?)( \(\+/Grade\))?$");
        if(m.Success && Translate(m.Groups[2].Value) is { } caption)return m.Groups[1].Value+caption+m.Groups[3].Value;
        return null;
    }
}
