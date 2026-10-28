using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

sealed record MapRange(int First, int Last, int Offset);
sealed record Mapping(string ReferenceSha256, string DonorSha256, MapRange[] Ranges,
    Dictionary<ushort, ushort> DonorIds, Dictionary<ushort, string> TextOverrides, ushort[] PreserveIds);

static partial class CzechMerge
{
    public static void Run(string referencePath, string donorPath, string mappingPath, string outputDirectory)
    {
        var rules = JsonSerializer.Deserialize<Mapping>(File.ReadAllText(mappingPath)) ?? throw new InvalidDataException("Empty mapping.");
        byte[] referenceBytes = File.ReadAllBytes(referencePath), donorBytes = File.ReadAllBytes(donorPath);
        if (!Convert.ToHexString(SHA256.HashData(referenceBytes)).Equals(rules.ReferenceSha256, StringComparison.OrdinalIgnoreCase)
            || !Convert.ToHexString(SHA256.HashData(donorBytes)).Equals(rules.DonorSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Mapping fingerprints do not match these game versions.");
        var reference = Archive.Read(referenceBytes);
        var donor = Archive.Read(donorBytes);
        if (reference.Nation != "UNITEDKINGDOM" || donor.Nation != "CZECH") throw new InvalidDataException("Unexpected source nations.");
        if (!Archive.Write(reference).AsSpan().SequenceEqual(referenceBytes) || !Archive.Write(donor).AsSpan().SequenceEqual(donorBytes))
            throw new InvalidDataException("Archive codec did not round-trip the original files exactly.");
        var oldLast = reference.Records.GroupBy(r => r.Id).ToDictionary(g => g.Key, g => g.Last());
        var newLast = donor.Records.GroupBy(r => r.Id).ToDictionary(g => g.Key, g => g.Last());
        var changes = new List<object>();
        var unresolved = new List<object>();
        var output = new List<Entry>();
        var pairs = new List<object>();
        foreach (var old in reference.Records)
        {
            string result = old.Text, reason = "preserved";
            ushort? donorId = null;
            if (old.Id == 0) { result = "CZECH"; reason = "nation"; }
            else if (old != oldLast[old.Id]) reason = "shadowed duplicate";
            else if (rules.PreserveIds.Contains(old.Id) || IsTechnical(old.Text)) reason = "technical or custom invariant";
            else if (rules.TextOverrides.TryGetValue(old.Id, out var custom)) { result = custom; reason = "reviewed custom translation"; }
            else
            {
                if (rules.DonorIds.TryGetValue(old.Id, out var explicitId)) donorId = explicitId;
                else
                {
                    var range = rules.Ranges.SingleOrDefault(r => old.Id >= r.First && old.Id <= r.Last);
                    if (range is not null) donorId = checked((ushort)(old.Id + range.Offset));
                }
                if (donorId is not null && newLast.TryGetValue(donorId.Value, out var translated) && !string.IsNullOrWhiteSpace(translated.Text))
                {
                    if (!SameArgumentTypes(old.Text, translated.Text))
                        throw new InvalidDataException($"Format argument mismatch: reference {old.Id} -> Czech {donorId}.");
                    result = MatchFormatting(old.Text, translated.Text);
                    reason = "mapped Gameforge translation";
                }
                else unresolved.Add(new { old.Id, old.Text, DonorId = donorId });
            }
            if (old.Id != 0 && !Archive.FormatTokens(result).SequenceEqual(Archive.FormatTokens(old.Text)))
                throw new InvalidDataException($"Printf contract changed for ID {old.Id}.");
            output.Add(old with { Text = result });
            if (result != old.Text) changes.Add(new { old.Id, DonorId = donorId, English = old.Text, Czech = result, Reason = reason });
            if (old.Id != 0 && old == oldLast[old.Id]) pairs.Add(new { English = old.Text, Czech = result });
        }
        if (unresolved.Count != 0)
            throw new InvalidDataException("Unmapped entries: " + JsonSerializer.Serialize(unresolved));
        var table = new Table("CZECH", 1250, output);
        byte[] bytes = Archive.Write(table);
        var verified = Archive.Read(bytes);
        if (!verified.Records.SequenceEqual(table.Records)) throw new InvalidDataException("Merged archive validation failed.");
        if (!verified.Records.Select(r => r.Id).SequenceEqual(reference.Records.Select(r => r.Id)))
            throw new InvalidDataException("Original ID order changed.");
        if (Directory.Exists(outputDirectory)) throw new IOException("Choose a new output directory.");
        Directory.CreateDirectory(outputDirectory);
        File.WriteAllBytes(Path.Combine(outputDirectory, "TString.tcd"), bytes);
        var options = new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        File.WriteAllText(Path.Combine(outputDirectory, "changes.json"), JsonSerializer.Serialize(changes, options));
        File.WriteAllText(Path.Combine(outputDirectory, "ui-dictionary.json"), JsonSerializer.Serialize(pairs, options));
        File.WriteAllText(Path.Combine(outputDirectory, "TString.json"), JsonSerializer.Serialize(table, options));
        File.WriteAllText(Path.Combine(outputDirectory, "merge-report.json"), JsonSerializer.Serialize(new
        {
            Source = Path.GetFullPath(donorPath), Reference = Path.GetFullPath(referencePath),
            ReferenceSha256 = rules.ReferenceSha256, DonorSha256 = rules.DonorSha256,
            ResultSha256 = Convert.ToHexString(SHA256.HashData(bytes)), OriginalRecords = reference.Records.Count,
            ResultRecords = verified.Records.Count, ChangedRecords = changes.Count,
            Unmapped = unresolved.Count, PrintfContractsVerified = true, TechnicalStringsPreserved = true,
        }, options));
        Console.WriteLine($"Czech table ready: {output.Count} records, {changes.Count} changed, no unmapped entries; all printf contracts verified.");
    }

    private static bool IsTechnical(string text)
    {
        if (text.StartsWith(".\\") || text.Contains("http", StringComparison.OrdinalIgnoreCase) || text.Contains('@') || text.StartsWith('/')) return true;
        // Strings consisting only of punctuation, numbers and printf fields are part of formatting.
        string remaining = Token().Replace(text, "");
        return !remaining.Any(char.IsLetter);
    }

    [GeneratedRegex(@"%(?:\d+\$)?[-+#0 ]*(?:\d+|\*)?(?:\.(?:\d+|\*))?(?:I64|I32|hh|ll|[hljztLw])?[diuoxXfFeEgGaAcCsSpn%]")]
    private static partial Regex Token();
    [GeneratedRegex(@"(I64|I32|hh|ll|[hljztLw])?([diuoxXfFeEgGaAcCsSpn])$")]
    private static partial Regex Argument();
    private static bool SameArgumentTypes(string a, string b) => Archive.FormatTokens(a).Select(t => Argument().Match(t).Value)
        .SequenceEqual(Archive.FormatTokens(b).Select(t => Argument().Match(t).Value));
    private static string MatchFormatting(string reference, string translated)
    {
        var fields = Archive.FormatTokens(reference);
        int index = 0;
        return Token().Replace(translated, match => match.Value == "%%" ? "%%" : fields[index++]);
    }
}
