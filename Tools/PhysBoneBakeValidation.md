# PhysBone VAT authoring validation (2026-09-24)

## Implementation boundary

Creator's opt-in `PhysBone を含める` processes MA on a preview-scene copy,
then samples the selected clip and native Unity constraints before advancing
the actual SDK PhysBone solver. No user Play Mode switch, global Unity clock
change, runtime PhysBone reconstruction, source scene save or upload is performed.

`Rac2PhysBoneBakeSession` owns its manager and native buffers. SDK singleton and
debug timing fields are set only for synchronous operations, and restored before
any editor yield. An existing manager or another FLARE export blocks this mode.
Jobs complete before reading meshes. The preview manager's native cleanup is
explicitly invoked if Unity did not invoke OnDestroy in Edit Mode (buffer sentinel
prevents double disposal). Source-external bone/collider references are rejected.

There is no public SDK offline step API. The reflection bridge is intentionally
limited to Base SDK **3.10.4**; unknown versions stop with an actionable error.
The actual SDK settings, chain construction, collisions and integration are used;
this is not a home-made spring approximation. Default path with the checkbox OFF
does not instantiate the solver.

Simulation steps are at most 1/60 second. Holding clip time zero for configurable
warmup (default one second) is not added to the exported clip. Loop-tail correction
blends only physics position/rotation offsets to those of frame zero, relative to
the current sampled animation pose. It is restored after mesh capture and does not
feed back into simulation. It cannot repair a non-looping clip's root-motion seam.

## Reproduction

The opt-in `FlarePhysBoneNativeCheck.cs` runs with `-batchmode -nographics
-executeMethod FlarePhysBoneNativeCheck.Start` in an **isolated** Unity project.
Copy it and the six authoring helpers (preprocessor, animation sampler, constraint
barrier, editor runner, PhysBone session, frame baker) into that project's
Assets/Editor. Install Base SDK 3.10.4, MA 1.18.7, NDMF 1.14.8 and their dependencies.
The Sirius fixture uses `Assets/Sirius/Prefab_Variant/Sirius_main Variant ver1.1.prefab`
and walking clip `Assets/Maria-walking.anim`. These private test assets are not
dependencies of the distributable feature. Read `physbone-check.txt` on completion.
Do not run this exit-on-completion test in the user's active editor.

## Native checks

Unity 2022.3.22f1 / Base SDK 3.10.4 / MA 1.18.7 / NDMF 1.14.8:

- Synthetic moving-root chain: maximum actual bone swing **10.90611 degrees**.
- Sphere PhysBone Collider on/off tip difference: **0.3129376 m**.
- Repeating the same collision fixture: **0 m** difference.
- Loop-offset blend returns to the first offset; restoring output recovers the
  original simulated pose instead of feeding the blend into physics.
- Negative delta rejected; overlapping solver rejected.
- Cancelling at a warmup yield releases the manager; immediate retry succeeds.
- Zero warmup succeeds; non-loop playback samples the exact clip endpoint.
- Sirius **21 active chains**, **42 VAT poses**: enabled vs disabled coat vertices
  differ by up to **0.6235732 m**. All **11 active skinned renderers** retained.
- Source prefab component snapshots and original clip serialization unchanged.
- SDK global singleton/timing fields restored between steps and after cleanup.
- C# Editor assembly compilation succeeds (two pre-existing SphereShell warnings).
- Existing constraint player-loop preservation checks and public-menu checks pass.

The native log still contains the same two Persistent and two TransformAccessArray
shutdown allocations as the pre-existing MA-only isolated test. The new manager's
initial 297 Persistent/5 TransformAccessArray leak was identified and corrected;
the full repeated/cancel/fixture run returns to that pre-existing baseline. This
does not claim that the SDK/MA test environment itself is leak-free.

## Limits / not validated

- User-facing GUI and VRChat appearance are assigned to the user; no GUI QA ran.
- Mesh poses/BakeMesh were checked; a new public Sirius RAC2 was not exported or
  uploaded during this change, and no unitypackage release was rebuilt.
- Selected clip only, not an Animator Controller/FX/contact interaction simulation.
- No hands, grabbing/posing, external-world collisions or downloaded live physics.
- Animated PhysBone/Collider configuration and GameObject activation curves stop
  with a message rather than being silently ignored.
- Arbitrary third-party rigs and other SDK versions are not validated.
