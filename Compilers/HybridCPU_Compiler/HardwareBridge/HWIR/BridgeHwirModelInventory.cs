#if HARDWAREBRIDGE_HWIR
using System.Security.Cryptography;
using System.Text.Json;

namespace HybridCPU.Compiler.HardwareBridge.Hwir;

/// <summary>Identity and coverage of an observed source inventory, never an elaborated HWIR design.</summary>
public sealed record BridgeHwirModelInventorySummary(string Schema, string Disposition, string SourceIndexSha256,
    string SemanticReportSha256, int Types, int Classes, int Structs, int Declarations, int Members);

/// <summary>
/// Admits the complete source-index projection to the HWIR bridge as source-only metadata.
/// Caller must separately compare it with its semantic report and pinned inputs. This check
/// does not resolve symbols again, prove MSBuild closure, infer hardware, or create modules.
/// </summary>
public static class BridgeHwirModelInventory
{
    public static BridgeHwirModelInventorySummary Capture(ReadOnlyMemory<byte> indexBytes)
    {
        if (indexBytes.Length is < 1 or > 32 * 1024 * 1024)
            throw new InvalidDataException("Unsupported bounded source inventory.");
        using var document = JsonDocument.Parse(indexBytes);
        var index = document.RootElement;
        if (
            index.GetProperty("schema").GetString() != "circuitweave.source-coverage-index/v1" ||
            index.GetProperty("disposition").GetString() != "incomplete-unqualified")
            throw new InvalidDataException("Unsupported bounded source inventory.");
        var reportSha = index.GetProperty("semanticReportSha256").GetString();
        if (reportSha is not { Length: 64 } || reportSha.Any(c => !char.IsAsciiHexDigit(c)))
            throw new InvalidDataException("Invalid semantic report identity.");
        var symbols = index.GetProperty("symbols");
        if (symbols.ValueKind != JsonValueKind.Array || symbols.GetArrayLength() > 100_000)
            throw new InvalidDataException("Invalid source symbol count.");
        var identities = new HashSet<(string Assembly, string Symbol)>();
        var classes = 0; var structs = 0; var declarations = 0; var members = 0;
        foreach (var type in symbols.EnumerateArray())
        {
            var assembly = Required(type, "assembly"); var symbol = Required(type, "symbol");
            if (!identities.Add((assembly, symbol))) throw new InvalidDataException("Duplicate semantic source type identity.");
            switch (Required(type, "kind"))
            {
                case "Class": classes++; break;
                case "Struct": structs++; break;
                default: throw new InvalidDataException("Unsupported source type kind.");
            }
            var memberArray = type.GetProperty("members"); var parts = type.GetProperty("declarations");
            if (memberArray.ValueKind != JsonValueKind.Array || memberArray.GetArrayLength() > 100_000 ||
                parts.ValueKind != JsonValueKind.Array || parts.GetArrayLength() is < 1 or > 20_000)
                throw new InvalidDataException("Invalid source member or declaration collection.");
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var member in memberArray.EnumerateArray())
            {
                if (!ids.Add(Required(member, "id"))) throw new InvalidDataException("Duplicate member identity.");
                _ = Required(member, "kind");
                var locations = member.GetProperty("locations");
                if (locations.ValueKind != JsonValueKind.Array || locations.GetArrayLength() is < 1 or > 20_000)
                    throw new InvalidDataException("Missing member source location.");
                foreach (var location in locations.EnumerateArray()) CheckLocation(location);
                members = checked(members + 1);
            }
            var seenParts = new HashSet<(string Path, int Line, int Column)>();
            foreach (var part in parts.EnumerateArray())
            {
                var coordinate = CheckLocation(part.GetProperty("location"));
                if (!seenParts.Add(coordinate)) throw new InvalidDataException("Duplicate partial declaration coordinate.");
                var own = part.GetProperty("memberIds");
                if (own.ValueKind != JsonValueKind.Array || own.GetArrayLength() > memberArray.GetArrayLength())
                    throw new InvalidDataException("Invalid declaration member list.");
                var local = new HashSet<string>(StringComparer.Ordinal);
                foreach (var id in own.EnumerateArray())
                    if (id.ValueKind != JsonValueKind.String || id.GetString() is not { } text || !ids.Contains(text) || !local.Add(text))
                        throw new InvalidDataException("Unknown or repeated declaration member identity.");
                declarations = checked(declarations + 1);
            }
        }
        return new("hardwarebridge.model-inventory/v1", "source-only-unqualified",
            Convert.ToHexString(SHA256.HashData(indexBytes.Span)).ToLowerInvariant(), reportSha,
            identities.Count, classes, structs, declarations, members);
    }

    private static string Required(JsonElement item, string property)
    {
        var value = item.GetProperty(property).GetString();
        if (string.IsNullOrWhiteSpace(value) || value.Length > 8192 || value.Any(char.IsControl))
            throw new InvalidDataException("Invalid source identity: " + property);
        return value;
    }

    private static (string Path, int Line, int Column) CheckLocation(JsonElement location)
    {
        var path = Required(location, "file");
        var line = location.GetProperty("startLine").GetInt32();
        var column = location.GetProperty("startColumn").GetInt32();
        if (line < 1 || column < 1) throw new InvalidDataException("Invalid source coordinate.");
        return (path, line, column);
    }
}
#endif
