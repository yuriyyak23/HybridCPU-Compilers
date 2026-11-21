// A *real* isolated Csc PE/PDB fixture. Test Companion facts below are explicitly
// test-only assertions assembled from actual emitted metadata, NOT Golden/Companion output.
using System.Buffers.Binary;
using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HybridCPU.Compiler.HardwareBridge.CompilerFacts;
using HybridCPU.Compiler.HardwareBridge.SourceSnapshot;

internal static class Program
{
    private static string Hash(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes));

    private static void Check(bool yes, string message)
    {
        if (!yes) throw new Exception("HB-INT-03 executed Csc fixture: " + message);
    }

    private static void Refuse(BridgeCscLinkRefusal code, Action body)
    {
        try { body(); }
        catch (BridgeCscLinkException e) when (e.Code == code) { return; }
        throw new Exception("Expected typed refusal " + code);
    }

    private static (string[] Args, byte[] Pe, byte[] Pdb) Compile(string csc, string sourcePath,
        string pePath, string pdbPath, string reference, params string[] otherSources)
    {
        // The ordered Csc argv is created ONCE and passed unchanged to ProcessStartInfo.
        string[] argv = new[] { csc, "/noconfig", "/nostdlib+", "/target:library",
            "/debug:portable", "/deterministic+", "/optimize-",
            "/out:" + pePath, "/pdb:" + pdbPath, "/reference:" + reference, sourcePath }
            .Concat(otherSources).ToArray();
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
        };
        foreach (string a in argv) start.ArgumentList.Add(a);
        var duration = Stopwatch.StartNew();
        using Process process = Process.Start(start) ??
            throw new Exception("Could not start actual Csc.");
        Task<string> stdoutRead = process.StandardOutput.ReadToEndAsync();
        Task<string> stderrRead = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(90_000))
        {
            process.Kill(entireProcessTree: true);
            throw new Exception("BLOCKED: actual toy Csc exceeded 90 s process timeout.");
        }
        Check(Task.WaitAll(new Task[] { stdoutRead, stderrRead }, 10_000),
            "Actual Csc output capture timed out.");
        string stdout = stdoutRead.Result, stderr = stderrRead.Result;
        duration.Stop();
        Console.WriteLine("actual-csc duration-ms=" + duration.ElapsedMilliseconds +
            " compilerSHA256=" + Hash(File.ReadAllBytes(csc)) +
            " argvSHA256=" + Hash(JsonSerializer.SerializeToUtf8Bytes(argv)));
        Console.WriteLine("actual-csc exit=" + process.ExitCode +
            " stdoutSHA256=" + Hash(Encoding.UTF8.GetBytes(stdout)) +
            " stderrSHA256=" + Hash(Encoding.UTF8.GetBytes(stderr)));
        Check(process.ExitCode == 0, "Actual Csc failed: " + stderr + stdout);
        byte[] pe = File.ReadAllBytes(pePath);
        byte[] pdb = File.ReadAllBytes(pdbPath);
        Console.WriteLine("actual-pe SHA256=" + Hash(pe) + " bytes=" + pe.Length);
        Console.WriteLine("actual-pdb SHA256=" + Hash(pdb) + " bytes=" + pdb.Length);
        return (argv, pe, pdb);
    }

    private static void CheckNoReparse(string directory, string path)
    {
        Check((File.GetAttributes(directory) & FileAttributes.ReparsePoint) == 0,
            "reparse directory forbidden");
        Check((File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0,
            "reparse source forbidden");
        Check(Path.GetFullPath(path) == path, "source path must be absolute and normalized");
    }

    public static int Main()
    {
        string? csc = Environment.GetEnvironmentVariable("HB_CSC_PATH");
        if (string.IsNullOrWhiteSpace(csc) || !File.Exists(csc))
        {
            Console.Error.WriteLine("BLOCKED: HB_CSC_PATH must identify the pinned SDK's actual csc.dll.");
            return 2;
        }
        string sdk = Environment.GetEnvironmentVariable("HB_SDK_VERSION") ??
            throw new Exception("Missing exact installed SDK pin.");
        string scratch = Path.Combine(Path.GetTempPath(), "hb-int-03-csc-" +
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            string sourcesDir = Path.Combine(scratch, "src");
            Directory.CreateDirectory(sourcesDir);
            string cs = Path.Combine(sourcesDir, "Fixture.cs");
            const string sourceText = """
                public static class Fixture
                {
                    public static int Add(int a, int b)
                    {
                        return Helper.Sum(a, b);
                    }
                }
                """;
            const string helperText = """
                public static class Helper
                {
                    public static int Sum(int a, int b)
                    {
                        return a + b;
                    }
                }
                """;
            byte[] sourceBytes = Encoding.UTF8.GetBytes(sourceText);
            byte[] helperBytes = Encoding.UTF8.GetBytes(helperText);
            string helperPath = Path.Combine(sourcesDir, "Helper.cs");
            File.WriteAllBytes(cs, sourceBytes);
            File.WriteAllBytes(helperPath, helperBytes);
            CheckNoReparse(sourcesDir, cs);
            CheckNoReparse(sourcesDir, helperPath);
            byte[] before = File.ReadAllBytes(cs);
            byte[] helperBefore = File.ReadAllBytes(helperPath);
            string pePath = Path.Combine(scratch, "Fixture.dll");
            string pdbPath = Path.Combine(scratch, "Fixture.pdb");
            string referencePath = typeof(object).Assembly.Location;
            byte[] referenceBytes = File.ReadAllBytes(referencePath);
            var result = Compile(csc, cs, pePath, pdbPath, referencePath, helperPath);
            Check(File.ReadAllBytes(cs).AsSpan().SequenceEqual(before) &&
                File.ReadAllBytes(helperPath).AsSpan().SequenceEqual(helperBefore),
                "Fixture Csc modified a source!");

            using var pe = new PEReader(new MemoryStream(result.Pe, false));
            MetadataReader metadata = pe.GetMetadataReader();
            Guid mvid = metadata.GetGuid(metadata.GetModuleDefinition().Mvid);
            DebugDirectoryEntry cvEntry = pe.ReadDebugDirectory().Single(entry =>
                entry.Type == DebugDirectoryEntryType.CodeView);
            CodeViewDebugDirectoryData cv = pe.ReadCodeViewDebugDirectoryData(cvEntry);
            Console.WriteLine("actual-mvid=" + mvid.ToString("D") +
                " codeview-guid=" + cv.Guid.ToString("D") +
                " codeview-stamp=" + cvEntry.Stamp.ToString("X8") +
                " codeview-age=" + cv.Age);
            MethodDefinitionHandle methodHandle = metadata.MethodDefinitions.Single(h =>
                metadata.GetString(metadata.GetMethodDefinition(h).Name) == "Add");
            int token = MetadataTokens.GetToken(methodHandle);
            MethodDefinition method = metadata.GetMethodDefinition(methodHandle);
            byte[] il = pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes()!;
            MethodDefinitionHandle helperHandle = metadata.MethodDefinitions.Single(h =>
                metadata.GetString(metadata.GetMethodDefinition(h).Name) == "Sum");
            int helperToken = MetadataTokens.GetToken(helperHandle);
            MethodDefinition helperMethod = metadata.GetMethodDefinition(helperHandle);
            byte[] helperIl = pe.GetMethodBody(helperMethod.RelativeVirtualAddress).GetILBytes()!;
            int callOffset = Array.IndexOf(il, (byte)0x28);
            Check(callOffset >= 0 && callOffset + 5 <= il.Length &&
                BinaryPrimitives.ReadInt32LittleEndian(il.AsSpan(callOffset + 1, 4)) ==
                    helperToken, "Primary method must call exact owned-body MethodDef.");
            using MetadataReaderProvider pdbProvider =
                MetadataReaderProvider.FromPortablePdbStream(new MemoryStream(result.Pdb, false));
            MetadataReader pdb = pdbProvider.GetMetadataReader();
            MethodDebugInformation debug = pdb.GetMethodDebugInformation(
                MetadataTokens.MethodDebugInformationHandle(MetadataTokens.GetRowNumber(methodHandle)));
            SequencePoint point = debug.GetSequencePoints().First(p => !p.IsHidden);
            DocumentHandle docHandle = point.Document.IsNil ? debug.Document : point.Document;
            string pdbPathForSource = pdb.GetString(pdb.GetDocument(docHandle).Name);
            MethodDebugInformation helperDebug = pdb.GetMethodDebugInformation(
                MetadataTokens.MethodDebugInformationHandle(
                    MetadataTokens.GetRowNumber(helperHandle)));
            SequencePoint helperPoint = helperDebug.GetSequencePoints().First(p => !p.IsHidden);
            DocumentHandle helperDocHandle = helperPoint.Document.IsNil ?
                helperDebug.Document : helperPoint.Document;
            string pdbPathForHelper = pdb.GetString(pdb.GetDocument(helperDocHandle).Name);
            Check(pdbPathForSource == cs && pdbPathForHelper == helperPath,
                "PDB document paths not exact captured source paths");
            Check(pdb.GetBlobBytes(pdb.GetDocument(docHandle).Hash).AsSpan()
                .SequenceEqual(SHA256.HashData(before)) &&
                pdb.GetBlobBytes(pdb.GetDocument(helperDocHandle).Hash).AsSpan()
                .SequenceEqual(SHA256.HashData(helperBefore)),
                "PDB document checksum not actual source");

            var roots = new[] { new BridgeRootSelectorV1("Fixture", "Add", token,
                Array.Empty<string>(), Array.Empty<string>()) };
            BridgeSourceSnapshotV1 snapshot = BridgeSourceSnapshotV1.Capture(
                new BridgeSourceRequestV1("Fixture", "CSharp", new[] {
                    new BridgeSourceTextV1("src/Fixture.cs", sourceText),
                    new BridgeSourceTextV1("src/Helper.cs", helperText)
                }, new ReadOnlyMemory<byte>[] { referenceBytes }, roots,
                Array.Empty<int>(), new[] { 0 }), "fixture-only-admission",
                "fixture-only-compiler", sdk);
            Check(snapshot.Documents[0].Sha256 == Hash(before) &&
                snapshot.Documents[1].Sha256 == Hash(helperBefore),
                "snapshot not pinned to both actual sources");
            string id = "TEST-ONLY:Fixture.Add:" + token.ToString("X8");
            // There is no claim that this identity digest or input digest came from
            // the real Companion. Both are test-only assertions derived from actual bytes.
            string helperId = "TEST-ONLY:Helper.Sum:" + helperToken.ToString("X8");
            var methodFact = new BridgeMethodFactV1(id, "Fixture", "Fixture.dll",
                "Fixture", "Add", "System.Int32(System.Int32,System.Int32)", token,
                Hash(Encoding.UTF8.GetBytes(id)), Hash(result.Pe), "Success", new[] { helperId });
            var helperFact = new BridgeMethodFactV1(helperId, "Fixture", "Fixture.dll",
                "Helper", "Sum", "System.Int32(System.Int32,System.Int32)", helperToken,
                Hash(Encoding.UTF8.GetBytes(helperId)), Hash(result.Pe), "Success",
                Array.Empty<string>());
            var map = new BridgeMapRowV1(id, Hash(result.Pe), Hash(result.Pdb), token,
                point.Offset, 0, pdbPathForSource, Hash(before), point.StartLine, point.StartColumn);
            var helperMap = new BridgeMapRowV1(helperId, Hash(result.Pe), Hash(result.Pdb),
                helperToken, helperPoint.Offset, 0, pdbPathForHelper, Hash(helperBefore),
                helperPoint.StartLine, helperPoint.StartColumn);
            var facts = new BridgeCompilerFactsV1(BridgeCompilerFactsV1.SchemaId, "Candidate",
                snapshot.SnapshotDigest, Hash(before), "fixture-only", Hash(result.Pe),
                Hash(result.Pdb), Hash(il), Hash(Encoding.UTF8.GetBytes("fixture-graph")),
                "Success", new[] { id }, new[] { methodFact, helperFact },
                new[] { new BridgeEdgeFactV1("TEST-ONLY:Fixture.Add->Helper.Sum",
                    id, helperId, callOffset, helperToken) },
                new[] { map, helperMap }, Array.Empty<BridgeDiagnosticV1>());
            BridgeCscExecutedEventV1 e = new(sdk, csc, File.ReadAllBytes(csc),
                JsonSerializer.SerializeToUtf8Bytes(result.Args), 0, pePath, pdbPath,
                new[] {
                    new BridgeCscCapturedInputV1(cs, "src/Fixture.cs", "source", before),
                    new BridgeCscCapturedInputV1(helperPath, "src/Helper.cs", "source", helperBefore)
                },
                new[] { new BridgeCscCapturedInputV1(referencePath, "refs/0000.bin",
                    "metadata-only", referenceBytes) }, result.Pe, result.Pdb);

            // Existing sidecar/v1 bytes must not change by writing the opt-in new link.
            byte[] oldV1 = BridgeSidecarCodecV1.Write(snapshot,
                BridgeCompilerFactsV1.Rejected(snapshot.SnapshotDigest, "FIXTURE", "no Golden"));
            byte[] written = BridgeExecutedCscLinkV1.Write(snapshot, facts, e);
            BridgeCscLinkPayloadV1 actual = BridgeExecutedCscLinkV1.ReadAndValidate(
                written, snapshot, facts, e, sdk);
            Check(actual.SoftwareStatus == "Candidate" && actual.Methods.Count == 2 &&
                actual.Methods[0].MetadataToken == token &&
                actual.Methods[0].IlSha256 == Hash(il), "method/IL metadata linkage");
            Check(actual.Pins.Any(p => p.Role == "output-pe" && p.Sha256 == Hash(result.Pe)) &&
                actual.Pins.Any(p => p.Role == "output-portable-pdb" && p.Sha256 == Hash(result.Pdb)),
                "real output byte pins");
            Check(oldV1.SequenceEqual(BridgeSidecarCodecV1.Write(snapshot,
                BridgeCompilerFactsV1.Rejected(snapshot.SnapshotDigest, "FIXTURE", "no Golden"))),
                "sidecar/v1 bytes changed");
            Check(written.SequenceEqual(BridgeExecutedCscLinkV1.Write(snapshot, facts, e)),
                "new link envelope not canonical");

            Refuse(BridgeCscLinkRefusal.FailedCompilation, () =>
                BridgeExecutedCscLinkV1.Write(snapshot, facts, e with { ExitCode = 1 }));
            Refuse(BridgeCscLinkRefusal.MissingPePdb, () =>
                BridgeExecutedCscLinkV1.Write(snapshot, facts, e with { PdbImage = [] }));
            Refuse(BridgeCscLinkRefusal.StaleSource, () =>
                BridgeExecutedCscLinkV1.Write(snapshot, facts,
                    e with { Sources = new[] { e.Sources[0] with { Bytes = Encoding.UTF8.GetBytes("changed") } } }));
            Refuse(BridgeCscLinkRefusal.StaleReference, () =>
                BridgeExecutedCscLinkV1.Write(snapshot, facts,
                    e with { References = new[] { e.References[0] with { Bytes = [1, 2, 3] } } }));
            Refuse(BridgeCscLinkRefusal.WrongArguments, () =>
                BridgeExecutedCscLinkV1.Write(snapshot, facts,
                    e with { ArgvJsonUtf8 = JsonSerializer.SerializeToUtf8Bytes(
                        result.Args.Where(a => !a.StartsWith("/pdb:", StringComparison.Ordinal)).ToArray()) }));
            Refuse(BridgeCscLinkRefusal.WrongArguments, () =>
                BridgeExecutedCscLinkV1.Write(snapshot, facts,
                    e with { ArgvJsonUtf8 = JsonSerializer.SerializeToUtf8Bytes(
                        result.Args.Append("@unobserved.rsp").ToArray()) }));
            Refuse(BridgeCscLinkRefusal.WrongArguments, () =>
                BridgeExecutedCscLinkV1.Write(snapshot, facts,
                    e with { OutputPdbPath = e.OutputPePath }));
            Refuse(BridgeCscLinkRefusal.WrongPePdb, () =>
                BridgeExecutedCscLinkV1.Write(snapshot, facts,
                    e with { PeImage = result.Pe[..^1] }));
            Refuse(BridgeCscLinkRefusal.WrongPePdb, () =>
                BridgeExecutedCscLinkV1.Write(snapshot, facts,
                    e with { PdbImage = result.Pdb[..^1] }));
            return RunMutations(snapshot, facts, e, written, result, sourcesDir, referencePath, token,
                sourceBytes);
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    private static void RunOpaqueDispatchMutations()
    {
        // A self-consistent Companion graph with zero direct edges cannot
        // certify these opaque execution transfers. These are synthetic IL
        // parser negatives, separate from the actual two-source Csc positive.
        static void Opaque(string label, params byte[] il)
        {
            Refuse(BridgeCscLinkRefusal.StaleMethodGraph, () =>
            {
                _ = BridgeExecutedCscLinkV1.ParseIlCalls(il);
            });
            Console.WriteLine("PASS: R1-03 IL refusal " + label);
        }
        Opaque("jmp MethodDef", 0x27, 0x01, 0x00, 0x00, 0x06);
        Opaque("calli StandAloneSig", 0x29, 0x01, 0x00, 0x00, 0x11);
        Opaque("ldftn MethodDef", 0xFE, 0x06, 0x01, 0x00, 0x00, 0x06);
        Opaque("ldvirtftn MemberRef", 0xFE, 0x07, 0x01, 0x00, 0x00, 0x0A);
        Opaque("ldtoken MethodDef", 0xD0, 0x01, 0x00, 0x00, 0x06);
        Opaque("ldtoken MemberRef", 0xD0, 0x01, 0x00, 0x00, 0x0A);
        Opaque("ldtoken MethodSpec", 0xD0, 0x01, 0x00, 0x00, 0x2B);
        Opaque("truncated ldtoken", 0xD0, 0x01);
        // Preserve the existing admitted direct MethodDef subset.
        var direct = BridgeExecutedCscLinkV1.ParseIlCalls(
            new byte[] { 0x00, 0x28, 0x02, 0x00, 0x00, 0x06 });
        Check(direct.Count == 1 && direct[0].Offset == 1 &&
            direct[0].Token == 0x06000002, "direct MethodDef IL regression");
        Check(BridgeExecutedCscLinkV1.ParseIlCalls(
            new byte[] { 0xD0, 0x01, 0x00, 0x00, 0x02 }).Count == 0,
            "type-only ldtoken incorrectly treated as a method edge");
    }

    private static int RunMutations(BridgeSourceSnapshotV1 snapshot, BridgeCompilerFactsV1 facts,
        BridgeCscExecutedEventV1 e, byte[] written,
        (string[] Args, byte[] Pe, byte[] Pdb) result, string sourcesDir,
        string referencePath, int token, byte[] originalSource)
    {
        RunOpaqueDispatchMutations();
        BridgeMethodFactV1 original = facts.Methods[0];
        // Change declared assembly identity while keeping the original *actual* PE/PDB.
        // An internally consistent source snapshot/facts pair must not rename the PE.
        BridgeSourceSnapshotV1 ReCapture(string assembly, IReadOnlyList<BridgeRootSelectorV1> roots) =>
            BridgeSourceSnapshotV1.Capture(
                new BridgeSourceRequestV1(assembly, "CSharp", new[] {
                    new BridgeSourceTextV1("src/Fixture.cs", Encoding.UTF8.GetString(originalSource)),
                    new BridgeSourceTextV1("src/Helper.cs", Encoding.UTF8.GetString(e.Sources[1].Bytes))
                }, new ReadOnlyMemory<byte>[] { e.References[0].Bytes }, roots,
                    Array.Empty<int>(), new[] { 0 }),
                "fixture-only-admission", "fixture-only-compiler", e.SdkVersion);
        BridgeSourceSnapshotV1 foreignAssembly = ReCapture("WrongAssembly",
            snapshot.Roots);
        Refuse(BridgeCscLinkRefusal.WrongAssembly, () =>
            BridgeExecutedCscLinkV1.Write(foreignAssembly,
                facts with { SnapshotDigest = foreignAssembly.SnapshotDigest }, e));
        BridgeSourceSnapshotV1 unsupportedGeneric = ReCapture("Fixture",
            new[] { new BridgeRootSelectorV1("Fixture", "Add", token,
                new[] { "UnqualifiedGenericInstantiation" }, Array.Empty<string>()) });
        Refuse(BridgeCscLinkRefusal.GenericInstantiation, () =>
            BridgeExecutedCscLinkV1.Write(unsupportedGeneric,
                facts with { SnapshotDigest = unsupportedGeneric.SnapshotDigest }, e));
        Refuse(BridgeCscLinkRefusal.WrongMethodToken, () =>
            BridgeExecutedCscLinkV1.Write(snapshot, facts with {
                Methods = new[] { original with { MetadataToken = token + 999 } }
            }, e));
        Refuse(BridgeCscLinkRefusal.DuplicateMethod, () =>
            BridgeExecutedCscLinkV1.Write(snapshot, facts with {
                Methods = new[] { original, original }
            }, e));
        Refuse(BridgeCscLinkRefusal.WrongMethodToken, () =>
            BridgeExecutedCscLinkV1.Write(snapshot, facts with {
                Methods = new[] { original with {
                    CanonicalSignature = "System.Void(System.Int32)" }, facts.Methods[1] }
            }, e));
        Refuse(BridgeCscLinkRefusal.UnsupportedFacts, () =>
            BridgeExecutedCscLinkV1.Write(snapshot, facts with {
                Methods = new[] { original with { AssemblyIdentity = "Foreign" },
                    facts.Methods[1] }
            }, e));
        Refuse(BridgeCscLinkRefusal.StaleMethodGraph, () =>
            BridgeExecutedCscLinkV1.Write(snapshot, facts with {
                Edges = Array.Empty<BridgeEdgeFactV1>()
            }, e));
        Refuse(BridgeCscLinkRefusal.StaleMethodGraph, () =>
            BridgeExecutedCscLinkV1.Write(snapshot, facts with {
                Methods = new[] { original with { DirectCallees = Array.Empty<string>() },
                    facts.Methods[1] }
            }, e));
        Refuse(BridgeCscLinkRefusal.WrongSourceMap, () =>
            BridgeExecutedCscLinkV1.Write(snapshot, facts with {
                SourceMaps = new[] { facts.SourceMaps[0] with { Line = 99999 },
                    facts.SourceMaps[1] }
            }, e));
        Refuse(BridgeCscLinkRefusal.WrongSourceMap, () =>
            BridgeExecutedCscLinkV1.Write(snapshot, facts with {
                SourceMaps = new[] { facts.SourceMaps[0], facts.SourceMaps[0],
                    facts.SourceMaps[1] }
            }, e));
        Refuse(BridgeCscLinkRefusal.WrongSourceMap, () =>
            BridgeExecutedCscLinkV1.Write(snapshot, facts with {
                SourceMaps = new[] { facts.SourceMaps[0] with {
                    Checksum = new string('0', 64) }, facts.SourceMaps[1] }
            }, e));
        Refuse(BridgeCscLinkRefusal.UnsupportedFacts, () =>
            BridgeExecutedCscLinkV1.Write(snapshot, facts with { Status = "Verified" }, e));
        Refuse(BridgeCscLinkRefusal.InvalidPin, () =>
            BridgeExecutedCscLinkV1.ReadAndValidate(written, snapshot, facts,
                e with { CompilerImage = e.CompilerImage[..^1] }, e.SdkVersion));
        Refuse(BridgeCscLinkRefusal.InvalidPin, () =>
            BridgeExecutedCscLinkV1.ReadAndValidate(written, snapshot, facts, e, "wrong-sdk"));

        string json = Encoding.UTF8.GetString(written);
        byte[] duplicated = Encoding.UTF8.GetBytes(json.Replace(
            "\"Schema\":\"" + BridgeExecutedCscLinkV1.SchemaId + "\",",
            "\"Schema\":\"" + BridgeExecutedCscLinkV1.SchemaId + "\",\"Schema\":\"" +
                BridgeExecutedCscLinkV1.SchemaId + "\",", StringComparison.Ordinal));
        Refuse(BridgeCscLinkRefusal.DuplicateProperty, () =>
            BridgeExecutedCscLinkV1.ReadAndValidate(duplicated, snapshot, facts, e, e.SdkVersion));
        Refuse(BridgeCscLinkRefusal.NonCanonical, () =>
            BridgeExecutedCscLinkV1.ReadAndValidate(Encoding.UTF8.GetBytes(
                json.Replace(",\"Payload\":", ", \"Payload\":", StringComparison.Ordinal)),
                snapshot, facts, e, e.SdkVersion));
        Refuse(BridgeCscLinkRefusal.UnsupportedSchema, () =>
            BridgeExecutedCscLinkV1.ReadAndValidate(Encoding.UTF8.GetBytes(
                json.Replace(BridgeExecutedCscLinkV1.SchemaId,
                    "hybridcpu.hardwarebridge.executed-csc-link/v2",
                    StringComparison.Ordinal)), snapshot, facts, e, e.SdkVersion));

        // The *second* PE/PDB are emitted by another actual Csc invocation.
        string replacementDir = Path.Combine(Path.GetDirectoryName(sourcesDir)!, "replacement");
        Directory.CreateDirectory(replacementDir);
        string replacementSource = Path.Combine(replacementDir, "Fixture.cs");
        File.WriteAllText(replacementSource, "public static class Fixture { public static int Add(int a, int b) => a-b; }");
        string replacementPe = Path.Combine(replacementDir, "Fixture.dll");
        string replacementPdb = Path.Combine(replacementDir, "Fixture.pdb");
        var rebuilt = Compile(e.CompilerPath, replacementSource, replacementPe, replacementPdb, referencePath);
        Check(Hash(rebuilt.Pe) != Hash(result.Pe) && Hash(rebuilt.Pdb) != Hash(result.Pdb),
            "second actual Csc must yield changed PE and PDB");
        Refuse(BridgeCscLinkRefusal.WrongPePdb, () =>
            BridgeExecutedCscLinkV1.Write(snapshot, facts,
                e with { PeImage = rebuilt.Pe, PdbImage = rebuilt.Pdb }));
        BridgeCompilerFactsV1 mixedPairFacts = facts with {
            PdbSha256 = Hash(rebuilt.Pdb),
            SourceMaps = facts.SourceMaps.Select(row => row with {
                PdbSha256 = Hash(rebuilt.Pdb)
            }).ToArray()
        };
        Refuse(BridgeCscLinkRefusal.WrongPePdb, () =>
            BridgeExecutedCscLinkV1.Write(snapshot, mixedPairFacts,
                e with { PdbImage = rebuilt.Pdb }));
        Check(File.ReadAllBytes(e.Sources[0].CscPath).SequenceEqual(originalSource),
            "original fixture source unexpectedly changed");
        Console.WriteLine("PASS: actual Csc -> 2 owned methods + local IL edge -> PE/PDB maps + strict mutations");
        Console.WriteLine("NOTRUN: actual Golden executed Csc observer, real Companion facts, Core independent signoff");
        return 0;
    }
}
