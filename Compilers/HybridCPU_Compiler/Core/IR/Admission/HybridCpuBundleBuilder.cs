using System;
using System.Collections.Generic;
using System.Threading;
using HybridCPU.Compiler.Core.IR.Resources;

namespace HybridCPU.Compiler.Core.IR
{
    /// <summary>
    /// Stage 7 entry point that orchestrates bundle formation, typed-slot validation,
    /// safety-mask diagnostic preflight, and admission annotation.
    /// </summary>
    /// <remarks>
    /// <para>Pipeline:</para>
    /// <list type="number">
    ///   <item>Stage 6 <see cref="HybridCpuBundleFormer"/> produces legal bundles.</item>
    ///   <item><see cref="HybridCpuTypedSlotFactsEmitter"/> emits and validates typed-slot facts (primary structural check).</item>
    ///   <item><see cref="SafetyMaskDiagnosticChecker"/> validates intra-bundle safety-mask diagnostics (secondary structural check).</item>
    ///   <item><see cref="HybridCpuStealabilityAnalyzer"/> derives per-instruction steal verdicts (advisory only).</item>
    ///   <item>Admission results and the <see cref="IrAdmissibilityAgreement"/> are assembled.</item>
    /// </list>
    /// <para>
    /// Compiler preflight always emits and validates typed-slot facts, but that does
    /// not by itself mean the runtime mainline already requires them. The canonical
    /// repository-facing staging surface remains
    /// <see cref="TypedSlotFactStaging.CurrentMode"/> ==
    /// <see cref="TypedSlotFactMode.ValidationOnly"/>.
    /// </para>
    /// </remarks>
    public sealed class HybridCpuBundleBuilder
    {
        private readonly HybridCpuStealabilityAnalyzer _stealAnalyzer = new();
        private readonly SafetyMaskDiagnosticChecker _safetyMaskChecker = new();

        /// <summary>
        /// Runs the full Stage 7 admission pipeline on a bundled program.
        /// </summary>
        public IrAdmissibilityAgreement BuildAgreement(IrProgramBundlingResult bundlingResult)
            => BuildAgreement(CancellationToken.None, bundlingResult);

        public IrAdmissibilityAgreement BuildAgreement(CancellationToken cancellationToken, IrProgramBundlingResult bundlingResult)
        {
            ArgumentNullException.ThrowIfNull(bundlingResult);

            cancellationToken.ThrowIfCancellationRequested();
            List<IrBundleAdmissionResult> bundleResults = new List<IrBundleAdmissionResult>();

            foreach (IrBasicBlockBundlingResult blockResult in bundlingResult.BlockResults)
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (IrMaterializedBundle bundle in blockResult.Bundles)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    bundleResults.Add(AnalyzeBundle(bundle));
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            return new IrAdmissibilityAgreement(bundleResults);
        }

        /// <summary>
        /// Analyzes a single materialized bundle and produces an admission result.
        /// </summary>
        public IrBundleAdmissionResult AnalyzeBundle(IrMaterializedBundle bundle)
        {
            ArgumentNullException.ThrowIfNull(bundle);

            IReadOnlyList<StealabilityVerdict> stealVerdicts = _stealAnalyzer.AnalyzeBundle(bundle);
            SafetyMaskDiagnosticResult safetyDiagnostic = _safetyMaskChecker.CheckBundle(bundle);
            IrTypedSlotBundleFacts facts = HybridCpuTypedSlotFactsEmitter.EmitFacts(bundle);
            bool factsValid = HybridCpuTypedSlotFactsEmitter.ValidateEmittedFacts(facts);

            AdmissibilityClassification classification = Classify(safetyDiagnostic, stealVerdicts, facts, factsValid);

            return new IrBundleAdmissionResult(
                bundle.Cycle,
                classification,
                safetyDiagnostic.ToCompatibilityResult(),
                stealVerdicts,
                TypedSlotFacts: facts,
                TypedSlotFactsValid: factsValid);
        }

        private static AdmissibilityClassification Classify(
            SafetyMaskDiagnosticResult safetyDiagnostic,
            IReadOnlyList<StealabilityVerdict> stealVerdicts,
            IrTypedSlotBundleFacts facts,
            bool factsValid)
        {
            // Primary within compiler preflight: typed-slot structural validity is the
            // authoritative handoff check. Current runtime mainline still remains
            // ValidationOnly with respect to missing producer facts.
            if (!factsValid && !facts.IsEmpty)
            {
                // Compiler-only structural preflight buckets. These do not have
                // one-to-one runtime reject codes; the active runtime mainline
                // should not see them if the contract holds.
                HybridCpuMachineDescriptionV1 machine = HybridCpuMachineDescriptionV1.Default;
                if (facts.AluCount > machine.GetSlotClassCapacity(IrSlotClass.AluClass)
                    || facts.LsuCount > machine.GetSlotClassCapacity(IrSlotClass.LsuClass)
                    || facts.DmaStreamCount > machine.GetSlotClassCapacity(IrSlotClass.DmaStreamClass)
                    || facts.BranchControlCount > machine.GetSlotClassCapacity(IrSlotClass.BranchControl)
                    || facts.SystemSingletonCount > machine.GetSlotClassCapacity(IrSlotClass.SystemSingleton))
                {
                    return AdmissibilityClassification.TypedSlotClassCapacityExceeded;
                }

                if (facts.BranchControlCount > 0 && facts.SystemSingletonCount > 0 && machine.HasAliasedLanes(IrSlotClass.BranchControl))
                {
                    return AdmissibilityClassification.TypedSlotAliasedLaneConflict;
                }

                return AdmissibilityClassification.TypedSlotFactsInvalid;
            }

            // Secondary: safety-mask structural diagnostic.
            if (!safetyDiagnostic.IsCompatible)
            {
                return AdmissibilityClassification.SafetyMaskConflict;
            }

            // Advisory: steal-mismatch no longer blocks admissibility classification.
            // Mismatch information remains available via StealVerdicts / HasStealMismatch.

            return AdmissibilityClassification.StructurallyAdmissible;
        }
    }
}
