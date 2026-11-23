// Test-only call to the actual pinned Core-owned importer, including negative verdicts.
// Never act as an independent source verifier or hardware verification authority.
using System.Text.Json;
using CircuitWeave.Frontend.Roslyn.Bridge;

if (args.Length is not (4 or 6)) return 2;
string index = args[0], workspace = args[1], closureSha = args[2], indexSha = args[3];
var info = new FileInfo(index);
if (!info.Exists || info.Length is < 1 or > BridgeSegmentIndexImporterV2.MaxIndexBytes) return 2;
byte[] raw = File.ReadAllBytes(index);
// Do not pre-reject a wrong pin: the Core importer must produce that diagnostic.
var result = BridgeSegmentIndexImporterV2.Import(new BridgeV2IndexRequest(
    raw, workspace, closureSha, indexSha));
var receipt = new {
    state = result.State.ToString(), code = result.Diagnostic.Code,
    indexSha256 = result.IndexSha256, closureSha256 = result.ClosureSha256,
    workspaceCaptureSha256 = result.WorkspaceCaptureSha256,
    rows = result.Inputs?.Count,
    sourceRows = result.Inputs?.Count(input => input.Role == "source"),
    roots = result.Roots?.Count
};
// Create-new prevents a stale or forged prior result being mistaken for this invocation.
string path = Path.Combine(Path.GetDirectoryName(workspace)!, "core-reader-result.json");
using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
    JsonSerializer.Serialize(output, receipt);
bool imported = result.State == BridgeImportState.Candidate &&
                result.Diagnostic.Code == "CWINT03_V2_UNQUALIFIED_CLOSURE";
if (!imported) return 1;
if (args.Length == 4) return 0;

// The independent profile reader re-imports this same Core index and live closure;
// profile evidence remains CandidateOnly and does not qualify Csc execution.
byte[] profile = File.ReadAllBytes(args[4]);
string profileResultPath = Path.Combine(Path.GetDirectoryName(workspace)!, "csc-profile-result.json");
try
{
    var audit = CscClosureProfileV1.Audit(workspace, profile, args[5], raw, indexSha, closureSha);
    using var output = new FileStream(profileResultPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
    JsonSerializer.Serialize(output, audit, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
    return audit.Status == "CandidateOnly" ? 0 : 1;
}
catch (InvalidDataException ex)
{
    using var output = new FileStream(profileResultPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
    JsonSerializer.Serialize(output, new { status = "Rejected", diagnostic = ex.Message });
    return 1;
}
