# RAC2 0.2.4: decoding and compatibility contract

The top-level scene format remains RAC2 v3. Runtime 0.2.4 accepts existing v2/v3 files.
New optimized v3 files require runtime 0.2.4. Creator's **Share identical textures** option can be disabled for older runtimes; this disables both new texture references and the standard-checksum container flag.

## Texture payloads inside NODE materials

- Payload version 1 remains unchanged: uint32 version, width, height, byte length, then RGBA32 pixels.
- Payload version 2 is exactly 8 bytes: uint32 version=2, uint32 prior resource index.
- Resource indices count only full version-1 NODE textures, traversing nodes, then material slots, albedo before normal.
- A reference does not consume a resource index. Missing textures do not consume one.
- Only backward references are valid. A reference must match the original resource's linear/sRGB semantic.
- The writer hashes the complete texture payload, including dimensions, and includes albedo/normal semantics in the key.
- Particle textures are not members of this shared NODE table.
- The runtime owns each full texture once. Material entries borrow references and must not destroy them.
- Clear, parse failure, and replacement loads release the unique texture table and all staged mesh/material state.

## Container flags and checksums

- flags=0: existing uncompressed container, 16-byte table entries.
- flags=1: existing compressed container, 24-byte table entries, legacy signed-overflow checksum.
- flags=3: compressed container, 24-byte table entries, standard Adler-32.
- No other flags are accepted.
- The standard implementation reduces in 2776-byte blocks to keep signed 32-bit accumulators in range.
- Legacy readers preserve the historical 5552-byte block arithmetic; do not silently substitute standard Adler.
- The incremental decoder preserves the selected block boundary across frame yields.
- Test vector: 5552 bytes of FF -> standard F18F9B8C, legacy F0BD9B8C.

## Frame scheduling

LZ4 length extensions and copies resume within a token. The copy budget measures bytes rather than tokens.
Overlapping matches preserve the source phase when a yield splits a repeated period.
Mesh attributes and indices resume within a node. Native Mesh uploads, VAT uploads and material setup have separate stages.
GPU texture upload and individual Mesh API calls remain atomic native operations.
All VAT materials receive one common start time immediately before displaying the completed scene.

## Verification entry point

`AvatarCatalog.Remote.Rac2AlgorithmRegression.RunBatch` runs native Unity regression checks and then the existing composite Udon VM Play Mode harness.
Native results are written to Library/Rac2AlgorithmRegression.result.
Udon VM results are written to Library/Rac2CompositeUdonPlayModeTest.result.
These tests are not network-download benchmarks or headset visual inspections.
