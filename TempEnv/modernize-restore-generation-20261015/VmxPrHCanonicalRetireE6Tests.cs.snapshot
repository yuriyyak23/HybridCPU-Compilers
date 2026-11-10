using System.Reflection;
using YAKSys_Hybrid_CPU.Core;
using YAKSys_Hybrid_CPU.Arch;
using YAKSys_Hybrid_CPU.Arch.Generated;
using YAKSys_Hybrid_CPU.Core.Contracts;
using YAKSys_Hybrid_CPU.Core.Decoder;
using YAKSys_Hybrid_CPU.Core.Execution;
using YAKSys_Hybrid_CPU.Core.Memory;

namespace HybridCPU_ISE.Tests.VmxRefactoring;

public sealed class VmxPrHCanonicalRetireE6Tests
{
    [Fact]
    public void CanonicalHead_ConsumesLiveE5IntoOpaqueE6_ThenRetiresNoStateExactlyOnce()
    {
        var fixture = ExecuteToE5();
        DomainHypercallRetireOwner owner = fixture.Core.ExactHypercallRetireOwner;
        DomainHypercallRetireEligibility eligibility = Eligibility(fixture);
        DomainHypercallRetireResult issued = owner.Issue(
            fixture.CompletionOwner!, fixture.Carrier.ExactHypercallCompletionPublication!.Value,
            fixture.RestoreOwner, eligibility);

        DomainHypercallRetireOwner.VirtualizationRetireGrant e6 = Assert.IsType<DomainHypercallRetireOwner.VirtualizationRetireGrant>(issued.E6);
        Assert.True(issued.IsIssued);
        Assert.Equal(fixture.Carrier.VirtualizationAdmission!.AttemptId, e6.AttemptId);
        Assert.Equal(eligibility.RetireWindowIdentity, e6.RetireWindowIdentity);
        Assert.True(owner.ValidateLive(e6, fixture.RestoreOwner, eligibility.RetireWindowIdentity, eligibility.OrderEpoch));
        Assert.True(owner.ConsumeAtPreciseRetire(e6, fixture.RestoreOwner, eligibility.RetireWindowIdentity, eligibility.OrderEpoch));
        Assert.False(owner.ConsumeAtPreciseRetire(e6, fixture.RestoreOwner, eligibility.RetireWindowIdentity, eligibility.OrderEpoch));
        Assert.False(fixture.CompletionOwner!.ValidateLive(
            fixture.Carrier.ExactHypercallCompletionPublication.Value.E5,
            fixture.Carrier.ExactHypercallCompletionPublication.Value.Completion,
            fixture.RestoreOwner));
        Assert.False(fixture.Carrier.ExactHypercallExecutionReceipt!.HasStateEffect);
        Assert.False(fixture.Carrier.ExactHypercallExecutionReceipt.HasPayload);
    }

    [Fact]
    public void RealWriteBackRetireWindow_IssuesAndConsumesE6_WithoutCompatibilitySuccessEffect()
    {
        var fixture = ExecuteToE5();
        PostStageBIssuedAttempt attempt = CreateMatchingIssuedAttempt(fixture);
        fixture.Core.TestPrepareExactHypercallForWriteBack(fixture.Carrier, attempt);

        fixture.Core.TestRunWriteBackStage();

        DomainHypercallRetireOwner.VirtualizationRetireGrant e6 =
            Assert.IsType<DomainHypercallRetireOwner.VirtualizationRetireGrant>(
                fixture.Carrier.ExactHypercallRetireGrant);
        Assert.False(fixture.Core.ExactHypercallRetireOwner.ValidateLive(
            e6,
            fixture.RestoreOwner,
            fixture.Carrier.ExactHypercallRetireWindowIdentity,
            fixture.Carrier.ExactHypercallRetireOrderEpoch));
        Assert.True(fixture.Carrier.CreateRetireEffect().IsFaulted);
        Assert.False(fixture.Carrier.ExactHypercallExecutionReceipt!.HasStateEffect);
    }

    [Fact]
    public void NonHeadWrongIdentityAndSquash_DenyWithoutConsumingE5()
    {
        var fixture = ExecuteToE5();
        DomainHypercallRetireOwner owner = fixture.Core.ExactHypercallRetireOwner;
        DomainHypercallRetireEligibility canonical = Eligibility(fixture);

        Assert.Equal(DomainHypercallRetireDecision.NotCanonicalHead,
            owner.Issue(fixture.CompletionOwner!, fixture.Carrier.ExactHypercallCompletionPublication!.Value,
                fixture.RestoreOwner, canonical with { IsCanonicalHead = false, RetireOrderIndex = 1 }).Decision);
        Assert.Equal(DomainHypercallRetireDecision.IdentityMismatch,
            owner.Issue(fixture.CompletionOwner!, fixture.Carrier.ExactHypercallCompletionPublication.Value,
                fixture.RestoreOwner, canonical with { WorkingSlotId = canonical.WorkingSlotId == 7 ? 6 : 7 }).Decision);
        Assert.Equal(DomainHypercallRetireDecision.NotCanonicalHead,
            owner.Issue(fixture.CompletionOwner!, fixture.Carrier.ExactHypercallCompletionPublication.Value,
                fixture.RestoreOwner, canonical with { IsSquashed = true }).Decision);
        Assert.True(fixture.CompletionOwner!.ValidateLive(
            fixture.Carrier.ExactHypercallCompletionPublication.Value.E5,
            fixture.Carrier.ExactHypercallCompletionPublication.Value.Completion,
            fixture.RestoreOwner));
    }

    [Fact]
    public void DuplicateAndForeignRetireOwner_CannotReuseConsumedE5()
    {
        var fixture = ExecuteToE5();
        DomainHypercallRetireEligibility eligibility = Eligibility(fixture);
        DomainHypercallRetireOwner owner = fixture.Core.ExactHypercallRetireOwner;
        Assert.True(owner.Issue(
            fixture.CompletionOwner!, fixture.Carrier.ExactHypercallCompletionPublication!.Value,
            fixture.RestoreOwner, eligibility).IsIssued);

        Assert.Equal(DomainHypercallRetireDecision.DuplicateOrForeignE5,
            owner.Issue(fixture.CompletionOwner!, fixture.Carrier.ExactHypercallCompletionPublication.Value,
                fixture.RestoreOwner, eligibility).Decision);
        Assert.Equal(DomainHypercallRetireDecision.DuplicateOrForeignE5,
            new DomainHypercallRetireOwner().Issue(
                fixture.CompletionOwner!, fixture.Carrier.ExactHypercallCompletionPublication.Value,
                fixture.RestoreOwner, eligibility).Decision);
    }

    [Fact]
    public void RestoreInvalidatesE5AndE6IssuanceFailsClosed()
    {
        var fixture = ExecuteToE5();
        fixture.RestoreOwner.AdvanceAfterRestore();
        DomainHypercallRetireResult denied = fixture.Core.ExactHypercallRetireOwner.Issue(
            fixture.CompletionOwner!, fixture.Carrier.ExactHypercallCompletionPublication!.Value,
            fixture.RestoreOwner, Eligibility(fixture));
        Assert.Equal(DomainHypercallRetireDecision.StaleRestoreGeneration, denied.Decision);
        Assert.Null(denied.E6);
    }

    [Fact]
    public void E6_IsOpaqueAndCompatibilityEffectRemainsNonAuthority()
    {
        ConstructorInfo constructor = Assert.Single(
            typeof(DomainHypercallRetireOwner.VirtualizationRetireGrant)
                .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic));
        Assert.True(constructor.IsPrivate);
        Assert.Empty(typeof(DomainHypercallRetireOwner.VirtualizationRetireGrant).GetConstructors());
        Assert.DoesNotContain(
            typeof(DomainHypercallRetireOwner).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic),
            method => method.GetParameters().Any(parameter => parameter.ParameterType == typeof(bool)));
    }

    private static VmxPrFCanonicalHypercallCompositionTests.Fixture ExecuteToE5()
    {
        var fixture = VmxPrFCanonicalHypercallCompositionTests.CreateFixture(
            configure: true, completion: true, retirement: true);
        Assert.True(VmxPrFCanonicalHypercallCompositionTests.Materialize(fixture, 1));
        YAKSys_Hybrid_CPU.Processor.CPU_Core core = fixture.Core;
        Assert.True(fixture.Carrier.Execute(ref core));
        Assert.True(fixture.Carrier.ExactHypercallCompletionPublication!.Value.IsPublished);
        return fixture;
    }

    private static DomainHypercallRetireEligibility Eligibility(
        VmxPrFCanonicalHypercallCompositionTests.Fixture fixture)
    {
        SafetyVerifier.VirtualizationAdmissionCertificate e1 = fixture.Carrier.VirtualizationAdmission!;
        return new(
            fixture.Carrier, e1.VirtualThreadId, e1.DomainTag,
            e1.SourceSlotId, e1.WorkingSlotId, e1.BundleIdentity,
            OperationAttempt: 1, PhysicalLaneId: e1.WorkingSlotId,
            RetireOrderIndex: 0, RetireWindowIdentity: 101, OrderEpoch: 201,
            IsCanonicalHead: true, IsSquashed: false, HasWinningException: false);
    }

    private static PostStageBIssuedAttempt CreateMatchingIssuedAttempt(
        VmxPrFCanonicalHypercallCompositionTests.Fixture fixture)
    {
        SafetyVerifier.VirtualizationAdmissionCertificate e1 = fixture.Carrier.VirtualizationAdmission!;
        Assert.True(GeneratedIsaCatalog.TryGetDescriptor(
            (uint)YAKSys_Hybrid_CPU.Processor.CPU_Core.IsaOpcodeValues.ADD,
            out GeneratedIsaDescriptor descriptor));
        GeneratedStaticBinding binding = GeneratedStaticBinding.FromDescriptor(in descriptor);
        ExecutionContract contract = ExecutionContract.Create(
            binding,
            new RuntimeExecutionProviderBinding(binding.RuntimeExecutionProviderId, "pr-h-retire-identity"),
            InstructionClass.ScalarAlu,
            SerializationClass.Free,
            ExecutionPlacement.Create(SlotClass.AluClass, SlotPinningKind.ClassFlexible),
            "NoState",
            MemoryCapability.None,
            isRetireVisible: true,
            isAssist: false);
        AdmissionRecord admission = AdmissionRecord.Create(
            new SourceOperationProvenance(
                SemanticInstructionKey.Create([1], "pr-h-retire-identity", CanonicalDecodeContext.Unbound),
                e1.VirtualThreadId,
                e1.BundleIdentity,
                SlotId.Create(e1.SourceSlotId),
                fetchEpoch: 1),
            contract,
            e1.VirtualThreadId,
            e1.OwnerContextId,
            e1.DomainTag);
        return PostStageBIssuedAttempt.CreateAfterSuccessfulStageB(
            new PostStageBIdentityTemplate(
                admission,
                e1.BundleIdentity,
                SlotId.Create(e1.WorkingSlotId),
                new OperationAttemptIssuer()),
            LaneId.Create(7));
    }
}
