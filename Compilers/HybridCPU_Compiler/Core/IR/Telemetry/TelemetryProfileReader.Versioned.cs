using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HybridCPU.Compiler.Core.IR.Telemetry;

public sealed partial class TelemetryProfileReader
{
    public const string ProfileSchemaV1 = "hybridcpu.compiler-telemetry/v1";
    public const int MaximumProfileJsonCharacters = 1_048_576;

    /// <summary>
    /// Explicit versioned ingress. Invalid/stale data disables advisory heuristics.
    /// Digest covers the compiler-owned projection, independent of JSON property order.
    /// This does not enable any scheduler flag or grant runtime authority.
    /// </summary>
    public static TelemetryProfileReader CreateVersionedJson(
        string json, string expectedProgramHash, out string? canonicalDigest)
    {
        canonicalDigest = null;
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaximumProfileJsonCharacters ||
            string.IsNullOrWhiteSpace(expectedProgramHash)) return CreateEmpty();
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 2 ||
                !root.TryGetProperty("Schema", out JsonElement schema) || schema.GetString() != ProfileSchemaV1 ||
                !root.TryGetProperty("Profile", out JsonElement payload)) return CreateEmpty();
            // Validate raw input too: duplicate members cannot silently become last-writer-wins.
            using MemoryStream raw = new MemoryStream();
            using (Utf8JsonWriter writer = new Utf8JsonWriter(raw)) WriteCanonical(writer, root);
            TypedSlotTelemetryProfile? profile = payload.Deserialize<TypedSlotTelemetryProfile>(new JsonSerializerOptions
            {
                UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
            });
            if (profile is null || profile.ProgramHash != expectedProgramHash ||
                profile.TotalInjectionsPerClass is null || profile.TotalRejectsPerClass is null ||
                profile.RejectsByReason is null || profile.PerVtInjectionCounts is null ||
                profile.TotalInjectionsPerClass.Keys.Any(k => !Enum.IsDefined(k)) ||
                profile.TotalRejectsPerClass.Keys.Any(k => !Enum.IsDefined(k)) ||
                profile.RejectsByReason.Keys.Any(k => !Enum.IsDefined(k))) return CreateEmpty();
            using JsonDocument projection = JsonDocument.Parse(JsonSerializer.Serialize(profile));
            using MemoryStream canonical = new MemoryStream();
            using (Utf8JsonWriter writer = new Utf8JsonWriter(canonical))
            {
                writer.WriteStartObject();
                writer.WriteString("Schema", ProfileSchemaV1);
                writer.WritePropertyName("Profile");
                WriteCanonical(writer, projection.RootElement);
                writer.WriteEndObject();
            }
            canonicalDigest = Convert.ToHexString(SHA256.HashData(canonical.ToArray()));
            return new TelemetryProfileReader(profile);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or OverflowException)
        {
            return CreateEmpty();
        }
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value, string? member = null)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                HashSet<string> names = new HashSet<string>(StringComparer.Ordinal);
                foreach (JsonProperty property in value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    if (!names.Add(property.Name)) throw new JsonException("Duplicate profile member.");
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value, property.Name);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (JsonElement item in value.EnumerateArray()) WriteCanonical(writer, item);
                writer.WriteEndArray();
                break;
            case JsonValueKind.Number:
                double number = value.GetDouble();
                if (!double.IsFinite(number) || number < 0 ||
                    (member is not null && (member.Contains("Density", StringComparison.Ordinal) ||
                        member.Contains("Utilization", StringComparison.Ordinal) || member.EndsWith("Rate", StringComparison.Ordinal)) && number > 1))
                    throw new JsonException("Invalid telemetry number.");
                if (value.TryGetInt64(out long integer)) writer.WriteNumberValue(integer);
                else writer.WriteNumberValue(number);
                break;
            default:
                value.WriteTo(writer);
                break;
        }
    }
}
