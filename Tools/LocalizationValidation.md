# FLARE JP/EN localization — 2026-09-24

## Implemented scope

- Creator: persistent JP/EN selector, default from OS language; authoring controls, booth guide, performance explanations, progress and completion messages.
- Clothing/MA/NDMF and PhysBone helper messages use the same Editor language. External/unmapped diagnostics remain accessible through the explicit technical-details dialog and Console.
- ImagePad: per-instance UseJapanese, translated controls, progress and user-facing errors; optional SetJapanese/SetEnglish events for custom world buttons. Known built-in title/help captions only; user metadata and URLs are never translated.
- Product pedestal: its own UseJapanese setting controls product/trial labels and standalone loader feedback. For the integrated prefab set both components consistently.
- Reconstruction material Inspector: follows the surrounding lilToon Inspector's language, without replacing lilToon's inherited property drawers.
- Separate USER-GUIDE-JA.md and USER-GUIDE-EN.md, in-package help entry, BOOTH listing draft and seller checklist.
- Explicit release-builder inputs include the localization helper, both guides and performance helpers. The portable packaging harness already includes the relevant directories.

Menus, public Udon event names, serialized fields/GUIDs, binary layout, shader behavior, geometry, booth tolerances and networking semantics are unchanged. Developer/legacy tools, raw Console diagnostics and third-party UI are not translated by this change. Official rank names remain stable English identifiers. No external publication or main-repository push was performed.

## Checks

- `node Tools/check-flare-localization.mjs`: PASS, 209 unique nonempty JP/EN resources, known call-site coverage, no localized constants or EditorPrefs access from the summary field initializer, help/package inclusions and documented limits.
- `node Tools/check-flare-menus.mjs`: PASS, 49 unique FLARE actions, 8 matching validators, normal/developer entry separation.
- Main Runtime and Editor C# compilation: PASS. Five pre-existing NaN self-comparison warnings in runtime; two pre-existing SphereShell deprecation warnings in Editor.
- Reconstruction Editor compiled separately with its own Unity response file: PASS. An attempted combined compile was invalid because separate assembly namespaces collide; it was replaced by the actual separate-assembly compile.
- Isolated native check: `.codex-work/FLARE-core-import-check-20260920/localization-check.txt`, `localization-unity.log`. All 209 entries tested in both languages, unknown-key fallback, JP -> EN -> JP button labels, null optional references, and no-history retry feedback in both languages passed. C# proxy checks are not Udon VM playback tests.
- Isolated Udon compilation: PASS. No active user Unity project was controlled, and no GUI QA or scene playback was performed.
- After that native run, one Japanese compression label was shortened and Creator label width was scoped/restored to improve readability. Static checks and main Editor compilation passed again. The material Inspector language labels were separately compiled.
- Scoped `git diff --check` with CRLF allowance: PASS.

To rerun the native test, copy current RuntimeRacImagePadControllerV2.cs, Rac2ProductController.cs and Rac2ProductLoader.cs into a disposable copy of the packaged project's Runtime directory. Copy FlareLocalization.cs and Tools/FlareLocalizationNativeCheck.cs into its remote Editor assembly, then run Unity batchmode/nographics with `-executeMethod AvatarCatalog.Remote.FlareLocalizationNativeCheck.Run`. It exits the test Editor. Never invoke it in the user's active Editor. Existing standalone MA/PhysBone test harnesses now also need FlareLocalization.cs alongside the authoring helpers.

## Outstanding before distribution

- Rebuild the unitypackage; the previously built package does not include these edits automatically.
- Fresh-install check of that exact rebuilt package, including guide links and prefab/program references.
- User-visible JP/EN layout, Japanese glyphs, desktop/VR controls and loading in VRChat. No visual-fit claim is made from compilation.
- The previously reported Sirius iris/pupil rendering defect remains unresolved. Localization does not fix it; disclose or fix it before sale.
- Pricing, license terms, support contact and the final seller-approved listing remain the seller's decisions.
