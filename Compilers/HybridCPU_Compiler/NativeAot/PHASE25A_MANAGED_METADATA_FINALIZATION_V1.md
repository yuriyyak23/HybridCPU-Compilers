# Phase 25A final-allocation managed metadata

`hybridcpu.managed-metadata-finalization/v1` is the first independent Phase 25
workstream. It finalizes object-reference GC maps and code-manager method records only
from a successful Phase 20 qualification allocation and its exact final W=8 placement.

The allocation witness uses the selected pre-lowering schedule coordinate system for
value intervals. The metadata finalizer therefore checks reference liveness against
that coordinate system, while deriving encoded code offsets from the post-lowering
bundle placement. It independently re-hashes the input schedule, final schedule,
placement and allocation witness before accepting either coordinate system. Any
schedule, frame, placement, witness or options drift fails closed.

Only values explicitly typed as `Pointer` with the `managed-object-reference` contour
can be roots. A requested primitive must not be promoted to a movable reference.
Managed byrefs and interior references remain unsupported. Advanced region, loop,
virtual-thread, FSP and prefetch plans are excluded: the v1 finalizer accepts only the
exact `BasicBlockOnly` selected-plan digest. Spilled roots are accepted only while
resident in a stable final frame slot; a root participating in a reload/store at the
safepoint remains unknown.

Production options are disabled. Qualification emits descriptive HCMG/HCMM metadata;
it does not collect, suspend threads, walk stacks, move objects, execute, publish,
commit or retire. Runtime consumption and moving-GC differential evidence are a
separate Phase 25 runtime gate, and no umbrella managed-runtime support bit is enabled.
