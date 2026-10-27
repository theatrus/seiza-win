# Per-light calibration foundations

Development work after Windows 0.8.8, following
[Mac #92](https://github.com/theatrus/seiza-mac/pull/92) at
`d574764e6713f7ab3c0826452991504d6c6f6ca3` and
[Seiza #385](https://github.com/theatrus/seiza/pull/385) at
`41caa22ae49b3a512f65c1947a17d0258231da29`.
Both upstream PRs are still evolving. The shipped Windows workflow continues
to prepare **one compatible calibration set per output**. These foundations
add no new setup controls and do not enable per-light automatic calibration.
The published core/C ABI 0.44.2 and stacking 0.51.2 pins are unchanged.

## Intended workflow

One output should be able to combine lights needing different bias/dark/flat
recipes. Calibration sessions must follow each light's actual recipe and
effective gates, independently of folders, CSV row order, sky panels or output
groups. Keep original integration order and count the anchor once.

The Mac [setup design](https://github.com/theatrus/seiza-mac/blob/d574764e6713f7ab3c0826452991504d6c6f6ca3/docs/STACKING-CALIBRATION.md)
is the shared product reference. Windows should keep mode/light selection first,
then offer a calibration review with four views: calibration frames and grades,
measured flat groups, per-light matches/explanations, and reusable master recipes.
Library access, discovery, persistence, jobs and WinUI belong in this host.
Peer grouping, quality eligibility, matching, flat families and recipe policy
belong in the shared core. Missing volumes and inconclusive evidence must not
be reported as empty libraries, OK grades or proven matches.

## Implemented boundary

| Capability | Windows foundation | Limit |
| --- | --- | --- |
| Bias/dark measurement | Typed `CalibrationQualityService` calls the published measurement export | Keeps all fields, algorithm version, 64-bit hot count, source sample units and unknown evidence fields. No host normalization or copied default threshold. |
| Bias/dark/flat screening | Captured PSF Guard peers reproduce native grades/finding codes | Fixtures partition peers explicitly; this is not library-wide peer selection or persistent quality overrides. |
| Measured flat policy | `FlatGroupPolicyService` forwards immutable C JSON on a worker thread | Export-probed and unavailable in the current pin. No policy, schema translation or fallback matcher is implemented in C#. Cancellation is observed around the synchronous call. |
| Master replacement | Existing path-based setter plus native boundary tests | Geometry/layout is validated before replacement. Each subsequent light must match the active masters' acquisition/optical metadata. |
| Rotation/matching gate | Native tolerance and recorded/measured rotation interop | Tolerance reset restores recorded rotation; apply the rotation basis afterward. A measured angle does not bypass the gain/sensor gate. |
| Fixed/expanded replay | A→B→A with different gains, biases and flat angles, checkpoint identity, historical masters and per-pixel replay | Expanded references are planned without calibration in this test. It proves integration/replay only. |
| Prepared-input mode | Boundary test confirms setters are refused | Prepared-only arrays cannot replace a session-aware raw-file expanded planner. |

The [unchanged reference fixtures](../tests/Seiza.App.Tests/Fixtures/CalibrationParity/README.md)
include grades, selections, overrides, master counts/gates, flat families/events
and uncertain placement. Only the exercised parts are claimed here. Synthetic
session tests use the CPU; real-observation and session-aware GPU validation
remain later acceptance checks.

Master/gate setters are separate native transactions and require exclusive
handle access. A future executor must validate the full session, hold the
Windows session gate across applying settings **and pushing its light**, and
abort the pending push on any failed setter. Never silently continue using a
previous calibration session. Gate changes cannot repair references already
planned under the wrong calibration.

## What remains upstream

The current #385 stage supplies measured flat families/groups, changes, angle
epochs, light evidence and personal-choice resolution. Its JSON boundary stays
opaque here while that record schema is changing. Families supplied for a light
must have passed hard sensor/optics gates; personal choices do not disable them.
Automatic results, choices and uncertainty must remain distinct.

Full setup still needs the shared library-wide peer grading/eligibility and
temporal/cohort selector, including ungraded/poor/override behavior, complete-set
preferences, dark age/reach and deterministic master dependency recipes.
The existing metadata matcher does not implement those PSF Guard decisions.

Expanded raw-file planning must calibrate each planning input with its assigned
masters and effective gate **before** star detection and footprint planning.
The current expanded open accepts one calibration set for all planning inputs.
Switching masters only while integrating does not close this gap. The new core
contract must retain an empty file-input stack, original identities/order,
per-frame replay recipes, source protection, cancellation, canvas ceilings and
memory/scratch accounting. It must not count calibrated scratch references as
exposures or silently fall back to prepared-only input mode.

## Next Windows work

1. Consume the published, versioned core contract after #385 and its follow-ups
   are ready. Keep C/Python fixture parity; do not switch production Cargo pins
   to a Git branch or add a Windows copy of PSF Guard's policy.
2. Add an incremental local library index and evidence persistence: separate
   file identities/measurements, peer grades, policy inputs/results and personal
   choices. Cache policy output against its algorithm version **and all inputs**.
   Reuse the existing master caches/leases without pruning masters still needed
   by a reviewed plan or replay.
3. Build the shared recipe dependency graph and a serialized per-light executor.
   A failed build or stale source invalidates the reviewed assignment; it must
   not silently become an uncalibrated run. Cover fixed and correctly calibrated
   expanded planning together, both containers/sample domains and CPU/GPU paths.
4. Add cancellable Analyze Calibration, the four native review views and the
   reviewed Keep with warnings / Exclude / Stop coverage policy. Enable the
   workflow only after decisions and output pixels pass cross-host parity.

Live acquisition remains on its existing behavior until the shared planner and
executor are ready. This work changes no processing algorithm/output version,
product version, or release tag.

## Validation

Local Release solution build passed with zero warnings/errors. The focused
foundation suite passed 12 tests with one explicit unreleased-policy skip.
The full managed suite passed 843 tests with that same one skip; Windows
graphics/codec tests passed 96 with four optional-input skips. Fixture Git
blob hashes match the pinned upstream copies. No production UI has changed.

Run the foundation tests against the pinned native DLL:

```powershell
dotnet test tests/Seiza.App.Tests/Seiza.App.Tests.csproj -c Release --no-restore `
  --filter "FullyQualifiedName~CalibrationQualityParityTests|FullyQualifiedName~CalibrationSessionBoundaryTests|FullyQualifiedName~FlatGroupPolicyTests"
```

`SharedSnapshotKeepsCapturedFamiliesEventsAndUncertainty` is explicitly skipped
when `seiza_flat_group_plan_json` is absent. It is not a passing parity claim.
An isolated development DLL can exercise the bridge without changing product
pins, using the existing test-only `SeizaNativeDll` override:

```powershell
dotnet test tests/Seiza.App.Tests/Seiza.App.Tests.csproj -c Release --no-restore `
  -p:SeizaNativeDll=C:/validation/seiza_cabi.dll `
  --filter "FullyQualifiedName~FlatGroupPolicyTests"
```

Rebuild normally afterward to copy the pinned DLL back. Never package or release
that experimental DLL as a substitute for a published dependency.
