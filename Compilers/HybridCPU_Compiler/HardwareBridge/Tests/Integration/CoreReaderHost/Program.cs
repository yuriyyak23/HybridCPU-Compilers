// Test-only call to the genuine Core-owned importer. No Bridge-owned parser/qualifier.
using System.Security.Cryptography;
using System.Text.Json;
using CircuitWeave.Frontend.Roslyn.Bridge;

if (args.Length != 4) return 2;
string index = args[0], workspace = args[1], closureSha = args[2], indexSha = args[3];
byte[] raw = File.ReadAllBytes(index);
if (Convert.ToHexString(SHA256.HashData(raw)) != indexSha) return 2;
var result = BridgeSegmentIndexImporterV2.Import(new BridgeV2IndexRequest(
    raw, workspace, closureSha, indexSha));
var receipt = new {
    state = result.State.ToString(), code = result.Diagnostic.Code,
    indexSha256 = result.IndexSha256, closureSha256 = result.ClosureSha256,
    workspaceCaptureSha256 = result.WorkspaceCaptureSha256,
    rows = result.Inputs?.Count, roots = result.Roots?.Count
};
File.WriteAllText(Path.Combine(Path.GetDirectoryName(workspace)!, "core-reader-result.json"),
    JsonSerializer.Serialize(receipt));
return result.State == BridgeImportState.Candidate &&
       result.Diagnostic.Code == "CWINT03_V2_UNQUALIFIED_CLOSURE" ? 0 : 1;
