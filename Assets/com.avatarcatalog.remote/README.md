# Remote Avatar Catalog (RAC1 MVP)

Japanese guide for publishing RAC1 files and loading visitor-provided URLs:
[README-JA.md](README-JA.md)

This package exports static catalogue displays to RAC1 and reconstructs them in
a VRChat world through `RemoteAvatarCatalogLoader`.

## Booth Authoring MVP

Create a booth with:

`GameObject > Avatar Catalog > Create RAC1 Booth Authoring Root`

The generated `Rac1BoothAuthoring` component uses the fixed
`Standard-3x3-v1` profile:

- 3.0 m wide, 3.0 m deep and 2.7 m high
- the BoothRoot origin is the floor centre
- `+Y` is up and `+Z` faces the customer
- all geometry must remain inside the profile with a 0.005 m validation tolerance

The menu-created BoothRoot is tagged `EditorOnly`, preventing the source avatar
and shop decoration from being included in the VRChat world build.

A translucent 3 x 3 metre floor guide is shown at the BoothRoot floor plane.
It includes a 0.5 metre grid and highlights the customer-facing `+Z` edge.
Use **Show Floor Guide** in the inspector to hide it. The generated guide mesh,
material and object are editor-only helpers and are never saved or exported.

Assign the posed avatar hierarchy to **Avatar Root** and visual-only shop
decoration to **Shop Visual Root**. Then use **Validate**, **Capture / Refresh**
and **Capture + Export RAC1** in that order. Capture does not modify the source
hierarchy. It uses each `SkinnedMeshRenderer`'s currently evaluated bones and
blendshape weights, then discards bones, animation and behaviours.

The MVP flattens all enabled `MeshRenderer`/`MeshFilter` and
`SkinnedMeshRenderer` content into BoothRoot-local static triangles. Source
`_BaseMap` or `_MainTex` plus `_BaseColor` or `_Color` are baked into one 1024
RGBA32 atlas. AlphaClip input is converted to binary atlas alpha. Transparent
blend materials are rejected.

Current upload limits are 40,000 flattened vertices, 120,000 indices, one 1024
atlas and 10 MB per RAC1 file. Textured sub-mesh UV0 should normally stay
inside 0..1 and the selected base texture must use tiling `(1,1)` and offset
`(0,0)`. When a finite out-of-range UV domain uses `Repeat`, validation shows a
**Fix** button next to **Select**. Fix opts that Booth Authoring component into
temporary Repeat-domain baking, then immediately revalidates. It never edits
the source Mesh, FBX, material or texture. Repeat spans above four tiles per
axis, non-finite UVs, and non-Repeat wrap modes remain hard errors.
Textureless solid-color materials ignore source UV0 and sample the centre of
their generated atlas tile, so the source Mesh/FBX is not modified.

Animator, animation clips, PhysBone, contacts, constraints, particles, Cloth
simulation, scripts, colliders, rigidbodies, lights, cameras and audio are not
serialized. The mall world remains responsible for the booth shell, collision,
lighting, Udon interactions and purchase links.

## Runtime MaterialTemplate

Create a Material with shader:

`Avatar Catalog/RAC1 Opaque Cutout`

Assign it to `RemoteAvatarCatalogLoader.MaterialTemplate`. Keep the loader
properties at:

- `BaseColorProperty`: `_Color`
- `MainTextureProperty`: `_MainTex`

Set `_Cutoff` to `0.5`. The same template renders opaque atlas pixels and clips
alpha-cutout pixels; alpha blending is intentionally unsupported. The shader is
a PC/Built-in Render Pipeline catalogue shader and is included in the world at
build time. Shaders are never downloaded in RAC1.

For the standard booth, enable the loader's **Enforce Standard Booth Profile**
and keep width `3`, depth `3`, height `2.7`, and boundary tolerance `0.005`.

## Editor self-test

Run:

`Tools > FLARE > Developer > Tests > RAC1 > Run Booth Authoring Self-Test`

The test verifies the floor guide dimensions, visibility toggle and authoring-only
export exclusion. It also creates one static shop mesh and one skinned avatar mesh,
captures both, verifies the 1024 atlas and tight RAC1 header bounds, exports through
the existing `Rac1BinaryExporter`, and confirms that out-of-booth geometry is rejected.

For a dedicated batch Unity process:

```text
Unity.exe -batchmode -quit -projectPath <project> \
  -executeMethod AvatarCatalog.Remote.Rac1BoothAuthoringSelfTest.RunFromCommandLine \
  -logFile <log-path>
```

The older single-renderer `Tools > FLARE > Developer > Legacy Exporters > RAC1 Exporter` remains
available and its API/format are unchanged.
