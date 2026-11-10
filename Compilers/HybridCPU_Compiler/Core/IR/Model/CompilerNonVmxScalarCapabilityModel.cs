using System;
using System.Collections.Generic;

namespace HybridCPU.Compiler.Core.IR;

public enum CompilerNonVmxScalarFeature : byte
{
    ScalarBitmanipCore = 0,
    ScalarBitfield = 1,
    ScalarAddressGeneration = 2,
    ScalarSelectCzero = 3,
    ScalarCarryLessChecksum = 4,
    ScalarSystemCounter = 5
}

/// <summary>
/// Compiler-visible Non-VMX scalar capability gate.
/// This surface may block compiler emission; it is not runtime legality evidence.
/// </summary>
public sealed class CompilerNonVmxScalarCapabilityModel
{
    private readonly HashSet<CompilerNonVmxScalarFeature> _enabledFeatures;

    private CompilerNonVmxScalarCapabilityModel(IEnumerable<CompilerNonVmxScalarFeature> enabledFeatures)
    {
        ArgumentNullException.ThrowIfNull(enabledFeatures);
        _enabledFeatures = new HashSet<CompilerNonVmxScalarFeature>(enabledFeatures);
    }

    public static CompilerNonVmxScalarCapabilityModel Default { get; } =
        Enable(
            CompilerNonVmxScalarFeature.ScalarBitmanipCore,
            CompilerNonVmxScalarFeature.ScalarBitfield,
            CompilerNonVmxScalarFeature.ScalarAddressGeneration,
            CompilerNonVmxScalarFeature.ScalarSelectCzero,
            CompilerNonVmxScalarFeature.ScalarCarryLessChecksum,
            CompilerNonVmxScalarFeature.ScalarSystemCounter);

    public static CompilerNonVmxScalarCapabilityModel Disabled { get; } =
        Enable();

    public static CompilerNonVmxScalarCapabilityModel Enable(
        params CompilerNonVmxScalarFeature[] enabledFeatures) =>
        new(enabledFeatures);

    public bool Supports(CompilerNonVmxScalarFeature feature) =>
        _enabledFeatures.Contains(feature);

    public void Require(CompilerNonVmxScalarFeature feature, string mnemonic)
    {
        if (Supports(feature))
        {
            return;
        }

        throw new InvalidOperationException(
            $"{mnemonic} compiler emission requires Non-VMX scalar capability {feature}.");
    }
}
