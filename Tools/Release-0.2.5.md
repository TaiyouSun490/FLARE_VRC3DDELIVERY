# FLARE Core 0.2.5 packaging notes

Date: 2026-09-24. Target: Unity 2022.3.22f1, Worlds SDK 3.10.4, lilToon 2.3.4, PC / Built-in.

## Scope

- Core importer distribution, not the experimental arbitrary-gimmick branch or mall backend.
- Integrated Creator with JP/EN resources and two user guides, VAT/PhysBone baking, particles, reconstruction materials and three distribution prefabs.
- Preserve existing asset GUIDs, including the six generated Udon programs, for existing references.
- Include only `Assets/com.avatarcatalog.remote`, `Assets/RemoteAvatarCatalogDistribution`, required `Assets/NightSlotMall` resources, and six actual `Assets/SerializedUdonPrograms` dependencies.
- No avatars, clothing, authoring/world scenes, RAC2 test data, third-party SDKs or source-project caches in the archive. No operator RAC2 URL or enabled unconfigured sample button in the prefabs.

## Evidence

- `node Tools/check-flare-localization.mjs`: PASS, 209 bilingual resources and call-site coverage.
- `node Tools/check-flare-menus.mjs`: PASS, 49 unique actions / 8 validators in the source workspace, including its optional local diagnostics.
- `FlarePackageVerification.Pack` in an isolated project: PASS, C# and Udon compilation, three prefab reference checks, dependency coverage. The final build log contains no `ArgumentNullException` or Udon compile-error entry.
- `python Tools/inspect_unitypackage.py <package>`: PASS, 155 asset paths, three prefabs, six owned generated programs, valid metadata, correct version and required guides.
- Final archive: 756,973 bytes; SHA-256 `da493cd20f19c049dea0011e07f39302a7d7864f7c5ad3d7afe29fccc8bb1c79`.
- Final fresh-project import verification: PASS, `FlarePackageVerification.ImportPackage` imported the final archive into an empty SDK / lilToon project; explicit C# / Udon compilation and all three prefab reference checks passed. No avatar or source-world fixtures were installed.

The first isolated build attempt reached Udon compilation before the SDK-generated utility scripts had been C#-compiled. It was rejected, not distributed. Finish SDK initialization before invoking `Pack`. The harness compiles Udon before normalizing prefabs and avoids saving already-portable prefabs unnecessarily.

During fresh-project automatic import, the SDK also logged a transient `GetAllRegisteredPackages can only be called from the main thread` error from `UdonSharp.Compiler.Udon.CompilerUdonInterface.AssemblyCacheInit`. The subsequent explicit compile and validation passed. This is not an error-free startup-log claim; the SDK log remains a limitation of the initial import check.

## Reproduce

1. Prepare a separate Worlds SDK / lilToon project and finish its first import/compilation. Do not use an open working project for batch packaging.
2. Copy the tracked `Assets` with their `.meta` files, including the six tracked serialized programs. Do not copy Library, local avatar fixtures, scenes or Packages from the development source into the distribution.
3. Copy `Tools/FlarePackageVerification.cs` into a local `Assets/PackagingVerification/Editor` folder. This harness is intentionally not part of the shipped unitypackage.
4. Run Unity with `-batchmode -nographics -projectPath <isolated-project> -executeMethod FlarePackageVerification.Pack -logFile <build-log>`.
5. Audit the resulting archive with `Tools/inspect_unitypackage.py`.
6. In another empty SDK / lilToon project with only the harness, run `-executeMethod FlarePackageVerification.ImportPackage -flarePackage <absolute-package-path>`. The harness waits for compilation and checks all three prefabs; its `import-validation.txt` records success.
7. Build the BOOTH ZIP from the unitypackage, `BOOTH-START-HERE.md` (as README-FIRST.md), both user guides, dependencies and known issues. Keep seller notes and development logs outside the customer ZIP.

## Not verified / release limitations

This is structural and compiler validation, not VRChat VM playback, world upload or visual QA. The Sirius iris/pupil issue remains unresolved. JP/EN font glyphs, layout and runtime interaction still require user-visible checks. SDK API-updater/headless shader warnings can appear in isolated startup logs; they do not establish visual shader correctness.

See `Assets/RemoteAvatarCatalogDistribution/KNOWN-ISSUES.md` and `Tools/BOOTH-LISTING-DRAFT.md`. The seller must provide license terms, pricing and support information and disclose known issues before publication. This task prepares files; it does not publish a BOOTH listing.
