// Opt-in -batchmode test, copied with the four authoring helpers into an isolated project.
// No screenshots, playback, scene edits or commands are sent to the user's running Editor.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AvatarCatalog.Remote;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public static class FlareMaNativeCheck
{
    private static Rac2EditorBakeRunner runner;
    private static readonly List<string> report = new List<string>();
    private static int idleUpdates;
    public static void Start()
    {
        EditorApplication.update += StartWhenReady;
    }
    private static void StartWhenReady()
    {
        if (EditorApplication.isCompiling || EditorApplication.isUpdating) { idleUpdates = 0; return; }
        if (++idleUpdates < 3) return;
        EditorApplication.update -= StartWhenReady;
        runner = new Rac2EditorBakeRunner(Run(), error => {
            report.Add(error == null ? "PASS" : "FAIL: " + error);
            File.WriteAllLines("native-check.txt", report);
            EditorApplication.Exit(error == null ? 0 : 1);
        });
    }
    private static void Require(bool ok, string why) { if (!ok) throw new Exception(why); }
    private static string Snapshot(GameObject root)
    {
        var text = new StringBuilder();
        foreach (var t in root.GetComponentsInChildren<Transform>(true))
            foreach (var c in t.GetComponents<Component>())
                if (c) text.AppendLine(EditorJsonUtility.ToJson(c));
        return text.ToString();
    }
    private static IEnumerator Run()
    {
        var plain = new GameObject("No MA source");
        var plainCopy = UnityEngine.Object.Instantiate(plain);
        try
        {
            using (var untouched = Rac2AvatarPreprocessor.Prepare(plainCopy, plain, null))
                Require(!untouched.Clip && plainCopy.GetComponents<Component>().Length == 1,
                    "Plain static objects must not run NDMF");
            bool rejected = false;
            try { Rac2AvatarPreprocessor.Prepare(plain, plain, null); }
            catch (ArgumentException) { rejected = true; }
            Require(rejected, "Source object itself was accepted for mutation");
            report.Add("PASS: no-MA/no-clip path unchanged; source-as-copy rejected.");
        }
        finally { UnityEngine.Object.DestroyImmediate(plainCopy); UnityEngine.Object.DestroyImmediate(plain); }
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Sirius/Prefab_Variant/Sirius_main Variant ver1.1.prefab");
        var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>("Assets/Maria-walking.anim");
        Require(prefab && clip, "Sirius and walking clip must be present");
        var scene = EditorSceneManager.NewPreviewScene();
        var sourceJson = Snapshot(prefab);
        var clipJson = EditorJsonUtility.ToJson(clip);
        var copy = UnityEngine.Object.Instantiate(prefab);
        SceneManager.MoveGameObjectToScene(copy, scene);
        copy.hideFlags = HideFlags.HideAndDontSave;
        Rac2AvatarPreprocessor preparation = null;
        var meshes = new List<Mesh>();
        try
        {
            Require(Rac2AvatarPreprocessor.HasModularAvatar(prefab), "MA components not restored");
            var mt = Rac2AvatarPreprocessor.FindType("nadena.dev.modular_avatar.core.ModularAvatarMergeArmature");
            foreach (var component in copy.GetComponentsInChildren(mt, true))
                report.Add("Merge input " + AnimationUtility.CalculateTransformPath(((Component)component).transform, copy.transform)
                    + " target=" + mt.GetProperty("mergeTargetObject").GetValue(component));
            preparation = Rac2AvatarPreprocessor.Prepare(copy, prefab, clip);
            Require(Snapshot(prefab) == sourceJson, "Source hierarchy/components changed");
            Require(EditorJsonUtility.ToJson(clip) == clipJson, "Source clip changed");
            Require(preparation.Clip, "Processed clip missing");
            foreach (var a in copy.GetComponentsInChildren<Animator>(true)) {
                a.runtimeAnimatorController = null; a.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            }
            var renderers = copy.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                .Where(r => r.enabled && r.gameObject.activeInHierarchy && r.sharedMesh).ToArray();
            Require(renderers.Length == 11, "Mesh count changed: " + renderers.Length);
            var body = Array.Find(renderers, r => r.name == "Body");
            var bodyBones = new HashSet<Transform>(body.bones);
            foreach (var r in renderers)
                if (r.name.StartsWith("cloth_")) {
                    report.Add(r.name + " body bone refs=" + r.bones.Count(b => bodyBones.Contains(b)) + "/" + r.bones.Length);
                    report.Add(string.Join("; ", r.bones.Take(3).Select(b => b ? AnimationUtility.CalculateTransformPath(b, copy.transform) : "NULL")));
                }
            var origin = copy.transform.worldToLocalMatrix;
            var frames = new List<Vector3[][]>();
            var coatLocalFrames = new List<Vector3[]>();
            int coatIndex = Array.FindIndex(renderers, r => r.name == "cloth_overcoat");
            var chest = copy.GetComponent<Animator>().GetBoneTransform(HumanBodyBones.UpperChest);
            Require(chest && coatIndex >= 0, "Sirius chest/coat attachment missing");
            bool constraints = copy.GetComponentsInChildren<Behaviour>(true).Any(c => c is UnityEngine.Animations.IConstraint);
            AnimationMode.StartAnimationMode();
            using (var sampler = new Rac2VatAnimationSampler(copy, preparation.Clip, constraints))
            using (var barrier = constraints ? new Rac2ConstraintBakeBarrier(copy, 32) : null)
            for (int frame = 0; frame < 3; frame++)
            {
                sampler.Sample(frame * .35f);
                if (barrier != null) { barrier.BeginFrame(); while (!barrier.IsReady(frame)) yield return null; }
                var nodes = new Vector3[renderers.Length][];
                for (int i = 0; i < renderers.Length; i++) {
                    var mesh = new Mesh(); meshes.Add(mesh); renderers[i].BakeMesh(mesh);
                    var matrix = origin * renderers[i].transform.localToWorldMatrix;
                    nodes[i] = mesh.vertices.Select(v => matrix.MultiplyPoint3x4(v)).ToArray();
                }
                frames.Add(nodes);
                var chestFromExhibit = chest.worldToLocalMatrix * origin.inverse;
                coatLocalFrames.Add(nodes[coatIndex].Select(v => chestFromExhibit.MultiplyPoint3x4(v)).ToArray());
            }
            AnimationMode.StopAnimationMode();
            // Rigid whole-avatar translation/rotation cannot change pair distances.
            // The broken RAC2 had < 0.0011 m change on these clothes (half-float noise).
            bool deformed = true;
            foreach (var name in new[] { "Body", "cloth_jacket", "cloth_shrit_with_vest", "cloth_dress_shoes" })
            {
                int i = Array.FindIndex(renderers, r => r.name == name);
                Require(i >= 0, "Renderer absent: " + name);
                float max = 0; int count = frames[0][i].Length;
                for (int a = 0; a < 64; a++) {
                    int v1 = a * count / 64, v2 = (v1 + (int)(count * .43)) % count;
                    for (int f = 1; f < frames.Count; f++)
                        max = Mathf.Max(max, Mathf.Abs(Vector3.Distance(frames[f][i][v1], frames[f][i][v2]) - Vector3.Distance(frames[0][i][v1], frames[0][i][v2])));
                }
                report.Add(name + " pair-distance deformation = " + max + " m");
                deformed &= max > .02f;
            }
            Require(deformed, "Clothing still behaves as rigid unbound meshes");
            // Sirius's overcoat uses a ParentConstraint to UpperChest and independent
            // PhysBone chains. Its correct non-physics pose is rigid relative to the chest,
            // unlike the jacket/shoes. Requiring coat deformation would be a false failure.
            float coatDrift = 0, coatTravel = 0;
            for (int f = 1; f < frames.Count; f++)
                for (int v = 0; v < coatLocalFrames[0].Length; v++) {
                    coatDrift = Mathf.Max(coatDrift, Vector3.Distance(coatLocalFrames[f][v], coatLocalFrames[0][v]));
                    coatTravel = Mathf.Max(coatTravel, Vector3.Distance(frames[f][coatIndex][v], frames[0][coatIndex][v]));
                }
            report.Add("Overcoat: chest-relative drift=" + coatDrift + " m; exhibit-space travel=" + coatTravel + " m");
            Require(coatDrift < .001f && coatTravel > .05f, "Coat must track UpperChest without drifting");
            report.Add("11 renderers preserved; processed clip sampled; source prefab unchanged.");
        }
        finally
        {
            if (AnimationMode.InAnimationMode()) AnimationMode.StopAnimationMode();
            foreach (var mesh in meshes) if (mesh) UnityEngine.Object.DestroyImmediate(mesh);
            if (copy) UnityEngine.Object.DestroyImmediate(copy);
            preparation?.Dispose();
            EditorSceneManager.ClosePreviewScene(scene);
        }
        Require(Snapshot(prefab) == sourceJson && EditorJsonUtility.ToJson(clip) == clipJson,
            "Original source changed during sampling/cleanup");
    }
}
