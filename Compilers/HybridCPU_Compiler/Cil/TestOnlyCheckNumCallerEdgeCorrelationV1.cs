using System.Security.Cryptography;
using System.Text.Json;

namespace HybridCPU.Compiler.Cil;

/// <summary>
/// Explicit opt-in importer observation for the bounded CheckNumForName caller-edge gate.
/// It observes an already imported program and has no scheduling, metadata, execution or publication authority.
/// </summary>
internal static class TestOnlyCheckNumCallerEdgeCorrelationV1
{
    internal const string OutputDirectoryEnvironment = "HYBRIDCPU_TEST_CHECKNUM_CALLER_EDGE_DIRECTORY_V1";

    internal static void ObserveImportedMethod(ManagedCompiledMethodV1 method)
    {
        string? directory = Environment.GetEnvironmentVariable(OutputDirectoryEnvironment);
        if (string.IsNullOrWhiteSpace(directory) || method.Import.Program is null) return;

        CallerEdgeRow[] rows = method.Import.Program.Instructions
            .Where(instruction => instruction.Annotation.BranchTargetSymbolName?.Contains("CheckNumForName", StringComparison.Ordinal) == true)
            .Select(instruction => new CallerEdgeRow(
                method.Identity.StableIdentity,
                instruction.SourceSpan?.StartOffset,
                instruction.StableIdentity,
                instruction.Annotation.BranchTargetSymbolName!))
            .OrderBy(static row => row.CilOffset).ThenBy(static row => row.InstructionIdentity, StringComparer.Ordinal)
            .ToArray();
        if (rows.Length == 0) return;

        string fullDirectory = Path.GetFullPath(directory);
        Directory.CreateDirectory(fullDirectory);
        string fileName = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(method.Identity.StableIdentity))) + ".json";
        WriteAtomically(Path.Combine(fullDirectory, fileName), JsonSerializer.SerializeToUtf8Bytes(new
        {
            Schema = "hybridcpu.test-only-checknum-caller-edge/v1",
            MethodIdentity = method.Identity.StableIdentity,
            Edges = rows,
            Authority = "test-only;import-observation-only;no-scheduling-authority;no-runtime-authority;no-publication-authority"
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void WriteAtomically(string path, byte[] bytes)
    {
        string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (FileStream stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private sealed record CallerEdgeRow(string CallerMethodIdentity, int? CilOffset, string InstructionIdentity, string CalleeIdentity);
}
