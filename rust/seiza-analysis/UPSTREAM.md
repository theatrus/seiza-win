# Local analysis observer patch

Source: https://github.com/theatrus/seiza/tree/7343ada6ee2bfb39a12090fff15618e864b664a2/seiza-analysis
License: Apache-2.0 (included).

This copy adds `analyze_with_progress`, phase/frame counters and cooperative
cancellation checkpoints to the original calculation. `analyze` retains its
original signature and behavior through a no-op observer. No scientific formulas,
sampling, ordering, admission criteria or report schema are changed. The original
source files and integration tests are retained. The standalone Cargo manifest
uses this workspace's pinned dependencies.

Cancellation is checked before discovery/each frame, after reading, after
measurement, after registration/sampling and at each depth-replay frame. A
currently executing decoder/detector/registrar call finishes before stopping.
Temporary sample files are released by Rust ownership on every cancellation.

The Windows adapter provides the host callback ABI in
`rust/seiza-platform/src/analysis.rs`. Remove this local override when upstream
offers an equivalent progress/cancellation API. Packaging includes this notice;
the upstream revision alone is not a claim that this crate is unmodified.
