using System.Reflection;
using System.Text.Json;
using HybridCPU.Compiler.NativeAot;

internal static class PclAdmissionSmoke
{
    public static void Run()
    {
        const string variable = "HYBRIDCPU_SINGNEXT_PCL_EMISSION_V1";
        string? previous = Environment.GetEnvironmentVariable(variable);
        MethodInfo prepare = typeof(NativeAotSeamBaselineV1).Assembly.GetType(
            "HybridCPU.Compiler.NativeAot.SingNextPclSidebandV1", true)!.GetMethod(
            "TryPrepare", BindingFlags.Static | BindingFlags.NonPublic)!;
        try
        {
            int controls = 0;
            foreach (string shape in new[] { "metadata-producer", "metadata-manifest", "metadata-host",
                "derived-manifest-producer", "derived-input-producer", "derived-manifest-host", "valid", "valid-derived" })
            {
                string directory = Path.Combine(Path.GetTempPath(), "pcl-admission-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);
                string producer = Path.Combine(directory, "producer"), manifest = Path.Combine(directory, "manifest"),
                    input = Path.Combine(directory, "input"), metadata = Path.Combine(directory, "metadata"),
                    host = Path.Combine(directory, "host"), output = Path.Combine(directory, "output");
                string[] files = [producer, manifest, input, metadata, host];
                foreach (string file in files) File.WriteAllText(file, "owned-marker");
                bool derived = shape.StartsWith("derived", StringComparison.Ordinal) || shape == "valid-derived";
                if (shape == "metadata-producer") metadata = producer;
                if (shape == "metadata-manifest") metadata = manifest;
                if (shape == "metadata-host") metadata = host;
                if (shape == "derived-manifest-producer") manifest = producer;
                if (shape == "derived-input-producer") input = producer;
                if (shape == "derived-manifest-host") manifest = host;
                Environment.SetEnvironmentVariable(variable, JsonSerializer.Serialize(new {
                    ManifestPath = manifest, InputIrPath = input, ProducerPath = producer,
                    MetadataPath = metadata, HostPath = host, DeriveFromCanonicalIr = derived }));
                object?[] arguments = [output, null, null];
                bool accepted = (bool)prepare.Invoke(null, arguments)!;
                if (shape.StartsWith("valid", StringComparison.Ordinal))
                {
                    if (!accepted || arguments[1] is null || File.Exists(metadata) ||
                        !File.Exists(producer) || !File.Exists(host) ||
                        File.Exists(manifest) == derived || File.Exists(input) == derived)
                        throw new Exception("Valid PCL preparation drift.");
                }
                else if (accepted || arguments[1] is not null ||
                    arguments[2] is not string diagnostic || !diagnostic.StartsWith("HCNAOT4001:", StringComparison.Ordinal) ||
                    files.Any(file => !File.Exists(file) || File.ReadAllText(file) != "owned-marker"))
                    throw new Exception("Rejected PCL alias mutated input/output files.");
                controls++;
            }
            Environment.SetEnvironmentVariable(variable, null);
            object?[] disabled = [Path.Combine(Path.GetTempPath(), "pcl-disabled-output"), null, null];
            if (!(bool)prepare.Invoke(null, disabled)! || disabled[1] is not null || disabled[2] is not null)
                throw new Exception("Default-off PCL admission changed.");
            Type owner = typeof(NativeAotSeamBaselineV1).Assembly.GetType(
                "HybridCPU.Compiler.NativeAot.NativeAotSeamOutputV1", true)!;
            string outputDirectory = Path.Combine(Path.GetTempPath(), "seam-output-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(outputDirectory);
            string outputPath = Path.Combine(outputDirectory, "artifact");
            byte[] payload = [0, 1, 255, 42];
            int status = (int)owner.GetMethod("Write", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, [outputPath, payload, new { Schema = "test-only-output" }, null, CancellationToken.None])!;
            using JsonDocument outputManifest = JsonDocument.Parse(File.ReadAllText(outputPath + ".json"));
            if (status != 0 || !File.ReadAllBytes(outputPath).SequenceEqual(payload) ||
                outputManifest.RootElement.GetProperty("Schema").GetString() != "test-only-output")
                throw new Exception("Shared seam output round trip failed.");
            owner.GetMethod("RejectBestEffort", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [outputPath]);
            if (File.Exists(outputPath) || File.Exists(outputPath + ".json"))
                throw new Exception("Shared seam pair rejection cleanup failed.");
            Console.WriteLine($"PASS PCL admission and shared seam output round-trip/cleanup controls={controls + 3}");
        }
        finally { Environment.SetEnvironmentVariable(variable, previous); }
    }
}
