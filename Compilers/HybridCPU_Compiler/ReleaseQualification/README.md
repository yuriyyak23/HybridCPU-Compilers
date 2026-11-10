# ReleaseQualification ownership

`write-refplan7-final --commit C --evidence-index E --source-digest S --toolchain-digest T --options-digest O --image-a A --image-b B --out M`

The caller owns source/toolchain/options identity and the two clean-build declarations. This legacy command supplies `CleanBuildA/B=true`; it does not execute either build. Only use it for inputs whose build provenance has been separately established. Equal image hashes establish byte equality, not clean-build execution.

`HybridCpuRefPlan7ImageInputsV1` reads A then B and binds exact lowercase SHA256 values to the caller's scalar identities and declarations. `HybridCpuRefPlan7FinalReleaseV1` owns structural image admission: both declarations, 64-hex identities, and equal A/B hashes. This check is independent of producer execution; manifest validation also requires current phase evidence, contracts, matrix and digest checks. Successful validation alone does not grant publication authority.

Before promoting a clean-build claim, the producer must retain the commands, input identities, isolated build roots, native exit codes and hashes for two independently clean builds using identical source/toolchain/options/contracts. Preserve the compiler host, publish SDK and upstream ILCompiler build identities separately. Frozen images, a copied second image, matching hashes or component smoke results do not establish this claim. No current execution-receipt parser is introduced here; a future producer integration requires a concrete consumer and bounded compatibility validation.

Malformed evidence-index JSON and duplicate members fail before image reads. Output is created only after final validation. The CLI's declarations and manifest schema remain compatible; existing fail-closed admission and diagnostic precedence are retained.

Qualification: no.
