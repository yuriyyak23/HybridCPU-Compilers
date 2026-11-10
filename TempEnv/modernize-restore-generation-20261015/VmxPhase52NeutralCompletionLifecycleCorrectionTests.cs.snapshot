using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using YAKSys_Hybrid_CPU;
using YAKSys_Hybrid_CPU.Core;

namespace HybridCPU_ISE.Tests.VmxRefactoring;

public sealed class VmxPhase52NeutralCompletionLifecycleCorrectionTests
{
    [Fact]
    public void MachineCurrent_RecordsBoundedLifecycleAuthorizationWithoutVmReadAuthority()
    {
        string repositoryRoot =
            VmxDocumentationMigrationClaimHygieneTests.FindRepositoryRoot();
        string planRoot = Path.Combine(
            repositoryRoot,
            "HybridCPU_ISE", "docs", "ref2", "VirtualizationActivationPlan");
        using JsonDocument status = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            planRoot,
            "VirtualizationActivationStatusV1.json")));
        JsonElement phase52 = status.RootElement.GetProperty(
            "Phase52NeutralCompletionLifecycleCorrection");

        Assert.Equal(
            "ClosedGreenSubjectAndLaterNonSelfReferentialEvidence",
            phase52.GetProperty("State").GetString());
        Assert.Equal(
            "2246049040fe61721a00e8535ed925c63cf587dc",
            phase52.GetProperty("SubjectSha").GetString());
        Assert.Equal("Denied", phase52.GetProperty("VmReadAuthority").GetString());
        Assert.False(phase52.GetProperty("ProducerImplemented").GetBoolean());
        Assert.Equal(
            "NoneUntilSeparateOwnerAuthorizationForNextExactFieldOrOperation",
            status.RootElement.GetProperty("NextCandidatePool").GetString());

        string phase52Plan = File.ReadAllText(Path.Combine(
            planRoot,
            "52_neutral_completion_lifecycle_correction.md"));
        Assert.Contains("prevalidated before any retire-visible", phase52Plan);
        Assert.Contains("Completion-backed VMREAD remains closed", phase52Plan);
        Assert.DoesNotContain("VMX fully activated", phase52Plan);
    }

    [Fact]
    public void Evidence_IsLaterNonSelfReferentialAndHashesSubjectBytes()
    {
        const string subject = "2246049040fe61721a00e8535ed925c63cf587dc";
        const string tree = "3a59c1b2c6eb838ca26d38c0fb52f15b562b0828";
        string repositoryRoot =
            VmxDocumentationMigrationClaimHygieneTests.FindRepositoryRoot();
        string evidencePath = Path.Combine(
            repositoryRoot,
            "HybridCPU_ISE", "docs", "ref2", "VirtualizationActivationPlan", "evidence",
            "2026-08-13-phase52-neutral-completion-lifecycle-clean-evidence.json");
        using JsonDocument evidence = JsonDocument.Parse(File.ReadAllText(evidencePath));
        JsonElement root = evidence.RootElement;

        Assert.True(root.GetProperty("non_self_referential").GetBoolean());
        Assert.False(root.GetProperty("runtime_authority").GetBoolean());
        Assert.Equal(subject,
            root.GetProperty("implementation_subject").GetProperty("commit_sha").GetString());
        Assert.Equal(tree,
            root.GetProperty("implementation_subject").GetProperty("tree_sha").GetString());
        Assert.Equal(tree, GitText(repositoryRoot, "rev-parse", $"{subject}^{{tree}}"));

        foreach (JsonProperty source in root
            .GetProperty("source_hashes_sha256_clean_subject_bytes")
            .EnumerateObject())
        {
            byte[] bytes = GitBytes(
                repositoryRoot,
                "cat-file",
                "blob",
                $"{subject}:{source.Name}");
            string actual = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            Assert.Equal(source.Value.GetString(), actual);
        }

        Assert.False(root.GetProperty("completion_backed_vmread_opened").GetBoolean());
    }

    [Fact]
    public void MandatoryBatch_IsRejectedBeforeAnyCompletionPublication()
    {
        (ArchitecturalCompletionCommitOwner owner,
            ArchitecturalCompletionCommitOwner.ProducerRegistration producer) = CreateOwner();
        ArchitecturalCompletionCandidate duplicate = Candidate();

        ArchitecturalCompletionBatchPreparationResult preparation =
            owner.PrepareCanonicalRetireWindow(producer, [duplicate, duplicate]);

        Assert.False(preparation.IsPrepared);
        Assert.Equal(
            ArchitecturalCompletionCommitDecision.DeniedDuplicateOrReplay,
            preparation.Decision);
        Assert.Equal(0, owner.LiveReceiptCount);
        Assert.False(owner.TryGetLatestLiveReceipt(out _));
        Assert.Equal(
            CompletionObservationDecision.DeniedAbsent,
            owner.ObservationOwner.Observe(Scope()).Decision);
    }

    [Fact]
    public void PreparedBatch_PublishesOnlyWhenItsCanonicalLeaseCompletes()
    {
        (ArchitecturalCompletionCommitOwner owner,
            ArchitecturalCompletionCommitOwner.ProducerRegistration producer) = CreateOwner();

        ArchitecturalCompletionBatchPreparationResult preparation =
            owner.PrepareCanonicalRetireWindow(producer, [Candidate()]);

        Assert.True(preparation.IsPrepared);
        Assert.Equal(0, owner.LiveReceiptCount);
        Assert.Equal(
            CompletionObservationDecision.DeniedAbsent,
            owner.ObservationOwner.Observe(Scope()).Decision);

        ArchitecturalCompletionCommitResult result =
            Assert.Single(preparation.Lease!.Complete());
        Assert.True(result.IsCommitted);
        Assert.Equal(1, owner.LiveReceiptCount);
        Assert.True(owner.ObservationOwner.Observe(Scope()).IsObserved);
    }

    [Fact]
    public void Restore_CannotInterleaveWithPreparedCanonicalRetireWindow()
    {
        (ArchitecturalCompletionCommitOwner owner,
            ArchitecturalCompletionCommitOwner.ProducerRegistration producer) = CreateOwner();
        ArchitecturalCompletionBatchPreparationResult preparation =
            owner.PrepareCanonicalRetireWindow(producer, [Candidate()]);
        Assert.True(preparation.IsPrepared);

        using var restoreEntered = new ManualResetEventSlim();
        Task restore = Task.Run(() =>
        {
            restoreEntered.Set();
            owner.InvalidateAfterRestore();
        });
        Assert.True(restoreEntered.Wait(TimeSpan.FromSeconds(5)));
        Thread.Sleep(50);
        Assert.False(restore.IsCompleted);

        ArchitecturalCompletionCommitResult result =
            Assert.Single(preparation.Lease!.Complete());
        Assert.True(result.IsCommitted);
        Assert.True(restore.Wait(TimeSpan.FromSeconds(5)));

        Assert.False(owner.ValidateLiveReceipt(result.Receipt, result.Receipt!.Binding));
        Assert.Equal(0, owner.LiveReceiptCount);
        Assert.Equal(
            CompletionObservationDecision.DeniedAbsent,
            owner.ObservationOwner.Observe(Scope()).Decision);
    }

    [Fact]
    public void ClearAndRebind_InvalidateOnlyTheExactScopeReceiptsAndReplayIdentity()
    {
        (ArchitecturalCompletionCommitOwner owner,
            ArchitecturalCompletionCommitOwner.ProducerRegistration producer) = CreateOwner();
        ArchitecturalCompletionCommitResult first = owner.CommitAtCanonicalRetireBoundary(
            producer,
            Candidate());
        ArchitecturalCompletionCommitResult other = owner.CommitAtCanonicalRetireBoundary(
            producer,
            Candidate(domain: 18, context: 24, vt: 3, attempt: 12, eventId: 102));
        Assert.True(first.IsCommitted && other.IsCommitted);

        owner.ClearObservation(Scope());

        Assert.False(owner.ValidateLiveReceipt(first.Receipt, first.Receipt!.Binding));
        Assert.True(owner.ValidateLiveReceipt(other.Receipt, other.Receipt!.Binding));
        Assert.Equal(
            CompletionObservationDecision.DeniedAbsent,
            owner.ObservationOwner.Observe(Scope()).Decision);
        Assert.True(owner.ObservationOwner.Observe(new(18, 24, 3)).IsObserved);
        Assert.True(owner.CommitAtCanonicalRetireBoundary(producer, Candidate()).IsCommitted);

        owner.RebindObservation(Scope());
        Assert.Equal(
            CompletionObservationDecision.DeniedAbsent,
            owner.ObservationOwner.Observe(Scope()).Decision);
    }

    [Fact]
    public void OwnerReplacement_RevokesOldObservationReceiptsAndReplayIdentity()
    {
        (ArchitecturalCompletionCommitOwner owner,
            ArchitecturalCompletionCommitOwner.ProducerRegistration producer) = CreateOwner();
        ArchitecturalCompletionCommitResult committed =
            owner.CommitAtCanonicalRetireBoundary(producer, Candidate());
        DomainCompletionObservationOwner previous = owner.ObservationOwner;

        DomainCompletionObservationOwner replacement =
            owner.ReplaceObservationOwnerAfterArchitecturalStateReplacement();

        Assert.Equal(
            CompletionObservationDecision.DeniedInactiveOwner,
            previous.Observe(Scope()).Decision);
        Assert.Equal(
            CompletionObservationDecision.DeniedAbsent,
            replacement.Observe(Scope()).Decision);
        Assert.False(owner.ValidateLiveReceipt(committed.Receipt, committed.Receipt!.Binding));
        Assert.Equal(0, owner.LiveReceiptCount);
        Assert.True(owner.CommitAtCanonicalRetireBoundary(producer, Candidate()).IsCommitted);
    }

    [Fact]
    public void ProductionLifecycleCallers_AreCanonicalStateReplacementAndRestoreSeams()
    {
        string stateData = ActiveVmxConformanceHelpers.ReadProjectSource(
            "CloseToHSL/Core/Architecture/State/Architectural/CPU_Core.StateData.cs");
        string state = ActiveVmxConformanceHelpers.ReadProjectSource(
            "CloseToHSL/Core/Architecture/State/Architectural/CPU_Core.State.cs");
        string pipeline = ActiveVmxConformanceHelpers.ReadProjectSource(
            "CloseToHSL/Core/Pipeline/Stages/Issue/CPU_Core.Pipeline.cs");

        Assert.Contains(
            "ReplaceObservationOwnerAfterArchitecturalStateReplacement();",
            stateData);
        Assert.Contains("ResetExecutionStartPcState(pc, activeVtId);", pipeline);
        Assert.Contains(
            "ArchitecturalCompletionCommitOwner.InvalidateAfterRestore();",
            state);
        Assert.Contains("public void RestoreVectorContext(VectorContext ctx)", state);
    }

    private static (
        ArchitecturalCompletionCommitOwner Owner,
        ArchitecturalCompletionCommitOwner.ProducerRegistration Producer) CreateOwner()
    {
        var owner = new ArchitecturalCompletionCommitOwner(
            new VirtualizationRestoreGenerationOwner(),
            new DomainCompletionObservationOwner(new CompletionGenerationAuthority()));
        ArchitecturalCompletionCommitOwner.ProducerRegistration producer =
            owner.RegisterProducer(new ArchitecturalCompletionProducerPolicy(
                "CanonicalPipelineTrapEntryProducer",
                NeutralArchitecturalCompletionClass.TrapEntry,
                RequiresReason: true,
                AllowsQualification: false,
                NeutralFaultAddressSemantic.VirtualAddress,
                NeutralFaultAuxiliarySemantic.None));
        return (owner, producer);
    }

    private static CompletionObservationScope Scope() => new(17, 23, 2);

    private static string GitText(string workingDirectory, params string[] arguments) =>
        System.Text.Encoding.UTF8.GetString(GitBytes(workingDirectory, arguments)).TrimEnd();

    private static byte[] GitBytes(string workingDirectory, params string[] arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };
        foreach (string argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);
        Assert.True(process.Start());
        using var output = new MemoryStream();
        process.StandardOutput.BaseStream.CopyTo(output);
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error);
        return output.ToArray();
    }

    private static ArchitecturalCompletionCandidate Candidate(
        ulong domain = 17,
        int context = 23,
        int vt = 2,
        ulong attempt = 11,
        ulong eventId = 101) =>
        new(
            domain,
            context,
            vt,
            attempt,
            eventId,
            new NeutralArchitecturalCompletionFacts(
                NeutralArchitecturalCompletionClass.TrapEntry,
                NeutralScalarFact.Present(2),
                NeutralScalarFact.Absent,
                NeutralAddressFact.Absent,
                NeutralAuxiliaryFact.Absent));
}
