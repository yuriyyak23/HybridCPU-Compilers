using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using HybridCPU.Compiler.Cil;
using HybridCPU.Compiler.NativeAot;

internal static class MetadataInputSmoke
{
    public static void Run()
    {
        const string key = "HYBRIDCPU_METADATA_ONLY_INPUTS_V1";
        string? saved = Environment.GetEnvironmentVariable(key);
        MethodInfo read = typeof(HybridCpuSdkPackContractV1).Assembly.GetType("HybridCPU.Compiler.NativeAot.Program")!
            .GetMethod("ReadMetadataInputs", BindingFlags.NonPublic | BindingFlags.Static)!;
        MethodInfo createPresentation = typeof(HybridCpuSdkPackContractV1).Assembly.GetType("HybridCPU.Compiler.NativeAot.Program")!
            .GetMethod("CreateBodyPresentation", BindingFlags.NonPublic | BindingFlags.Static)!;
        var input = new { Path = typeof(object).Assembly.Location,
            Sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(object).Assembly.Location))).ToLowerInvariant() };
        ManagedBodyWorldModuleV1[] Invoke(string? json)
        {
            Environment.SetEnvironmentVariable(key, json);
            return (ManagedBodyWorldModuleV1[])read.Invoke(null, null)!;
        }
        void Reject(string json)
        {
            try { Invoke(json); }
            catch (TargetInvocationException e) when (e.InnerException is InvalidDataException or JsonException) { return; }
            throw new Exception("Malformed metadata input list did not fail closed");
        }
        try
        {
            if (Invoke(null).Length != 0 || Invoke("[]").Length != 0) throw new Exception("Absent metadata inputs changed legacy behavior");
            ManagedBodyWorldModuleV1[] modules = Invoke(JsonSerializer.Serialize(new[] { input }));
            if (modules.Length != 1 || modules[0].SourceIdentity != "metadata-only:" + input.Sha256)
                throw new Exception("Valid metadata bytes must retain their digest provenance");
            Reject(JsonSerializer.Serialize(new[] { new { input.Path, Sha256 = new string('0', 64) } }));
            Reject(JsonSerializer.Serialize(new[] { input, input }));
            Reject(JsonSerializer.Serialize(Enumerable.Repeat(input, 65).ToArray()));
            Reject(new string('x', 65537));
            Reject("{}"); Reject("null"); Reject("[null]");
            HybridCpuRuntimePackManifestV1 manifest = HybridCpuSdkPackContractV1.CreateManifest();
            if (HybridCpuSdkPackContractV1.Validate(manifest, "hybridcpu", "win-x64", "10.0.204", ["eh-unwind"])
                    ?.StartsWith("HCPUB1004:", StringComparison.Ordinal) != true ||
                HybridCpuSdkPackContractV1.ValidateDeclaredWorkstreams([], ["eh-unwind"]) is not null ||
                HybridCpuSdkPackContractV1.ValidateDeclaredWorkstreams(["gc-maps-safepoints"],
                    ["eh-unwind", "gc-maps-safepoints"])?.StartsWith("HCPUB1010:", StringComparison.Ordinal) != true ||
                HybridCpuSdkPackContractV1.ValidateDeclaredWorkstreams(
                    ["eh-unwind", "gc-maps-safepoints"], ["eh-unwind"]) is not null)
                throw new Exception("Derived workstreams must own publish validation while declarations remain optional superset assertions");
            string smokeAssembly = typeof(MetadataInputSmoke).Assembly.Location;
            NativeAotBodyPresentationV1 automatic = (NativeAotBodyPresentationV1)createPresentation.Invoke(null,
                [smokeAssembly, nameof(MetadataInputSmoke), nameof(Run), null])!;
            NativeAotBodyPresentationV1 asserted = (NativeAotBodyPresentationV1)createPresentation.Invoke(null,
                [smokeAssembly, nameof(MetadataInputSmoke), nameof(Run), $"{nameof(MetadataInputSmoke)}::{nameof(Run)}"])!;
            if (automatic.PresentedBodies.Count == 0 || automatic.ContractDigest != asserted.ContractDigest ||
                automatic.Root.MetadataToken == 0)
                throw new Exception("Body presentation must discover the complete token-bearing body set without a manual list");
            try
            {
                createPresentation.Invoke(null, [smokeAssembly, nameof(MetadataInputSmoke), nameof(Run), "Missing::Body"]);
                throw new Exception("Optional presented-method assertion did not reject an absent body");
            }
            catch (TargetInvocationException exception) when (exception.InnerException is ArgumentException) { }
        }
        finally { Environment.SetEnvironmentVariable(key, saved); }
        Console.WriteLine("PASS adapter metadata/body discovery guards and compiler-derived publish authority");
    }
}
