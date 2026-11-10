using System.Text;
using System.Security.Cryptography;
using CpuInterfaceBridge;
using CpuInterfaceBridge.Diagnostics;
using YAKSys_Hybrid_CPU;
using System.Text.Json;

int passed = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); passed++; }
void Reject(Action action, string name) { try { action(); } catch (Exception e) when (e is System.Text.Json.JsonException or InvalidDataException or InvalidOperationException or FormatException or OverflowException) { Check(true, name); return; } throw new Exception("Accepted " + name); }
string fixture = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "runner-v1.json"));
DiagnosticSessionV1 Import(string json, byte[]? bytes = null) => RunnerReportImporter.Import(new MemoryStream(Encoding.UTF8.GetBytes(json)), Guid.NewGuid(), "synthetic fixture", bytes is null ? null : new MemoryStream(bytes));
DiagnosticSessionV1 session = Import(fixture);
Check(session.Safepoints.State == EvidenceState.Available && session.Safepoints.Value == 0 && session.RootCount.State == EvidenceState.Unavailable, "zero versus absent evidence");
Check(session.Image.Verification == DigestVerification.NotProvided && session.Qualification.State == EvidenceState.Unavailable && session.LoaderStatus.State == EvidenceState.Unavailable, "independent statuses and absent SHA");
Check(session.Ecalls[0].Receiver == 9 && session.Ecalls[0].ProviderReason.State == EvidenceState.Unavailable && DiagnosticQueries.Failure(session).ExactReportReason == session.Reason, "ECALL and exact report reason");
Reject(() => Import("{}"), "missing schema");
Reject(() => Import(fixture.Replace("/v1", "/v2")), "unsupported schema");
Reject(() => Import(fixture.Replace("258", "-1")), "negative PC");
Reject(() => Import(fixture.Replace("258", "18446744073709551616")), "PC overflow");
Reject(() => Import(fixture.Replace("512", "256")), "empty code range");
Reject(() => Import(fixture.Replace("\"status\":", "\"status\":\"Completed\",\"status\":")), "duplicate property");
Reject(() => Import(fixture[..40]), "truncated JSON");
Evidence<MethodLocation> location = DiagnosticQueries.Locate(258, session.CodeRecords);
Check(location.Value?.NativeOffset == 2 && location.Value.Assembly is null, "method native offset and unavailable assembly");
Check(DiagnosticQueries.Locate(512, session.CodeRecords).State == EvidenceState.Unavailable, "half open boundary");
Check(DiagnosticQueries.Locate(258, session.CodeRecords.Append(new("Other", 257, 300))).State == EvidenceState.Contradictory, "ambiguous code records");
Reject(() => DiagnosticQueries.FromOffset("M", ulong.MaxValue, 1, 1), "checked address addition");
Check(DiagnosticQueries.CompareHandle(session, 1, 4096).Value && !DiagnosticQueries.CompareHandle(session, 1, 9).Value && DiagnosticQueries.FindType(session, 2).State == EvidenceState.Unavailable, "runtime handle lookup");
Check(DiagnosticQueries.CilOffset(session, location.Value!).State == EvidenceState.Unavailable, "absent CIL mapping");
byte[] image = [1, 2, 3];
string sha = Convert.ToHexString(SHA256.HashData(image)).ToLowerInvariant();
string withSha = fixture.Replace("\"image\":", $"\"imageSha256\":\"{sha}\",\"image\":");
DiagnosticSessionV1 verified = Import(withSha, image);
Check(verified.Image.Verification == DigestVerification.VerifiedAgainstSuppliedBytes, "SHA byte verification");
Check(Import(withSha, [4]).Image.Verification == DigestVerification.Mismatch, "SHA mismatch");
Check(Import(fixture, image).Image.Verification == DigestVerification.BytesOnlyUnbound, "bytes cannot bind SHA-less report");
Check(DiagnosticQueries.CilOffset(verified, location.Value!, [new(sha, "Doom.Tick", 2, 17, "explicit fixture map")]).Value == 17, "exact bound CIL mapping");
ReportComparison comparison = DiagnosticQueries.Compare(session, Import(fixture.Replace("ExecutionFault", "CycleBudgetExceeded")));
Check(comparison.DifferentImportSessions && comparison.DifferentRuns.State == EvidenceState.Unavailable && !comparison.SameObservedFailure && comparison.Assessment.Contains("does not"), "report comparison never proves resolution");
DiagnosticEventHub<int> hub = new DiagnosticEventHub<int>(session.SessionId);
using DiagnosticSubscription<int> a = hub.Subscribe(1);
using DiagnosticSubscription<int> b = hub.Subscribe(3);
hub.Publish(1); hub.Publish(2); hub.Publish(3); hub.Complete();
List<DiagnosticEvent<int>> aa = new List<DiagnosticEvent<int>>(); await foreach (DiagnosticEvent<int> e in a.ReadAllAsync()) aa.Add(e);
List<DiagnosticEvent<int>> bb = new List<DiagnosticEvent<int>>(); await foreach (DiagnosticEvent<int> e in b.ReadAllAsync()) bb.Add(e);
Check(a.DroppedEventCount == 2 && b.DroppedEventCount == 0 && aa.Count == 1 && bb.Count == 3 && bb[2].Sequence == 3 && bb[2].SessionId == session.SessionId, "bounded independent subscribers and sequences");
using DiagnosticSubscription<int> canceled = new DiagnosticEventHub<int>(session.SessionId).Subscribe();
using CancellationTokenSource cts = new CancellationTokenSource(); cts.Cancel();
try { await foreach (DiagnosticEvent<int> e in canceled.ReadAllAsync(cts.Token)) { } throw new Exception("Cancellation missing"); } catch (OperationCanceledException) { Check(true, "subscriber cancellation"); }
CompileResult compile = await new LegacyCompilerService().CompileAsync(new("code"), new());
Check(!compile.Success && compile.Errors.Single() == "No compilation action configured.", "default compile fails despite populated cache double");
compile = await new LegacyCompilerService((_, _, _) => ValueTask.CompletedTask).CompileAsync(new("code"), new());
Check(!compile.Success && compile.Errors.Single().Contains("freshness"), "action cannot prove cache freshness");
LegacyCompilerService owned = LegacyCompilerService.WithOwnedResults((_, _, _) => ValueTask.FromResult<IReadOnlyList<CompiledProgram>>([new(0, new())]));
Check((await owned.CompileAsync(new("code"), new())).Success, "explicit invocation-owned compile results");
int commands = 0;
LegacyCompilerService logs = new LegacyCompilerService((_, _, _) => { commands++; return ValueTask.CompletedTask; });
await foreach (CompilerMessage e in logs.CompileLogStream(new("code"), new())) { }
Check(commands == 1, "CompileLogStream starts command");
LegacyEmulatorService emulator = new LegacyEmulatorService(new IseCoreStateService(new object()));
Processor.OnCycle = null;
EmulationResult result = await emulator.EmulateAsync(new() { MaxCycles = 2, ProgressEveryNCycles = 20 }, null);
Check(result.StopReason == EmulationStopReason.BudgetExhausted && result.ExecutedCycles == 2, "budget exhaustion is not completion");
result = await emulator.EmulateAsync(new() { MaxCycles = 10, ProgressEveryNCycles = 20, Breakpoints = new HashSet<ulong> { 1 } }, null);
Check(result.StopReason == EmulationStopReason.BreakpointHit && result.ExecutedCycles == 1, "breakpoint independent of sampling");
using CancellationTokenSource external = new CancellationTokenSource();
Processor.OnCycle = () => external.Cancel();
result = await emulator.EmulateAsync(new() { MaxCycles = 10 }, null, external.Token);
Check(result.StopReason == EmulationStopReason.Canceled, "cancellation without delay");
Processor.OnCycle = () => emulator.PauseAsync().GetAwaiter().GetResult();
result = await emulator.EmulateAsync(new() { MaxCycles = 10 }, null);
Check(result.StopReason == EmulationStopReason.Paused, "pause without delay");
Processor.OnCycle = () => emulator.StopAsync().GetAwaiter().GetResult();
result = await emulator.EmulateAsync(new() { MaxCycles = 10, CycleDelay = TimeSpan.FromMilliseconds(1) }, null);
Check(result.StopReason == EmulationStopReason.Stopped, "stop during delay");
Processor.OnCycle = () => throw new InvalidOperationException("exact provider failure");
result = await emulator.EmulateAsync(new() { MaxCycles = 10 }, null);
Check(result.StopReason == EmulationStopReason.Faulted && result.FailureReason == "exact provider failure", "exact legacy failure reason");
// CLI filesystem inputs/outputs stay below the explicitly isolated build output.
string cliDirectory = Path.Combine(AppContext.BaseDirectory, "cli-fixtures");
Directory.CreateDirectory(cliDirectory);
string jsonPath = Path.Combine(cliDirectory, "selected report.json");
string packagePath = Path.Combine(cliDirectory, "selected package.hcexe");
File.WriteAllText(jsonPath, fixture);
File.WriteAllBytes(packagePath, image);
(int Exit, string Output, string Error) RunReport(params string[] arguments)
{
    using StringWriter output = new StringWriter();
    using StringWriter error = new StringWriter();
    int code = HybridCpuIseImageRunner.ReportCommand.Run(arguments, output, error);
    return (code, output.ToString(), error.ToString());
}
(int Exit, string Output, string Error) cli = RunReport("--report", jsonPath);
using (JsonDocument json = System.Text.Json.JsonDocument.Parse(cli.Output))
{
    Check(cli.Exit == 0 && cli.Error.Length == 0 && json.RootElement.GetProperty("qualification").GetString() == "Unavailable" &&
        !json.RootElement.GetProperty("executionStartedByThisCommand").GetBoolean(), "CLI passive import cannot qualify or execute");
    Check(json.RootElement.GetProperty("card").GetProperty("ExactReportReason").GetString() == session.Reason, "CLI preserves exact report reason");
}
Check(RunReport("--report").Exit == 2 && RunReport("--report", jsonPath, "--unknown", packagePath).Exit == 2 &&
    RunReport("--report", jsonPath, "--image", packagePath, "extra").Exit == 2, "CLI rejects ambiguous arguments");
Check(RunReport("--report", Path.Combine(cliDirectory, "missing.json")).Exit == 3, "CLI missing explicit file");
File.WriteAllText(jsonPath, withSha);
cli = RunReport("--report", jsonPath, "--image", packagePath);
Check(cli.Exit == 0 && cli.Output.Contains("VerifiedAgainstSuppliedBytes"), "CLI verifies explicit image bytes");
File.WriteAllBytes(packagePath, [4]);
cli = RunReport("--report", jsonPath, "--image", packagePath);
Check(cli.Exit == 4 && cli.Output.Contains("ImageDigestMismatch") && cli.Error.Contains("HCISE-DIAG0004"), "CLI digest mismatch nonzero with card");
File.WriteAllText(jsonPath, fixture);
cli = RunReport("--report", jsonPath, "--image", packagePath);
Check(cli.Exit == 0 && cli.Output.Contains("BytesOnlyUnbound"), "CLI cannot bind SHA-less report to supplied bytes");
File.WriteAllText(jsonPath, "{}");
Check(RunReport("--report", jsonPath).Exit == 3, "CLI invalid report nonzero");
File.WriteAllText(jsonPath, fixture.Replace("258", "-1"));
Check(RunReport("--report", jsonPath).Exit == 3, "CLI damaged range nonzero");
await HostObservationTests.Run(Check);
Console.WriteLine($"{passed} assertions passed; bridge and runner report command source with isolated dependency doubles; no guest execution.");
