using System;
using System.Collections.Generic;

namespace HybridCPU.Compiler.Core.Threading
{
    public enum HybridCpuThreadCompilerFacadeBoundaryKind
    {
        TypedCompilerBoundary = 0,
        ObsoleteCompatibilityFacade,
        CompilerArtifactObservation,
        CompilerMetadataBoundary,
        CompilerStateMutation
    }

    public sealed record HybridCpuThreadCompilerFacadeAuditRow(
        string MemberKey,
        string MemberName,
        HybridCpuThreadCompilerFacadeBoundaryKind BoundaryKind,
        string AuthoritySemantics);

    public static class HybridCpuThreadCompilerFacadeAudit
    {
        private static readonly HybridCpuThreadCompilerFacadeAuditRow[] Rows =
        [
            Typed("CompileAcceleratorSubmit/3", "CompileAcceleratorSubmit", "L7-SDC typed lowering decision; descriptor, execution, publication, commit and retire remain runtime-owned."),
            Typed("CompileAcceleratorSubmit/5", "CompileAcceleratorSubmit", "L7-SDC typed lowering decision; descriptor, execution, publication, commit and retire remain runtime-owned."),
            Typed("CompileMtileLoadWithDecision/4", "CompileMtileLoadWithDecision", "MatrixTile helper carrier plus CompilerLoweringDecision; plan is an artifact, not authority."),
            Typed("CompileMtileStoreWithDecision/4", "CompileMtileStoreWithDecision", "MatrixTile helper carrier plus CompilerLoweringDecision; plan is an artifact, not authority."),
            Typed("CompileMtileMaccWithDecision/6", "CompileMtileMaccWithDecision", "MatrixTile helper carrier plus CompilerLoweringDecision; plan is an artifact, not authority."),
            Typed("CompileMtransposeWithDecision/5", "CompileMtransposeWithDecision", "MatrixTile helper carrier plus CompilerLoweringDecision; plan is an artifact, not authority."),
            Typed("CompileVloadWithDecision/4", "CompileVloadWithDecision", "VectorTransfer helper carrier plus CompilerLoweringDecision; plan is an artifact, not authority."),
            Typed("CompileVstoreWithDecision/4", "CompileVstoreWithDecision", "VectorTransfer helper carrier plus CompilerLoweringDecision; plan is an artifact, not authority."),

            Compatibility("CompileInstruction/9", "CompileInstruction", "Raw VLIW carrier compatibility facade; carrier emission is not execution, publication, commit or retire."),
            Compatibility("InsertInstruction/10", "InsertInstruction", "Raw VLIW carrier mutation compatibility facade; carrier insertion is not execution, publication, commit or retire."),
            Compatibility("CompileDmaStreamComputeDescriptor/5", "CompileDmaStreamComputeDescriptor", "DSC descriptor parse/admission compatibility facade; descriptor acceptance is not runtime legality or publication."),
            Compatibility("CompileDmaStreamCompute/3", "CompileDmaStreamCompute", "DSC lane6 carrier compatibility facade; guard and descriptor observations do not grant runtime authority."),
            Compatibility("CompileMtileLoad/4", "CompileMtileLoad", "Raw MatrixTile plan compatibility shim over CompileMtileLoadWithDecision."),
            Compatibility("CompileMtileStore/4", "CompileMtileStore", "Raw MatrixTile plan compatibility shim over CompileMtileStoreWithDecision."),
            Compatibility("CompileMtileMacc/6", "CompileMtileMacc", "Raw MatrixTile plan compatibility shim over CompileMtileMaccWithDecision."),
            Compatibility("CompileMtranspose/5", "CompileMtranspose", "Raw MatrixTile plan compatibility shim over CompileMtransposeWithDecision."),
            Compatibility("CompileVload/4", "CompileVload", "Raw VectorTransfer plan compatibility shim over CompileVloadWithDecision."),
            Compatibility("CompileVstore/4", "CompileVstore", "Raw VectorTransfer plan compatibility shim over CompileVstoreWithDecision."),

            Artifact("CompileProgram/0", "CompileProgram", "Compiled program aggregate artifact; not execution-ready and not architectural publication."),
            Artifact("CompileProgram/1", "CompileProgram", "Compiled program aggregate with emitted carrier image metadata; not runtime publication, commit or retire."),
            Artifact("BuildIrProgram/0", "BuildIrProgram", "Compiler IR construction artifact; not lowering authority and not runtime legality."),
            Artifact("GetCompiledInstructions/0", "GetCompiledInstructions", "Carrier buffer observation; carriers are not execution, publication, commit or retire."),
            Artifact("GetBundleAnnotations/0", "GetBundleAnnotations", "Sideband observation; sideband is evidence/transport metadata only."),

            Metadata("DeclareLabel/2", "DeclareLabel", "Compiler IR metadata mutation; label declaration is not control-flow execution or publication."),
            Metadata("DeclareEntryPoint/3", "DeclareEntryPoint", "Compiler IR metadata mutation; entry-point declaration is not execution authority."),
            Metadata("DeclareLabelAtCurrentPosition/1", "DeclareLabelAtCurrentPosition", "Compiler IR metadata mutation; label declaration is not control-flow execution or publication."),
            Metadata("DeclareEntryPointAtCurrentPosition/2", "DeclareEntryPointAtCurrentPosition", "Compiler IR metadata mutation; entry-point declaration is not execution authority."),

            State("Reset/0", "Reset", "Compiler context state reset; clears local artifacts and grants no runtime authority.")
        ];

        public static IReadOnlyList<HybridCpuThreadCompilerFacadeAuditRow> PublicFacadeRows => Rows;

        public static bool TryGetRow(
            string memberKey,
            out HybridCpuThreadCompilerFacadeAuditRow row)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(memberKey);
            for (int index = 0; index < Rows.Length; index++)
            {
                if (string.Equals(Rows[index].MemberKey, memberKey, StringComparison.Ordinal))
                {
                    row = Rows[index];
                    return true;
                }
            }

            row = default!;
            return false;
        }

        private static HybridCpuThreadCompilerFacadeAuditRow Typed(
            string memberKey,
            string memberName,
            string authoritySemantics) =>
            new(
                memberKey,
                memberName,
                HybridCpuThreadCompilerFacadeBoundaryKind.TypedCompilerBoundary,
                authoritySemantics);

        private static HybridCpuThreadCompilerFacadeAuditRow Compatibility(
            string memberKey,
            string memberName,
            string authoritySemantics) =>
            new(
                memberKey,
                memberName,
                HybridCpuThreadCompilerFacadeBoundaryKind.ObsoleteCompatibilityFacade,
                authoritySemantics);

        private static HybridCpuThreadCompilerFacadeAuditRow Artifact(
            string memberKey,
            string memberName,
            string authoritySemantics) =>
            new(
                memberKey,
                memberName,
                HybridCpuThreadCompilerFacadeBoundaryKind.CompilerArtifactObservation,
                authoritySemantics);

        private static HybridCpuThreadCompilerFacadeAuditRow Metadata(
            string memberKey,
            string memberName,
            string authoritySemantics) =>
            new(
                memberKey,
                memberName,
                HybridCpuThreadCompilerFacadeBoundaryKind.CompilerMetadataBoundary,
                authoritySemantics);

        private static HybridCpuThreadCompilerFacadeAuditRow State(
            string memberKey,
            string memberName,
            string authoritySemantics) =>
            new(
                memberKey,
                memberName,
                HybridCpuThreadCompilerFacadeBoundaryKind.CompilerStateMutation,
                authoritySemantics);
    }
}
