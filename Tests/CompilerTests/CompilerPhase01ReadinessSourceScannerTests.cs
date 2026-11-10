using System;
using System.Linq;
using HybridCPU_ISE.Tests.TestHelpers;
using Xunit;

namespace HybridCPU_ISE.Tests.CompilerTests;

/// <summary>
/// PR-1 source guardrails. These tests protect the compiler/ISE boundary;
/// they do not claim that green compiler tests grant runtime legality.
/// </summary>
public sealed class CompilerPhase01ReadinessSourceScannerTests
{
    [Theory]
    [InlineData("if (vector.UsesScalarVectorDotOrBackendFallback) throw new Exception();")]
    [InlineData("var stamp = new Stamp(vector.UsesScalarHelperFallback);")]
    [InlineData("if (IsNullableInt32FallbackParameter(scalarSignature)) return false;")]
    [InlineData("long result = outerPresent ? scalar : fallback;")]
    public void FallbackEvidenceAndNullableValues_AreNotCrossContourOperations(string source)
    {
        Assert.DoesNotContain(ProductionLoweringReadinessScanner.ScanText(
            "HybridCPU_Compiler/Core/IR/Lowering/Provider.cs", source),
            row => row.Rule == ProductionLoweringReadinessScanner.HiddenFallbackRule);
    }

    [Fact]
    public void CurrentCompilerSource_HasNoAuthorityStrengtheningShapes()
    {
        IReadOnlyList<ProductionLoweringReadinessViolation> violations = ProductionLoweringReadinessScanner.ScanCompilerSources()
            // ManagedPortSmoke contains C# input fixtures, not compiler lowering implementations.
            .Where(violation => !(violation.Rule == ProductionLoweringReadinessScanner.HiddenFallbackRule &&
                violation.RelativePath.Replace('\\', '/').EndsWith("/Tools/ManagedPortSmoke/NullableValueSmoke.cs", StringComparison.Ordinal)))
            .ToArray();

        Assert.True(
            violations.Count == 0,
            string.Join(Environment.NewLine, violations.Select(static violation => violation.ToString())));
    }

    [Theory]
    [InlineData(
        ProductionLoweringReadinessScanner.LegacyAuthorityPromotionRule,
        "public bool ProductionAllowed => descriptor.IsLegal;")]
    [InlineData(
        ProductionLoweringReadinessScanner.ParserHelperPromotionRule,
        "var status = validation.IsDescriptorAbiAccepted ? CompilerProductionLoweringStatus.Production : CompilerProductionLoweringStatus.Rejected;")]
    [InlineData(
        ProductionLoweringReadinessScanner.CarrierLifecyclePromotionRule,
        "return PublishCarrier(carrier);")]
    [InlineData(
        ProductionLoweringReadinessScanner.RuntimeGuardAuthorityRule,
        "bool ProductionAllowed = guardObservation.ObservedGuardAllowsProgress;")]
    [InlineData(
        ProductionLoweringReadinessScanner.VmxEmissionRule,
        "var instruction = new VLIW_Instruction { OpCode = (uint)InstructionsEnum.VMXON };")]
    [InlineData(
        ProductionLoweringReadinessScanner.SecureComputeEmissionRule,
        "return new CompilerPositiveEmissionResult<SecureComputePlan>(decision, plan, sourceApi, reason);")]
    [InlineData(
        ProductionLoweringReadinessScanner.HiddenFallbackRule,
        "return ScalarFallback(vectorRequest);")]
    [InlineData(
        ProductionLoweringReadinessScanner.DescriptorlessL7Rule,
        "const uint opCode = (uint)InstructionsEnum.ACCEL_SUBMIT;")]
    [InlineData(
        ProductionLoweringReadinessScanner.ContourMixRule,
        "new InstructionSlotMetadata { DmaStreamComputeDescriptor = dsc, AcceleratorCommandDescriptor = l7 };")]
    [InlineData(
        ProductionLoweringReadinessScanner.RawPublicBoolRule,
        "public bool IsAllowed => true;")]
    [InlineData(
        ProductionLoweringReadinessScanner.ForbiddenAuthorityTokenRule,
        "return ProductionAllowedByExplicitCompilerGate;")]
    public void SyntheticAuthorityPromotion_IsRejected(
        string expectedRule,
        string sourceLine)
    {
        IReadOnlyList<ProductionLoweringReadinessViolation> violations = ProductionLoweringReadinessScanner.ScanText(
            "HybridCPU_Compiler/Core/IR/Lowering/ProductionBadProvider.cs",
            "public sealed class ProductionBadProvider\n{\n    " + sourceLine + "\n}");

        Assert.Contains(violations, violation => violation.Rule == expectedRule);
    }

    [Fact]
    public void ExistingCompatibilityAdapters_RequireTypedEvidenceAndRuntimePendingMetadata()
    {
        IReadOnlyList<ProductionLoweringReadinessViolation> violations = ProductionLoweringReadinessScanner.ScanText(
            "HybridCPU_Compiler/Core/IR/Lowering/ExistingCompatibilityAdapter.cs",
            """
            public sealed class ExistingCompatibilityAdapter
            {
                [Obsolete]
                public bool IsAllowed => _isAllowedObservation;

                private const CompilerPublicationClass Publication = CompilerPublicationClass.EvidenceOnly;
                private const CompilerRuntimeAuthorityDependency Pending =
                    CompilerRuntimeAuthorityDependency.RuntimeLegalityARequired |
                    CompilerRuntimeAuthorityDependency.RuntimeLegalityBRequired;

                public CompilerLoweringDecision FromLegacy(bool sourceValue) =>
                    CompilerLoweringDecision.FromLegacyStructuralBool(
                        sourceValue,
                        "ExistingCompatibilityAdapter.IsAllowed",
                        SemanticIntentKind.Unknown,
                        ExecutionContourKind.UnknownRejected,
                        "Structural evidence only; RuntimeLegalityARequired and RuntimeLegalityBRequired remain pending.");
            }
            """);

        Assert.DoesNotContain(
            violations,
            violation => violation.Rule == ProductionLoweringReadinessScanner.RawPublicBoolRule);
    }

    [Fact]
    public void NegativeBoundaryText_IsNotMisclassifiedAsFallbackOrBackendEmission()
    {
        IReadOnlyList<ProductionLoweringReadinessViolation> violations = ProductionLoweringReadinessScanner.ScanText(
            "HybridCPU_Compiler/Core/IR/Contours/ContourProviderContracts.cs",
            """
            // MatrixTile scalar/vector/Stream fallback is forbidden.
            // VMX projection/no-emission; SecureCompute backend execution is forbidden.
            public sealed class NegativeBoundaryText { }
            """);

        Assert.Empty(violations);
    }
}
