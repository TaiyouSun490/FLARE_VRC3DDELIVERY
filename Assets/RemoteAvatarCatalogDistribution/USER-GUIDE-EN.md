# FLARE — User Guide (English)

[日本語](USER-GUIDE-JA.md)

FLARE is a PC VRChat world component that downloads RAC2 3D exhibits from public HTTPS URLs. One file can contain models, baked VAT animation, supported particles, interaction settings, and product metadata.

RAC2 is not an avatar upload format or a way to run arbitrary Unity scripts. VAT records vertex motion for playback; it does not recreate a live Animator or PhysBone simulation.

## 1. Requirements

Configuration used for local checks:

- Unity 2022.3.22f1, Built-in Render Pipeline
- VRChat Worlds SDK 3.10.4, including UdonSharp
- lilToon 2.3.4
- Windows / PC worlds

Create a Worlds project with VCC and install the Worlds SDK and lilToon before importing FLARE. Third-party SDKs, shaders, avatars, clothing and hosting subscriptions are not included. Quest/Android and URP/HDRP are outside the verified scope.

Back up an existing project. Importing the unitypackage updates assets with matching GUIDs. This is not a downgrade path from the separate newer declarative-gimmick branch.

## 2. Place an ImagePad and load a file

1. Import the supplied unitypackage through `Assets > Import Package > Custom Package...`.
2. Drag `Assets/RemoteAvatarCatalogDistribution/Prefabs/RAC2-ImagePad.prefab` onto the floor of your scene.
3. Initially keep the prefab references and child objects unchanged.
4. Build the world for PC through the VRChat SDK and check it in VRChat. Follow the SDK's own world-build instructions.
5. Paste a public HTTPS `.rac2` file URL into the panel and press **LOAD RAC2**.
6. After loading, the exhibit appears. **RETRY** reloads the last URL; **CLEAR** removes the current exhibit.

For hosts outside VRChat's trusted list, each player may need to enable `Allow Untrusted URLs` in their VRChat settings. This is not a world setting that enables it for every visitor.

The distribution does not include Maria, Sirius, or permission to use their test URLs. Use content you are authorized to distribute. An empty RAC2 sample URL in the portable prefab is intentional.

### Choose a prefab

| Prefab | Purpose |
| --- | --- |
| RAC2-ImagePad | Models, VAT and particles; recommended starting point |
| RAC2-ImagePad-Pedestal | Model display plus product metadata and avatar trial |
| RAC2-Product-Pedestal | Product metadata and avatar trial only; no model reconstruction |

Trials refer to an avatar already uploaded to VRChat. They do not turn the downloaded RAC2 mesh into a wearable avatar.

## 3. Language

- In Creator, choose `日本語` or `English` in `Language / 言語`. The first default follows the OS language; subsequent choices are saved in Unity Editor preferences.
- Open the selected-language guide using `Tools > FLARE > User Guide...` or **User guide** in Creator.
- For a world ImagePad, set `Use Japanese` on its `RuntimeRacImagePadControllerV2` component: on for Japanese, off for English. Build the world after changing it.
- On a pedestal, use the same setting on `Rac2ProductController`. For an English integrated prefab, turn it off on both components.
- A custom world UI can call ImagePad's public `SetJapanese` and `SetEnglish` events. The selection is local, not network-synchronized.
- The material reveal-effect section follows the surrounding lilToon Inspector language.

Unity menu paths, filenames, API identifiers and official rating names (Excellent through Very Poor) remain stable. Console diagnostics and third-party errors retain their original wording. Verify Japanese font rendering in the built world. Custom scene labels are not automatically translated.

## 4. Create one RAC2 with a model and particles

1. Put the model and particles under a single root GameObject. The model itself can be the root.
2. Select it and open `Tools > FLARE > RAC2 Creator...`, or right-click and choose `FLARE > Create RAC2 from this object...`.
3. Check **Exhibit Root**. Use **Use Selection** to assign the selected object.
4. Review mesh, material and emitter counts and the drawing-cost estimate.
5. Click **Frame exported booth** to inspect placement. Export automatically centers the model and places it on the floor using its geometry. You do not need to move the root pivot to the feet.
6. Leave **Animation Clip** empty for a static exhibit, or configure animation below.
7. Set particles, optional catalog information and pickup behavior.
8. Click **Create RAC2...**, choose a destination and check the completion counts for renderers, VAT nodes and emitters.

Creator writes a local file. It does not automatically upload to a CDN. Normal users do not need the test runners, legacy exporters or installers under `FLARE > Developer`.

### Animation and PhysBones

- Assign the desired **Animation Clip**. Without a clip, the result is static and contains no PhysBone motion.
- Choose **VAT FPS** and **Loop**. Each file uses one clip.
- **Bake VAT Normals** records normals as the mesh deforms, increasing storage.
- Enable **Include PhysBones** to record secondary motion. The current offline solver bridge supports SDK 3.10.4 and stops on incompatible SDK versions.
- **Warm-up** settles the initial pose before recording. **Loop physics blend** brings only the physics offset back toward the start; it does not fix discontinuities in the source clip or root motion.
- PhysBone colliders inside the root are included. External world collisions and live player grabbing/touch are not. Clip-driven PhysBone settings and activation are unsupported.
- MA clothing requires Modular Avatar and NDMF. The checked combination is MA 1.18.7 / NDMF 1.14.8. Clothing is built on an export copy; the original avatar is not edited.
- If the completion dialog says **0 VAT nodes**, no VAT animation was included.

### Booth and particles

The standard booth is 3m wide, 3m deep and 2.7m high. Green marks the booth, blue the floor, and yellow/red the model bounds. Final validation of animated exports uses the full VAT animation envelope.

If too large, inspect the dimensions and use **Select oversized object**, then adjust the model or animation. Particle emitter positions and travel do not affect floor placement, centering or size validation. Particles outside the booth are hidden during playback.

Not every Unity particle module is supported. Supported basics include Point/Sphere/Cone/Box, Billboard/Mesh, Alpha/Additive blending, gravity, color changes, rotation and flipbooks. Noise, Collision, Trails, Sub Emitters and Lights are unsupported. Use Local simulation space and constant lifetime, gravity and emission rate. Creator reports other incompatible settings.

### Catalog information and pickup

Enable **Include Catalog Info** to store a product name, creator and HTTPS product URL. For trials, supply a valid `avtr_...` Blueprint ID, enable **Trial Enabled**, and use a pedestal prefab.

**Portable / VRC Pickup** stores pickup permission and enables a collider. Loading or reloading resets the exhibit to its initial placement.

## 5. Hosting

Upload RAC2 to your own web server or CDN. Use a public HTTPS URL that returns the file itself, without authentication, cookies or an HTML viewing page. Configure it to return `200 OK` directly, without redirects. Recommended Content-Type: `application/octet-stream`.

**Smaller file (slower load)** uses lossless LZ4 compression. It reduces transfer size but adds Udon decompression work. **Share identical textures** requires runtime 0.2.4 or later.

Hosting providers can impose their own size limits; FLARE's 128 MiB safety limit does not mean a host accepts that size. If a replaced file remains cached, test with a new filename and URL.

## 6. Performance, reveal effect and limits

Adaptive loading is on by default. It continuously adjusts work using observed local FPS. Individual Unity mesh operations and GPU uploads cannot be fully split, so hitch-free loading is not guaranteed.

Configure the reveal effect in `Rac2RuntimeLoader > Reconstruction`. Duration is measured in seconds; Block Size controls pattern size; Edge Color controls emission; Inflation expands existing vertices near the edge. Set Inflation to zero to disable expansion. The effect does not add geometry or fill cut surfaces. Bloom depends on world post-processing. It is not applied to the basic GLB preview, and Android is not verified.

| Item | Current limit |
| --- | --- |
| Meshes / material slots | 16 / 64 |
| Particles | 4 emitters, 32 particles each |
| Total vertices / triangles | 250,000 / 500,000 |
| Stored / expanded file | 128 MiB each |
| VAT | 2–240 frames, 1–60 FPS |
| Standalone pedestal input | 64 MiB |

Being within a safety limit does not guarantee acceptable performance. Optimize excessive meshes, materials, vertices and textures before VAT export, using Mesh Baker or similar tools where appropriate. The drawing-cost estimate is not the SDK's complete avatar performance rank.

## 7. Troubleshooting

| Symptom | Check |
| --- | --- |
| No animation or secondary motion | Assign a clip and check the VAT node count; this is not live PhysBone playback |
| Clothing does not follow | MA/NDMF installation, missing scripts, constraint references and the source animation |
| Exhibit exceeds booth | Reduce the model/full-animation envelope; moving the pivot does not fix size |
| META bounds error | Re-export with the updated Creator and ensure the URL serves the new file |
| Missing eyes or changed colors/materials | Full lilToon fidelity is not supported; check main texture, normal map and Opaque/Cutout compatibility |
| Download fails | Direct HTTPS URL, public access, host size limits and Allow Untrusted URLs |
| Loading is heavy | Reduce meshes, textures or VAT frames; keep adaptive loading on and compare uncompressed output |

Use **Show technical details** on an export error or copy the Unity Console entry. Include Unity/SDK/lilToon versions, reproduction steps and the full error when reporting a problem. Do not send avatar data you are not allowed to redistribute.

## 8. Current caveat

A Sirius test exhibit has a reported issue where the iris/pupil is not visible after loading. Its cause and fix are not yet confirmed. Exact appearance for arbitrary models or lilToon settings is not guaranteed. Before sale, import the actual delivery package into a clean project and check JP/EN text, loading, VAT, particles and the reveal effect in VRChat.
