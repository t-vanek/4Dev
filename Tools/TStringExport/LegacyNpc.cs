using System.Text;
using System.Text.RegularExpressions;

static class LegacyNpc
{
    static readonly Encoding Western=Encoding.GetEncoding(1252,EncoderFallback.ExceptionFallback,DecoderFallback.ExceptionFallback);
    static readonly Encoding Korean=Encoding.GetEncoding(949,EncoderFallback.ExceptionFallback,DecoderFallback.ExceptionFallback);
    static readonly Dictionary<string,string> Text=File.ReadLines("Tools/TStringExport/legacy-npc.tsv")
        .Where(s=>s.Length!=0&&!s.StartsWith('#')).Select(s=>s.Split('\t',2)).ToDictionary(s=>s[0],s=>s[1]);
    // Hangul syllable romanization is used only for unmapped personal names, never NPC roles.
    static readonly string[] Initial=["g","kk","n","d","tt","r","m","b","pp","s","ss","","j","jj","ch","k","t","p","h"];
    static readonly string[] Vowel=["a","ae","ya","yae","eo","e","yeo","ye","o","wa","wae","oe","yo","u","wo","we","wi","yu","eu","ui","i"];
    static readonly string[] Final=["","k","k","k","n","n","n","t","l","k","m","p","l","l","p","l","m","p","p","t","t","ng","t","t","k","t","p","t"];
    public static string? Translate(string input,string key)
    {
        string source;
        try{source=Korean.GetString(Western.GetBytes(input));}catch(EncoderFallbackException){return null;}catch(DecoderFallbackException){return null;}
        if(!source.Any(c=>c>='가'&&c<='힣'))return null;
        if(Text.TryGetValue(source.Trim(),out var translated))return translated;
        if(key.Split('/')[^1].StartsWith('1'))throw new InvalidDataException("Missing legacy NPC role translation: "+source);
        var result=new StringBuilder();bool capital=true;
        foreach(char c in source.Trim())
        {
            if(c<'가'||c>'힣'){result.Append(c);capital=char.IsWhiteSpace(c);continue;}
            int index=c-'가';string syllable=Initial[index/(21*28)]+Vowel[index/28%21]+Final[index%28];
            result.Append(capital?char.ToUpperInvariant(syllable[0])+syllable[1..]:syllable);capital=false;
        }
        return result.ToString();
    }
}
