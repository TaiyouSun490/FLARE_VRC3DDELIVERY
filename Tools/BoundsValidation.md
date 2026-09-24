# Static-pose META mismatch (2026-09-24)

The user-supplied Sirius file (19,794,439 bytes, SHA-256
`cd71e651fcf06e9048e7484fdfbcb19023a156292ecfa21a2d757d7e7489226e`)
contains 11 static nodes and **zero VAT nodes**. No PhysBone animation can be
recovered from that static file; the selected clip must be exported again.

The file reproduces a different bounds failure from booth-size overflow:

- META Y range: 0 to 2.160548 m.
- Serialized vertex Y range: 0.179390 to 2.136388 m.
- The lower empty margin is 0.179390 m, beyond the runtime's 0.101 m consistency
  limit. Geometry IS contained; the old error incorrectly said it was not.

The static export path trusted Mesh.bounds after BakeMesh/Instantiate. Those can
retain conservative skinning/culling bounds rather than exact vertex bounds.
Creator now recalculates bounds on its own baked/copied meshes before placement.
The bundle writer independently derives META from serialized vertex bounds (or
the full VAT bounds) and node transforms, instead of trusting caller bounds.
Original source mesh bounds are not changed. Particle-only anchor semantics and
the runtime's containment/consistency/booth tolerances are unchanged.

The runtime now distinguishes geometry outside META from excessive empty margins.
Creator explicitly labels the no-clip path as static and serializes its selected
root, clip and VAT/PhysBone settings across editor assembly reloads.

`node Tools/inspect_rac2_bounds.mjs <file.rac2> --summary --require-consistent`
checks the serialized static/VAT node envelopes and reports VAT node count.
The strict option deliberately fails for particle-only files (no model envelope),
and is not a complete RAC2 material/texture/security validator.

`FlareBoundsNativeCheck.cs` is an opt-in isolated Unity batch check; it writes
actual RAC2 files using the production exporter and compares their META values
against expected bounds for inflated source bounds, transformed/mirrored nodes,
VAT extent and particle-only anchors. It also checks source mesh bounds remain
unchanged. Do not run its exit-on-completion entry point in the active user editor.

Result: all four native cases PASS on Unity 2022.3.22f1. Independent byte-level
inspection of the three geometry fixtures also passes the unchanged runtime
bounds policy, while the original Sirius failure is reproduced. Runtime and
Editor C# compilation pass with existing warnings; Udon VM/GUI execution has
not been rerun for this change.

No corrected Sirius RAC2 was published in this change. The user must choose the
clip and re-export for the intended animated PhysBone test. GUI/VRChat validation
remains user-owned.
