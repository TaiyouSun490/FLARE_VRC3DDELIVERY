// Opt-in batch test in an isolated Unity 2022.3 project with the authoring helpers.
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
using VRC.Dynamics;
using VRC.SDK3.Dynamics.PhysBone.Components;

public static class FlarePhysBoneNativeCheck
{
    private static Rac2EditorBakeRunner runner;
    private static readonly List<string> Report = new List<string>();
    private static int idle;
    public static void Start() { EditorApplication.update += Ready; }
    private static void Ready()
    {
        if (EditorApplication.isCompiling || EditorApplication.isUpdating) { idle = 0; return; }
        if (++idle < 3) return;
        EditorApplication.update -= Ready;
        runner = new Rac2EditorBakeRunner(Run(), error => {
            Report.Add(error == null ? "PASS" : "FAIL: " + error);
            File.WriteAllLines("physbone-check.txt", Report);
            EditorApplication.Exit(error == null ? 0 : 1);
        });
    }
    private static void Require(bool ok, string text) { if (!ok) throw new Exception(text); }
    private static IEnumerator Run()
    {
        var scene = EditorSceneManager.NewPreviewScene();
        var root = new GameObject("Fixture");
        SceneManager.MoveGameObjectToScene(root, scene);
        var pivot = new GameObject("Pivot").transform; pivot.SetParent(root.transform, false);
        var bone = new GameObject("Bone").transform; bone.SetParent(pivot, false);
        var tip = new GameObject("Tip").transform; tip.SetParent(bone, false); tip.localPosition = Vector3.down;
        var pb = bone.gameObject.AddComponent<VRCPhysBone>();
        pb.pull = .2f; pb.spring = .5f; pb.stiffness = .1f;
        pb.gravity = .1f; pb.immobile = 0;
        try
        {
            using (var session = new Rac2PhysBoneBakeSession(root))
            {
                for (int i = 0; i < 60; i++) { bone.localRotation = Quaternion.identity; session.Step(1f / 60f); }
                session.PrepareOutput(0);
                Quaternion firstRotation = bone.localRotation;
                session.RestoreOutput();
                float max = 0;
                for (int i = 0; i < 60; i++)
                {
                    pivot.localPosition = Vector3.right * Mathf.Sin(i * .1f) * .5f;
                    bone.localRotation = Quaternion.identity;
                    session.Step(1f / 60f);
                    max = Mathf.Max(max, Quaternion.Angle(bone.localRotation, Quaternion.identity));
                    Require(!PhysBoneManager.Inst && !PhysBoneManager.DisableTiming, "SDK globals leaked between steps");
                    yield return null;
                }
                Report.Add("Synthetic swing max degrees=" + max);
                Require(max > 5f, "SDK physics did not deform the chain");
                var unblended = bone.localRotation;
                session.PrepareOutput(1);
                Require(Quaternion.Angle(firstRotation, bone.localRotation) < .02f, "Loop seam did not return to the initial physics offset");
                session.RestoreOutput();
                Require(Quaternion.Angle(unblended, bone.localRotation) < .02f, "Seam correction fed back into simulation");
                bool rejected = false;
                try { session.Step(-.1f); } catch (ArgumentOutOfRangeException) { rejected = true; }
                Require(rejected, "Negative physics delta accepted");
                rejected = false;
                try { using (var other = new Rac2PhysBoneBakeSession(root)) { } }
                catch (InvalidOperationException) { rejected = true; }
                Require(rejected, "Overlapping solver was allowed");
                Report.Add("Loop-offset blend/restore, negative-delta rejection and overlapping-export guard passed.");
            }
            Require(!PhysBoneManager.Inst, "Manager not cleaned");
        }
        finally { UnityEngine.Object.DestroyImmediate(root); EditorSceneManager.ClosePreviewScene(scene); }
        var a = CollisionFixture(false);
        var b = CollisionFixture(true);
        var c = CollisionFixture(true);
        Require(Vector3.Distance(a, b) > .01f, "PhysBone Collider did not affect the chain");
        Require(Vector3.Distance(b, c) < .0001f, "Repeated fixed-time simulation differs");
        Report.Add("Collider-on/off tip difference=" + Vector3.Distance(a, b) + " m; repeat error=" + Vector3.Distance(b, c));
        var cancellation = CancelCheck();
        try { while (cancellation.MoveNext()) yield return cancellation.Current; }
        finally { (cancellation as IDisposable)?.Dispose(); }
        var sirius = Sirius();
        try { while (sirius.MoveNext()) yield return sirius.Current; }
        finally { (sirius as IDisposable)?.Dispose(); }
    }

    private static Vector3 CollisionFixture(bool collision)
    {
        var scene = EditorSceneManager.NewPreviewScene();
        var root = new GameObject("Collision fixture"); SceneManager.MoveGameObjectToScene(root, scene);
        var bone = new GameObject("Bone").transform; bone.SetParent(root.transform, false);
        var tip = new GameObject("Tip").transform; tip.SetParent(bone, false); tip.localPosition = Vector3.down;
        var pb = bone.gameObject.AddComponent<VRCPhysBone>();
        pb.pull = .1f; pb.spring = .2f; pb.stiffness = 0; pb.gravity = .3f; pb.radius = .02f;
        if (collision)
        {
            var colliderGo = new GameObject("Sphere"); colliderGo.transform.SetParent(root.transform, false);
            colliderGo.transform.localPosition = new Vector3(.15f, -.7f, 0);
            var collider = colliderGo.AddComponent<VRCPhysBoneCollider>();
            collider.radius = .35f; collider.shapeType = VRCPhysBoneColliderBase.ShapeType.Sphere;
            pb.colliders.Add(collider);
        }
        try
        {
            using (var session = new Rac2PhysBoneBakeSession(root))
                for (int i = 0; i < 120; i++) { session.ResetInputPose(); session.Step(1f / 60f); }
            return tip.position;
        }
        finally { UnityEngine.Object.DestroyImmediate(root); EditorSceneManager.ClosePreviewScene(scene); }
    }

    private static IEnumerator CancelCheck()
    {
        var scene = EditorSceneManager.NewPreviewScene();
        var root = new GameObject("Cancellation fixture"); SceneManager.MoveGameObjectToScene(root, scene);
        var bone = new GameObject("Bone").transform; bone.SetParent(root.transform, false);
        var tip = new GameObject("Tip").transform; tip.SetParent(bone, false); tip.localPosition = Vector3.down;
        bone.gameObject.AddComponent<VRCPhysBone>();
        var clip = new AnimationClip();
        clip.SetCurve("", typeof(Transform), "m_LocalPosition.x", AnimationCurve.Linear(0, 0, 1, .5f));
        try
        {
            AnimationMode.StartAnimationMode();
            using (var baker = new Rac2VatFrameBaker(root, clip, false, 32, true))
            {
                var warm = baker.WarmUp(1);
                Require(warm.MoveNext(), "Warmup did not yield");
                yield return warm.Current;
                (warm as IDisposable)?.Dispose(); // Simulate cancelling at a yield boundary.
            }
            Require(!PhysBoneManager.Inst && !PhysBoneManager.DisableTiming, "Cancellation leaked SDK globals");
            // Immediately retry after cancellation, zero warmup and a non-loop endpoint.
            using (var baker = new Rac2VatFrameBaker(root, clip, false, 32, true))
            {
                var warm = baker.WarmUp(0);
                try { while (warm.MoveNext()) yield return warm.Current; }
                finally { (warm as IDisposable)?.Dispose(); }
                for (int f = 0; f < 3; f++)
                {
                    var sample = baker.SampleFrame(f, 3, false, .15f);
                    try { while (sample.MoveNext()) yield return sample.Current; }
                    finally { (sample as IDisposable)?.Dispose(); }
                    baker.RestoreOutput();
                }
                Require(Mathf.Abs(root.transform.localPosition.x - .5f) < .0001f, "Non-loop clip endpoint not sampled");
            }
            Report.Add("Cancel/retry, zero warmup and non-loop endpoint passed.");
        }
        finally
        {
            if (AnimationMode.InAnimationMode()) AnimationMode.StopAnimationMode();
            UnityEngine.Object.DestroyImmediate(clip); UnityEngine.Object.DestroyImmediate(root);
            EditorSceneManager.ClosePreviewScene(scene);
        }
    }

    private static string Snapshot(GameObject root)
    {
        var text = new StringBuilder();
        foreach (var t in root.GetComponentsInChildren<Transform>(true))
            foreach (var c in t.GetComponents<Component>()) if (c) text.AppendLine(EditorJsonUtility.ToJson(c));
        return text.ToString();
    }

    private static IEnumerator Sirius()
    {
        var source = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Sirius/Prefab_Variant/Sirius_main Variant ver1.1.prefab");
        var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>("Assets/Maria-walking.anim");
        Require(source && clip, "Sirius inputs missing");
        string original = Snapshot(source), originalClip = EditorJsonUtility.ToJson(clip);
        var baseline = new List<Vector3[]>();
        for (int pass = 0; pass < 2; pass++)
        {
            var scene = EditorSceneManager.NewPreviewScene();
            var copy = UnityEngine.Object.Instantiate(source);
            SceneManager.MoveGameObjectToScene(copy, scene);
            copy.hideFlags = HideFlags.HideAndDontSave;
            Rac2AvatarPreprocessor preparation = null;
            var mesh = new Mesh();
            try
            {
                preparation = Rac2AvatarPreprocessor.Prepare(copy, source, clip);
                foreach (var a in copy.GetComponentsInChildren<Animator>(true)) {
                    a.runtimeAnimatorController = null; a.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                }
                Require(copy.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                    .Count(r => r.enabled && r.gameObject.activeInHierarchy && r.sharedMesh) == 11, "Active renderer count changed");
                var coat = copy.GetComponentsInChildren<SkinnedMeshRenderer>(true).First(r => r.name == "cloth_overcoat");
                var origin = copy.transform.worldToLocalMatrix;
                int count = Mathf.CeilToInt(preparation.Clip.length * 30);
                Report.Add("Sirius pass=" + pass + " active chains=" + Rac2PhysBoneBakeSession.Count(copy) + " frames=" + count);
                bool constraints = copy.GetComponentsInChildren<Behaviour>(true).Any(c => c is UnityEngine.Animations.IConstraint);
                AnimationMode.StartAnimationMode();
                float maxDifference = 0;
                using (var baker = new Rac2VatFrameBaker(copy, preparation.Clip, constraints, 32, pass == 1))
                {
                    var warm = baker.WarmUp(1);
                    try { while (warm.MoveNext()) yield return warm.Current; }
                    finally { (warm as IDisposable)?.Dispose(); }
                    for (int f = 0; f < count; f++)
                    {
                        var sample = baker.SampleFrame(f, count, true, .15f);
                        try { while (sample.MoveNext()) yield return sample.Current; }
                        finally { (sample as IDisposable)?.Dispose(); }
                        mesh.Clear(); coat.BakeMesh(mesh);
                        var matrix = origin * coat.transform.localToWorldMatrix;
                        var positions = mesh.vertices.Select(v => matrix.MultiplyPoint3x4(v)).ToArray();
                        if (pass == 0) baseline.Add(positions);
                        else for (int v = 0; v < positions.Length; v++)
                            maxDifference = Mathf.Max(maxDifference, Vector3.Distance(positions[v], baseline[f][v]));
                        baker.RestoreOutput();
                    }
                }
                if (pass == 1) {
                    Report.Add("Sirius coat max physics-on/off vertex difference=" + maxDifference + " m");
                    Require(maxDifference > .005f, "Coat has no measurable physics motion");
                }
            }
            finally
            {
                if (AnimationMode.InAnimationMode()) AnimationMode.StopAnimationMode();
                UnityEngine.Object.DestroyImmediate(mesh);
                UnityEngine.Object.DestroyImmediate(copy);
                preparation?.Dispose();
                EditorSceneManager.ClosePreviewScene(scene);
            }
        }
        Require(original == Snapshot(source) && originalClip == EditorJsonUtility.ToJson(clip), "Original input changed");
        Require(!PhysBoneManager.Inst && !PhysBoneManager.DisableTiming, "SDK globals leaked after Sirius");
        Report.Add("Source prefab/clip unchanged; 11 renderers retained; SDK globals restored.");
    }
}
