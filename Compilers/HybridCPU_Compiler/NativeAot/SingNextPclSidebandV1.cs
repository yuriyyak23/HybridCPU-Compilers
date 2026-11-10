using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HybridCPU.Compiler.NativeAot;

/// <summary>
/// Optional post-lowering transport for SingNext immutable compiler evidence. This sideband
/// invokes an external producer after the final artifact has been written; it grants no
/// machine permission and does not participate in lowering or legality decisions.
/// </summary>
internal static class SingNextPclSidebandV1
{
    public const string EnvironmentVariable = "HYBRIDCPU_SINGNEXT_PCL_EMISSION_V1";
    private const int MaximumConfigurationBytes = 16 * 1024;
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    internal static bool TryPrepare(
        string outputPath,
        out SingNextPclEmissionContextV1? context,
        out string? diagnostic)
    {
        context = null;
        diagnostic = null;
        string? text = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (string.IsNullOrEmpty(text)) return true;

        try
        {
            if (System.Text.Encoding.UTF8.GetByteCount(text) > MaximumConfigurationBytes)
                throw new InvalidDataException("configuration exceeds its bound");
            SingNextPclEmissionRequestV1 request = JsonSerializer.Deserialize<SingNextPclEmissionRequestV1>(text, Json)
                ?? throw new InvalidDataException("configuration is empty");
            string output = Path.GetFullPath(outputPath);
            string metadata = RequiredPath(request.MetadataPath, nameof(request.MetadataPath));
            if (PathComparer.Equals(output, metadata))
                throw new InvalidDataException("output and metadata paths must be distinct");
            string? directory = Path.GetDirectoryName(metadata);
            if (string.IsNullOrEmpty(directory))
                throw new InvalidDataException("metadata path has no parent directory");
            string manifest = request.DeriveFromCanonicalIr
                ? RequiredDerivedPath(request.ManifestPath, nameof(request.ManifestPath))
                : RequiredFile(request.ManifestPath, nameof(request.ManifestPath));
            string input = request.DeriveFromCanonicalIr
                ? RequiredDerivedPath(request.InputIrPath, nameof(request.InputIrPath))
                : RequiredFile(request.InputIrPath, nameof(request.InputIrPath));
            string producer = RequiredFile(request.ProducerPath, nameof(request.ProducerPath));
            string? host = string.IsNullOrWhiteSpace(request.HostPath)
                ? null
                : RequiredFile(request.HostPath, nameof(request.HostPath));
            string toolchain = Path.GetFullPath(typeof(SingNextPclSidebandV1).Assembly.Location);
            if (!File.Exists(toolchain))
                throw new FileNotFoundException("adapter toolchain assembly is unavailable", toolchain);

            string[] distinct = [manifest, input, output, toolchain, producer, metadata];
            if (distinct.Distinct(PathComparer).Count() != distinct.Length)
                throw new InvalidDataException("manifest, input, output, toolchain, producer, and metadata paths must be distinct");
            if (host is not null && distinct.Any(path => PathComparer.Equals(host, path)))
                throw new InvalidDataException("host path must be distinct from all evidence and artifact paths");

            // Admit every path before clearing outputs: a rejected alias must not delete
            // the producer, host, toolchain or input manifest/IR.
            Directory.CreateDirectory(directory);
            File.Delete(metadata);
            if (request.DeriveFromCanonicalIr)
            {
                ClearPreparedPath(manifest);
                ClearPreparedPath(input);
            }
            context = new(manifest, input, output, toolchain, producer, metadata, host,
                request.DeriveFromCanonicalIr);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or IOException or JsonException or UnauthorizedAccessException)
        {
            diagnostic = $"HCNAOT4001: SingNext PCL sideband configuration is invalid: {exception.Message}";
            return false;
        }
    }

    internal static bool TryEmit(SingNextPclEmissionContextV1? context, out string? diagnostic)
    {
        diagnostic = null;
        if (context is null) return true;
        try
        {
            ProcessStartInfo start = new ProcessStartInfo(context.HostPath ?? context.ProducerPath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            if (context.HostPath is not null) start.ArgumentList.Add(context.ProducerPath);
            start.ArgumentList.Add(context.ManifestPath);
            start.ArgumentList.Add(context.InputIrPath);
            start.ArgumentList.Add(context.OutputPath);
            start.ArgumentList.Add(context.ToolchainPath);
            start.ArgumentList.Add(context.ProducerPath);
            start.ArgumentList.Add(context.MetadataPath);

            using Process process = Process.Start(start)
                ?? throw new InvalidOperationException("configured evidence producer did not start");
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(60_000))
            {
                process.Kill(entireProcessTree: true);
                Task.WaitAll(stdout, stderr);
                DeleteMetadataBestEffort(context.MetadataPath);
                diagnostic = "HCNAOT4002: SingNext PCL evidence producer timed out";
                return false;
            }
            Task.WaitAll(stdout, stderr);
            if (process.ExitCode != 0 || !File.Exists(context.MetadataPath) || new FileInfo(context.MetadataPath).Length == 0)
            {
                DeleteMetadataBestEffort(context.MetadataPath);
                string detail = stderr.Result.Trim();
                if (detail.Length == 0) detail = stdout.Result.Trim();
                diagnostic = $"HCNAOT4003: SingNext PCL evidence producer failed with exit {process.ExitCode}: {detail}";
                return false;
            }
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            DeleteMetadataBestEffort(context.MetadataPath);
            diagnostic = $"HCNAOT4004: SingNext PCL evidence producer could not complete: {exception.Message}";
            return false;
        }
    }

    private static string RequiredFile(string? path, string name)
    {
        string full = RequiredPath(path, name);
        if (!File.Exists(full)) throw new FileNotFoundException($"{name} does not identify a file", full);
        return full;
    }

    private static string RequiredPath(string? path, string name)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new InvalidDataException($"{name} is required");
        return Path.GetFullPath(path);
    }

    private static string RequiredDerivedPath(string? path, string name)
    {
        string full = RequiredPath(path, name);
        string? directory = Path.GetDirectoryName(full);
        if (string.IsNullOrEmpty(directory))
            throw new InvalidDataException($"{name} has no parent directory");
        return full;
    }

    private static void ClearPreparedPath(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.Delete(path);
    }

    private static void DeleteMetadataBestEffort(string path)
    {
        try { File.Delete(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
}

internal sealed record SingNextPclEmissionRequestV1(
    string ManifestPath,
    string InputIrPath,
    string ProducerPath,
    string MetadataPath,
    string? HostPath = null,
    bool DeriveFromCanonicalIr = false);

internal sealed record SingNextPclEmissionContextV1(
    string ManifestPath,
    string InputIrPath,
    string OutputPath,
    string ToolchainPath,
    string ProducerPath,
    string MetadataPath,
    string? HostPath,
    bool DeriveFromCanonicalIr);
