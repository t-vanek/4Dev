using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
Console.OutputEncoding = new UTF8Encoding(false);
if (args.Length == 4 && args[0] == "--prepare-content")
{
    ContentTranslation.Prepare(args[1],args[2],args[3]);
    return;
}
if (args.Length == 4 && args[0] == "--apply-content")
{
    ContentTranslation.Apply(args[1],args[2],args[3]);
    return;
}
if (args.Length == 5 && args[0] == "--merge")
{
    CzechMerge.Run(args[1], args[2], args[3], args[4]);
    return;
}
if (args is ["--self-test"])
{
    Archive.SelfTest();
    ContentTests.Run();
    Console.WriteLine("Archive validation tests passed.");
    return;
}
if (args.Length != 3)
{
    Console.Error.WriteLine("Usage: TStringExport <installed-client-directory> <reference-TString.tcd> <new-output-directory>");
    Environment.ExitCode = 2;
    return;
}

var sourceRoot = Path.GetFullPath(args[0]);
var referencePath = Path.GetFullPath(args[1]);
var outputRoot = Path.GetFullPath(args[2]);
var sourcePath = Path.Combine(sourceRoot, "Tcd", "TString.tcd");
var source = Archive.Read(File.ReadAllBytes(sourcePath));
var reference = Archive.Read(File.ReadAllBytes(referencePath));
if (source.Nation != "CZECH")
    throw new InvalidDataException($"Expected a Czech donor, found nation '{source.Nation}'.");
// Keep input data and installed games read-only, and do not overwrite a previous export.
if (Directory.Exists(outputRoot) || File.Exists(outputRoot))
    throw new IOException("Choose a new output directory; existing exports are never overwritten.");
if (outputRoot.StartsWith(sourceRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
        StringComparison.OrdinalIgnoreCase))
    throw new IOException("Output must not be inside the installed client.");

var sourceLast = source.Records.GroupBy(x => x.Id).ToDictionary(g => g.Key, g => g.Last());
var referenceLast = reference.Records.GroupBy(x => x.Id).ToDictionary(g => g.Key, g => g.Last());
var comparison = referenceLast.Values.OrderBy(x => x.Id).Select(item =>
{
    sourceLast.TryGetValue(item.Id, out var donor);
    var referenceTokens = Archive.FormatTokens(item.Text);
    var sourceTokens = donor is null ? [] : Archive.FormatTokens(donor.Text);
    return new
    {
        item.Id, ReferenceText = item.Text, CzechTextAtSameId = donor?.Text,
        ReferenceFormatTokens = referenceTokens, CzechFormatTokens = sourceTokens,
        FormatTokensMatch = donor is not null && referenceTokens.SequenceEqual(sourceTokens),
        // Matching printf types does not prove that two versions use an ID for the same message.
        RequiresSemanticMapping = item.Id != 0,
    };
}).ToArray();

Directory.CreateDirectory(Path.Combine(outputRoot, "original"));
var copied = new List<object>();
foreach (var name in new[] { "TString.tcd", "TStringCF.tcd" })
{
    var input = Path.Combine(sourceRoot, "Tcd", name);
    if (!File.Exists(input)) continue;
    var bytes = File.ReadAllBytes(input);
    var output = Path.Combine(outputRoot, "original", name);
    File.WriteAllBytes(output, bytes);
    if (!File.ReadAllBytes(output).AsSpan().SequenceEqual(bytes))
        throw new IOException($"Copy verification failed for {name}.");
    copied.Add(new { File = name, Bytes = bytes.Length, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)) });
}
WriteJson("TString.cs-CZ.json", new
{
    source.Nation, CodePage = source.CodePage, RecordCount = source.Records.Count,
    Records = source.Records.Select(r => new { r.Id, r.Text, r.StoredAsUnicode }),
});
WriteJson("TString.reference.json", new
{
    reference.Nation, CodePage = reference.CodePage, RecordCount = reference.Records.Count,
    // Duplicate IDs are intentional in some clients: preserve their order; the last record wins.
    Records = reference.Records.Select(r => new { r.Id, r.Text, r.StoredAsUnicode }),
});
WriteJson("same-id-comparison.json", comparison);
var report = new
{
    CreatedUtc = DateTimeOffset.UtcNow,
    SourcePath = sourcePath, ReferencePath = referencePath,
    SourceNation = source.Nation, SourceRecords = source.Records.Count,
    ReferenceNation = reference.Nation, ReferenceRecords = reference.Records.Count,
    ReferenceUniqueIds = referenceLast.Count,
    ReferenceDuplicateIds = reference.Records.GroupBy(r => r.Id).Where(g => g.Count() > 1).Select(g => g.Key),
    SharedIds = referenceLast.Keys.Intersect(sourceLast.Keys).Count(),
    SourceOnlyIds = sourceLast.Keys.Except(referenceLast.Keys).Count(),
    FormatMismatchesAtSameId = comparison.Count(x => !x.FormatTokensMatch),
    DirectReplacementVerified = false,
    Note = "IDs have different meanings across these versions. Export only: no active game data was replaced. TStringCF is an unmodified reference copy, not parsed or merged.",
    CopiedFiles = copied,
};
WriteJson("report.json", report);
File.WriteAllText(Path.Combine(outputRoot, "README.txt"), """
České texty z lokálně nainstalovaného klienta Gameforge

original/TString.tcd    původní soubor, binárně shodná kopie
original/TStringCF.tcd  původní doplňkový soubor, bez konverze
TString.cs-CZ.json      všechny texty z TString v čitelném UTF-8
TString.reference.json  aktuální anglická tabulka našeho klienta, včetně duplicit
same-id-comparison.json porovnání stejného ID a formátovacích značek
report.json            počty záznamů, původ souborů a kontrolní SHA-256

Číslování se mezi verzemi liší. Tyto soubory zatím nekopírujte přes Game/Tcd.
Shodné ID ani shodné formátovací značky samy o sobě nezaručují shodný význam.
Pro import je potřeba ověřená mapa významů textů mezi oběma verzemi.
Názvy předmětů, NPC, úkoly a texty uložené v rozhraní mohou být v dalších souborech;
tento export zpracovává TString.tcd, nikoliv celý jazykový balík hry.
""", new UTF8Encoding(false));
Console.WriteLine(JsonSerializer.Serialize(new
{
    Output = outputRoot, report.SourceNation, report.SourceRecords, report.ReferenceRecords,
    report.SharedIds, report.SourceOnlyIds, report.FormatMismatchesAtSameId,
}, new JsonSerializerOptions { WriteIndented = true }));

void WriteJson(string name, object value) => File.WriteAllText(Path.Combine(outputRoot, name),
    JsonSerializer.Serialize(value, new JsonSerializerOptions
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    }), new UTF8Encoding(false));

sealed record Entry(ushort Id, string Text, bool StoredAsUnicode);
sealed record Table(string Nation, int CodePage, List<Entry> Records);

static partial class Archive
{
    public static byte[] Write(Table table)
    {
        var narrow = Encoding.GetEncoding(table.CodePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(checked((ushort)table.Records.Count));
        foreach (var entry in table.Records)
        {
            writer.Write(entry.Id);
            byte[] text = (entry.StoredAsUnicode ? Encoding.Unicode : narrow).GetBytes(entry.Text);
            int length = entry.StoredAsUnicode ? text.Length / 2 : text.Length;
            if (entry.StoredAsUnicode) { writer.Write((byte)255); writer.Write((ushort)65534); }
            if (length < 255) writer.Write((byte)length);
            else if (length < 65534) { writer.Write((byte)255); writer.Write((ushort)length); }
            else { writer.Write((byte)255); writer.Write((ushort)65535); writer.Write((uint)length); }
            writer.Write(text);
        }
        return stream.ToArray();
    }
    public static Table Read(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = new BinaryReader(stream);
        ushort count = reader.ReadUInt16();
        var raw = new List<(ushort Id, byte[] Bytes, bool Wide)>();
        for (int i = 0; i < count; i++)
        {
            ushort id = reader.ReadUInt16();
            ulong length = ReadLength(reader, out bool wide);
            int charSize = wide ? 2 : 1;
            if (length > (ulong)(stream.Length - stream.Position) / (ulong)charSize)
                throw new InvalidDataException($"Record {i} (ID {id}) exceeds the archive bounds.");
            int byteCount = checked((int)length * charSize);
            raw.Add((id, reader.ReadBytes(byteCount), wide));
        }
        if (stream.Position != stream.Length) throw new InvalidDataException("Unexpected trailing archive data.");
        var nationRecord = raw.LastOrDefault(r => r.Id == 0);
        string nation = nationRecord.Bytes is null ? "" :
            (nationRecord.Wide ? Encoding.Unicode : Encoding.ASCII).GetString(nationRecord.Bytes);
        int codePage = nation switch
        {
            "CZECH" or "POLAND" or "HUNGARY" or "ROMANIA" => 1250,
            "RUSSIA" => 1251, "GREECE" => 1253, "TURKEY" => 1254,
            "JAPAN" => 932, "TAIWAN" => 950, "KOREA" or "" => 949,
            "UNITEDKINGDOM" or "UNITEDSTATE" or "ENGLISH" or "GERMANY" or "FRANCE" or "ITALY" or "SPAIN" or "PORTUGAL" => 1252,
            _ => throw new InvalidDataException($"Unknown nation '{nation}'. Specify its code page before exporting."),
        };
        var narrowEncoding = Encoding.GetEncoding(codePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        var unicodeEncoding = new UnicodeEncoding(false, false, true);
        return new Table(nation, codePage, raw.Select(r => new Entry(r.Id,
            (r.Wide ? unicodeEncoding : narrowEncoding).GetString(r.Bytes), r.Wide)).ToList());
    }

    private static ulong ReadLength(BinaryReader reader, out bool wide)
    {
        wide = false;
        byte small = reader.ReadByte();
        if (small != 255) return small;
        ushort medium = reader.ReadUInt16();
        if (medium == 65534)
        {
            wide = true;
            small = reader.ReadByte();
            if (small != 255) return small;
            medium = reader.ReadUInt16();
        }
        if (medium != 65535) return medium;
        uint large = reader.ReadUInt32();
        return large == uint.MaxValue ? reader.ReadUInt64() : large;
    }

    // Comparison is deliberately conservative: retain order, width and length modifiers.
    [GeneratedRegex(@"%(?:\d+\$)?[-+#0 ]*(?:\d+|\*)?(?:\.(?:\d+|\*))?(?:I64|I32|hh|ll|[hljztLw])?[diuoxXfFeEgGaAcCsSpn%]")]
    private static partial Regex Printf();
    public static string[] FormatTokens(string text) => Printf().Matches(text).Select(m => m.Value).Where(s => s != "%%").ToArray();

    public static void SelfTest()
    {
        byte[] Fixture(params byte[] content) => [1, 0, 1, 0, .. content];
        // No nation record defaults to the original archive's Korean code page; ASCII is invariant.
        if (Read(Fixture(1, (byte)'A')).Records[0].Text != "A") throw new Exception("Short length failed.");
        if (Read(Fixture(255, 254, 255, 1, 0x0d, 0x01)).Records[0].Text != "č") throw new Exception("UTF-16 marker failed.");
        byte[] text = Enumerable.Repeat((byte)'x', 260).ToArray();
        if (Read(Fixture([255, 4, 1, .. text])).Records[0].Text.Length != 260) throw new Exception("WORD length failed.");
        if (Read(Fixture([255, 255, 255, 4, 1, 0, 0, .. text])).Records[0].Text.Length != 260) throw new Exception("DWORD length failed.");
        if (Read(Fixture([255, 255, 255, 255, 255, 255, 255, 4, 1, 0, 0, 0, 0, 0, 0, .. text])).Records[0].Text.Length != 260)
            throw new Exception("QWORD length failed.");
        foreach (var bad in new[] { Fixture(2, (byte)'A'), Fixture(255), Fixture(0, 99) })
        {
            bool rejected = false;
            try { Read(bad); } catch (Exception e) when (e is InvalidDataException or EndOfStreamException) { rejected = true; }
            if (!rejected) throw new Exception("Malformed archive accepted.");
        }
        if (!FormatTokens("%s: %I64u %.2f%%").SequenceEqual(new[] { "%s", "%I64u", "%.2f" }))
            throw new Exception("Printf comparison failed.");
    }
}
