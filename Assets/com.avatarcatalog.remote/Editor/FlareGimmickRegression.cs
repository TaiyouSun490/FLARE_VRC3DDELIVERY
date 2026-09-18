using System;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using UdonSharp;
using UdonSharp.Compiler;
using UdonSharpEditor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDK3.Components;
using VRC.Udon;

namespace AvatarCatalog.Remote
{
    /// <summary>Real Udon VM tests. No reflection-based execution of runtime methods.</summary>
    [InitializeOnLoad]
    public static class FlareGimmickRegression
    {
        private const string Key = "FLARE.GimmickRegression";
        private static int _phase, _frames, _bad, _checks;
        private static double _deadline, _wait;
        private static float _tweenUntil;
        private static float _readyAt;
        private static GameObject _instance;
        private static UdonBehaviour _loader, _interpreter, _downloader;
        private static Transform _display;
        private static bool _waitingDownload;
        private static int _idleTicks;
        private static Material _overrideTemplate;
        private static readonly int[] BadFixtures = { 0, 1, 2, 3, 4, 5, 9, 10, 11 };
        static FlareGimmickRegression() { EditorApplication.update += Poll; }

        public static void RunBatch()
        { Begin(false); }
        public static void RunNetworkBatch()
        { Begin(true); }
        private static void Begin(bool network)
        {
            try
            {
                FlareGimmickBuilder.Build();
                UdonSharpCompilerV1.CompileSync(new UdonSharpCompileOptions { IsEditorBuild = true });
                if (UdonSharpProgramAsset.AnyUdonSharpScriptHasError()) throw new InvalidOperationException("Udon compilation failed.");
                MakeTextureFixture();
                MakeLargeFixture();
                SessionState.SetInt(Key + ".editorChecks", _checks);
                var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
                var descriptor = new GameObject("World").AddComponent<VRCSceneDescriptor>();
                descriptor.spawns = new[] { descriptor.transform };
                GameObject ground = GameObject.CreatePrimitive(PrimitiveType.Cube);
                ground.name = "Floor"; ground.transform.position = new Vector3(0, -.1f, 0); ground.transform.localScale = new Vector3(10, .2f, 10);
                EditorSceneManager.SaveScene(scene, "Assets/FLAREGimmicks/Samples/Runtime-Test.unity");
                SessionState.SetBool(Key, true);
                SessionState.SetBool(Key + ".network", network);
                SessionState.SetFloat(Key + ".deadline", (float)EditorApplication.timeSinceStartup + 240f);
                EditorApplication.isPlaying = true;
            }
            catch (Exception error) { Finish(false, error.ToString()); }
        }

        private static void Poll()
        {
            if (!SessionState.GetBool(Key, false)) return;
            try
            {
                _deadline = SessionState.GetFloat(Key + ".deadline", 0f);
                if (EditorApplication.timeSinceStartup > _deadline) throw new TimeoutException("FLARE VM timeout, phase " + _phase);
                if (!EditorApplication.isPlaying) return;
                if (++_frames < 30) return;
                if (_phase == 0)
                {
                    _checks = SessionState.GetInt(Key + ".editorChecks", 0);
                    _instance = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(FlareGimmickBuilder.PlayerPath));
                    _loader = UdonSharpEditorUtility.GetBackingUdonBehaviour(_instance.GetComponent<FlareGlbSceneLoader>());
                    _interpreter = UdonSharpEditorUtility.GetBackingUdonBehaviour(_instance.GetComponent<FlareGimmickInterpreter>());
                    _downloader = UdonSharpEditorUtility.GetBackingUdonBehaviour(_instance.GetComponent<FlareGlbDownloader>());
                    _display = _instance.transform.Find("Downloaded Scene");
                    _loader.InitializeUdonContent(); _interpreter.InitializeUdonContent();
                    _downloader.InitializeUdonContent();
                    _phase = 1; _wait = EditorApplication.timeSinceStartup + .2; return;
                }
                if (_phase == 1)
                {
                    if (EditorApplication.timeSinceStartup < _wait) return;
                    if (SessionState.GetBool(Key + ".network", false))
                    {
                        _readyAt = float.MaxValue; _waitingDownload = true;
                        Click("LOAD DEMO");
                    }
                    else Load(File.ReadAllBytes(FlareGimmickBuilder.DemoPath));
                    _phase = 2; return;
                }
                int status = (int)_loader.GetProgramVariable("Status");
                if (_phase == 13 && (int)_loader.GetProgramVariable("DecodeContinuations") > 2)
                {
                    Click("CLEAR");
                    Check((int)_interpreter.GetProgramVariable("ActiveTweens") == 0, "Cancel clears tween scheduler");
                    Load(File.ReadAllBytes(FlareGimmickBuilder.DemoPath));
                    _phase = 14; return;
                }
                if (_waitingDownload)
                {
                    string downloadState = (string)_downloader.GetProgramVariable("State");
                    if (downloadState.StartsWith("Download failed")) throw new InvalidOperationException(downloadState);
                    if (status == 0) return;
                    _waitingDownload = false;
                }
                if (status == 1) return;
                // Udon's built-in Interact ignores events until the spawned node has run Start.
                if (status == 2)
                {
                    if (_readyAt == float.MaxValue) _readyAt = Time.timeSinceLevelLoad + .2f;
                    if (Time.timeSinceLevelLoad < _readyAt) return;
                }
                if (_phase == 2 || _phase == 4 || _phase == 6 || _phase == 8 || _phase == 9 || _phase == 10 || _phase == 12 || _phase == 14 || _phase == 15)
                {
                    Check(status == 2, "Parse success, phase " + _phase + ": " + _loader.GetProgramVariable("LastError"));
                }
                if (_phase == 2)
                {
                    Transform root = _display.Find("Gimmick Demo");
                    Check(root != null && root.Find("Door/Door Panel") != null, "Hierarchy and node names");
                    Transform button = root.Find("Button");
                    Check(button.GetComponent<BoxCollider>().enabled, "Interact collider enabled");
                    Check(button.GetComponent<MeshFilter>().sharedMesh.vertexCount == 24, "Cube geometry");
                    Check(button.GetComponent<MeshRenderer>().enabled, "Mesh visible");
                    Capture("before");
                    button.GetComponent<UdonBehaviour>().Interact();
                    Check((int)_interpreter.GetProgramVariable("ActiveTweens") == 2, "Only the two animated nodes are scheduled");
                    Check((int)_interpreter.GetProgramVariable("ActionVisits") == 5, "Dispatch visits only the five matching actions");
                    _tweenUntil = Time.timeSinceLevelLoad + .75f; _phase = 3; return;
                }
                if (_phase == 3)
                {
                    if (Time.timeSinceLevelLoad < _tweenUntil) return;
                    Transform root = _display.Find("Gimmick Demo");
                    Debug.Log("[FLARE regression] door=" + root.Find("Door").localEulerAngles +
                        " executed=" + _interpreter.GetProgramVariable("ExecutedActions") + " ignored=" + _interpreter.GetProgramVariable("IgnoredActions") +
                        " nodeOwner=" + root.Find("Button").GetComponent<UdonBehaviour>().GetProgramVariable("Interpreter"));
                    Check(Quaternion.Angle(root.Find("Door").localRotation, Quaternion.Euler(0, 90, 0)) < .1f, "Interact -> custom event -> 90 degree door tween");
                    Transform indicator = root.Find("Indicator");
                    Check(!indicator.gameObject.activeSelf, "toggleActive on another node");
                    Check(Mathf.Abs(indicator.localPosition.x + .4f) < .001f, "move tween completes on inactive target");
                    AudioSource audio = root.Find("Button").GetComponent<AudioSource>();
                    Check(audio.clip != null && Mathf.Abs(audio.volume - .25f) < .001f, "World-owned audio ID and capped volume");
                    Capture("after");
                    Check((int)_interpreter.GetProgramVariable("ActiveTweens") == 0, "Finished tweens leave scheduler");
                    _idleTicks = (int)_interpreter.GetProgramVariable("TickCalls");
                    _tweenUntil = Time.timeSinceLevelLoad + .15f; _phase = 30; return;
                }
                if (_phase == 30)
                {
                    if (Time.timeSinceLevelLoad < _tweenUntil) return;
                    Check((int)_interpreter.GetProgramVariable("TickCalls") == _idleTicks, "Zero cross-Udon node ticks while idle");
                    Load(File.ReadAllBytes(FlareGimmickBuilder.DemoPath)); _phase = 4; return;
                }
                if (_phase == 4)
                {
                    Transform root = _display.Find("Gimmick Demo");
                    Check(Quaternion.Angle(root.Find("Door").localRotation, Quaternion.identity) < .1f, "Reload resets rotation");
                    Check(root.Find("Indicator").gameObject.activeSelf, "Reload resets active state");
                    _bad = 0; Load(Mutate(BadFixtures[_bad])); _phase = 5; return;
                }
                if (_phase == 5)
                {
                    Check(status == 3, "Reject malformed fixture " + BadFixtures[_bad]);
                    Check(!(bool)_interpreter.GetProgramVariable("Ready"), "No partial behavior after error");
                    if (++_bad < BadFixtures.Length) { Load(Mutate(BadFixtures[_bad])); return; }
                    Load(Mutate(6)); _phase = 6; return;
                }
                if (_phase == 6)
                {
                    Check((int)_interpreter.GetProgramVariable("IgnoredActions") == 2, "Unknown command and invalid target ignored");
                    _display.Find("Gimmick Demo/Button").GetComponent<UdonBehaviour>().Interact();
                    Check((int)_interpreter.GetProgramVariable("DroppedEvents") > 0, "Event cycle bounded");
                    Load(File.ReadAllBytes("Library/Flare-texture.glb")); _phase = 8; return;
                }
                if (_phase == 8)
                {
                    Texture tex = _display.Find("Gimmick Demo/Button").GetComponent<MeshRenderer>().sharedMaterial.mainTexture;
                    Check(tex != null && tex.width == 4 && tex.height == 4, "Prepared GLB texture restored");
                    Transform root = _display.Find("Gimmick Demo");
                    Check(root.localScale == new Vector3(2f, .5f, -1f), "Root nonuniform and negative scale preserved");
                    Check(root.Find("Button").GetComponent<Renderer>().sharedMaterial == root.Find("Indicator").GetComponent<Renderer>().sharedMaterial, "Runtime shares one material per definition");
                    Load(Mutate(7)); _phase = 9; return;
                }
                if (_phase == 9)
                {
                    Transform root = _display.Find("Gimmick Demo");
                    root.Find("Button").GetComponent<UdonBehaviour>().Interact();
                    Check(Quaternion.Angle(root.Find("Door").localRotation, Quaternion.Euler(0, -90, 0)) < .1f, "Negative rotation with zero duration");
                    Check(Mathf.Abs(root.Find("Indicator").localPosition.x + .5f) < .001f, "Zero movement preserves position");
                    Check(!root.Find("Indicator").gameObject.activeSelf, "Boolean setActive false");
                    Load(Mutate(8)); _phase = 10; return;
                }
                if (_phase == 10)
                {
                    Transform root = _display.Find("Gimmick Demo");
                    Transform indicator = root.Find("Indicator");
                    UdonBehaviour node = indicator.GetComponent<UdonBehaviour>();
                    // Deliberately leave the visible pose behind an already elapsed tween: an Interact can run before Update.
                    node.SetProgramVariable("_moving", true);
                    node.SetProgramVariable("_fromPosition", new Vector3(18f, 1.5f, 1.6f));
                    node.SetProgramVariable("_toPosition", new Vector3(20f, 1.5f, 1.6f));
                    node.SetProgramVariable("_moveDuration", .1f);
                    node.SetProgramVariable("_moveStart", Time.timeSinceLevelLoad - 1f);
                    root.Find("Button").GetComponent<UdonBehaviour>().Interact();
                    Check(Mathf.Abs(indicator.localPosition.x - 20f) < .001f, "Move checks the sampled pose, cannot escape position 20");
                    Check((int)_interpreter.GetProgramVariable("IgnoredActions") == 1, "Out-of-bounds interrupted tween is rejected");
                    Load(File.ReadAllBytes("Library/Flare-large.glb")); _phase = 12; return;
                }
                if (_phase == 12)
                {
                    Mesh mesh = _display.Find("Gimmick Demo/Button").GetComponent<MeshFilter>().sharedMesh;
                    Check(mesh.vertexCount == 2048 && mesh.triangles.Length == 12288, "Maximum default node geometry restored");
                    Check(mesh.triangles.Take(6).SequenceEqual(new[] { 0, 1, 2, 3, 4, 5 }), "Winding and chunk boundaries preserved");
                    Check((int)_loader.GetProgramVariable("PeakDecodeElements") <= 128, "At most 128 elements decoded per continuation");
                    Check((int)_loader.GetProgramVariable("DecodeContinuations") >= 144, "Large mesh actually split across continuations");
                    Check(mesh.uv.Length == 2048 && mesh.normals.Length == 2048, "Attribute streams restored after chunking");
                    Debug.Log("[FLARE performance] peak elements=" + _loader.GetProgramVariable("PeakDecodeElements") + " chunks=" + _loader.GetProgramVariable("DecodeContinuations") + " idle ticks=0");
                    Load(File.ReadAllBytes("Library/Flare-large.glb")); _phase = 13; return;
                }
                if (_phase == 14)
                {
                    Check(_display.Find("Gimmick Demo/Button").GetComponent<MeshFilter>().sharedMesh.vertexCount == 24, "Cancel during decode then reload has no stale mesh work");
                    _overrideTemplate = new Material((Material)_loader.GetProgramVariable("MaterialTemplate"));
                    _overrideTemplate.color = Color.magenta; _overrideTemplate.mainTexture = Texture2D.blackTexture;
                    _overrideTemplate.mainTextureScale = Vector2.one * 2f; _overrideTemplate.mainTextureOffset = Vector2.one;
                    _loader.SetProgramVariable("MaterialTemplate", _overrideTemplate);
                    Load(Mutate(12)); _phase = 15; return;
                }
                if (_phase == 15)
                {
                    Transform button = _display.Find("Gimmick Demo/Button");
                    Material mat = button.GetComponent<Renderer>().sharedMaterial;
                    Check(mat.color == Color.white && mat.mainTexture == null, "Omitted glTF material does not inherit template tint or texture");
                    Check(mat.mainTextureScale == Vector2.one && mat.mainTextureOffset == Vector2.zero, "Default UV transform does not inherit template");
                    Mesh mesh = button.GetComponent<MeshFilter>().sharedMesh;
                    Check(mesh.normals.Length == 24 && mesh.uv.Length == 0, "Optional normal/UV streams may be absent after a previous textured load");
                    Click("CLEAR");
                    Check((int)_loader.GetProgramVariable("Status") == 0 && !(bool)_interpreter.GetProgramVariable("Ready"), "Clear disables execution");
                    Finish(true, "PASS " + _checks + " assertions (editor export + real Udon VM); export/load/Interact/tweens/audio/reload/invalid input/event limits/texture/zero/reverse/bounds/idle scheduling/2048 vertices/12288 indices/128-element chunks/cancel. Network=" + SessionState.GetBool(Key + ".network", false) + "; headset not tested.");
                }
            }
            catch (Exception error) { Finish(false, error.ToString()); }
        }
        private static void Load(byte[] bytes)
        {
            _readyAt = float.MaxValue;
            _loader.SetProgramVariable("InputBytes", bytes); _loader.SendCustomEvent("LoadInput");
        }
        private static void Click(string name)
        {
            foreach (Button button in _instance.GetComponentsInChildren<Button>(true))
                if (button.name == name)
                {
                    Check(button.onClick.GetPersistentEventCount() == 1, "Serialized UI binding: " + name);
                    button.onClick.Invoke(); return;
                }
            throw new InvalidOperationException("Missing UI button: " + name);
        }
        private static void Check(bool value, string message)
        { _checks++; if (!value) throw new InvalidOperationException(message); }

        private static byte[] Mutate(int variant)
        {
            byte[] input = File.ReadAllBytes(FlareGimmickBuilder.DemoPath);
            int jsonLength = BitConverter.ToInt32(input, 12), header = 20 + jsonLength;
            JObject doc = JObject.Parse(Encoding.UTF8.GetString(input, 20, jsonLength));
            var nodes = (JArray)doc["nodes"];
            if (variant == 0) nodes[1]["children"] = new JArray(0);
            if (variant == 1) nodes[1]["extras"]["vrc_gimmick"]["gimmickId"] = "button";
            if (variant == 2) nodes[0]["translation"] = new JArray(1e20, 0, 0);
            if (variant == 3) doc["accessors"][0]["byteOffset"] = 9999999;
            if (variant == 4) doc["extras"]["flare_rgba_textures"] = new JArray(new JObject { ["width"] = 999999, ["height"] = 512, ["bufferView"] = 0 });
            if (variant == 5)
            {
                var actions = new JArray(); for (int i = 0; i < 65; i++) actions.Add(new JObject { ["type"] = "toggleActive" });
                nodes[3]["extras"]["vrc_gimmick"]["actions"] = actions;
            }
            if (variant == 6)
            {
                nodes[3]["extras"]["vrc_gimmick"]["actions"] = new JArray(
                    new JObject { ["type"] = "emitEvent", ["target"] = "button", ["event"] = "interact" },
                    new JObject { ["type"] = "SendCustomEvent", ["event"] = "Anything" },
                    new JObject { ["type"] = "rotate", ["target"] = "outside_world", ["value"] = 90 });
            }
            if (variant == 7)
            {
                nodes[3]["extras"]["vrc_gimmick"]["actions"] = new JArray(
                    new JObject { ["type"] = "rotate", ["target"] = "door", ["axis"] = "y", ["value"] = -90, ["duration"] = 0 },
                    new JObject { ["type"] = "move", ["target"] = "node_4", ["value"] = new JArray(0, 0, 0), ["duration"] = 0 },
                    new JObject { ["type"] = "setActive", ["target"] = "node_4", ["value"] = false });
            }
            if (variant == 8)
            {
                nodes[4]["translation"] = new JArray(19.5, 1.5, -1.6);
                nodes[3]["extras"]["vrc_gimmick"]["actions"] = new JArray(new JObject { ["type"] = "move", ["target"] = "node_4", ["value"] = new JArray(.5, 0, 0), ["duration"] = 0 });
            }
            if (variant == 9) doc["extensionsRequired"] = "malformed-not-an-array";
            if (variant == 10)
            {
                doc["bufferViews"][0]["byteStride"] = 12;
                doc["extras"]["flare_rgba_textures"] = new JArray(new JObject { ["width"] = 6, ["height"] = 12, ["bufferView"] = 0 });
            }
            if (variant == 12)
                foreach (JObject mesh in (JArray)doc["meshes"])
                {
                    var primitive = (JObject)mesh["primitives"][0]; primitive.Remove("material");
                    ((JObject)primitive["attributes"]).Remove("NORMAL"); ((JObject)primitive["attributes"]).Remove("TEXCOORD_0");
                }
            int length = BitConverter.ToInt32(input, header); var bin = new byte[length]; Buffer.BlockCopy(input, header + 8, bin, 0, length);
            if (variant == 11)
            {
                int accessor = (int)doc["meshes"][0]["primitives"][0]["indices"];
                int view = (int)doc["accessors"][accessor]["bufferView"];
                int offset = (int)doc["bufferViews"][view]["byteOffset"] + (int)doc["bufferViews"][view]["byteLength"] - 2;
                bin[offset] = 255; bin[offset + 1] = 255;
            }
            return FlareGimmickExporter.Container(doc, bin);
        }
        private static void MakeTextureFixture()
        {
            var material = new Material(Shader.Find("Standard")); var texture = new Texture2D(4, 4, TextureFormat.RGBA32, false);
            Color[] pixels = new Color[16]; for (int i = 0; i < pixels.Length; i++) pixels[i] = i % 2 == 0 ? Color.red : Color.blue;
            texture.SetPixels(pixels); texture.Apply(); material.mainTexture = texture;
            GameObject exhibit = FlareGimmickBuilder.MakeDemo(material);
            exhibit.transform.localScale = new Vector3(2f, .5f, -1f);
            try
            {
                byte[] exported = FlareGimmickExporter.Export(exhibit);
                JObject doc = JObject.Parse(Encoding.UTF8.GetString(exported, 20, BitConverter.ToInt32(exported, 12)));
                Check(((JArray)doc["materials"]).Count == 1 && ((JArray)doc["textures"]).Count == 1, "Export deduplicates shared materials and textures");
                byte[] prepared = FlareGimmickExporter.Prepare(exported);
                Check(prepared.SequenceEqual(FlareGimmickExporter.Prepare(prepared)), "Repeated preparation is byte-identical");
                File.WriteAllBytes("Library/Flare-texture.glb", prepared);
            }
            finally { UnityEngine.Object.DestroyImmediate(exhibit); UnityEngine.Object.DestroyImmediate(material); UnityEngine.Object.DestroyImmediate(texture); }
        }
        private static void MakeLargeFixture()
        {
            var material = new Material(Shader.Find("Standard"));
            GameObject exhibit = FlareGimmickBuilder.MakeDemo(material);
            var mesh = new Mesh();
            var vertices = new Vector3[2048]; var normals = new Vector3[2048]; var uv = new Vector2[2048];
            var indices = new int[12288];
            for (int i = 0; i < vertices.Length; i++) { vertices[i] = new Vector3((i % 32) / 32f, (i / 32) / 64f, 0); normals[i] = Vector3.back; uv[i] = new Vector2(vertices[i].x, vertices[i].y); }
            for (int i = 0; i < indices.Length; i++) indices[i] = i % vertices.Length;
            mesh.vertices = vertices; mesh.normals = normals; mesh.uv = uv; mesh.triangles = indices;
            exhibit.transform.Find("Button").GetComponent<MeshFilter>().sharedMesh = mesh;
            try { File.WriteAllBytes("Library/Flare-large.glb", FlareGimmickExporter.Export(exhibit)); }
            finally { UnityEngine.Object.DestroyImmediate(exhibit); UnityEngine.Object.DestroyImmediate(mesh); UnityEngine.Object.DestroyImmediate(material); }
        }
        private static void Finish(bool success, string result)
        {
            if (_overrideTemplate != null) UnityEngine.Object.DestroyImmediate(_overrideTemplate);
            SessionState.SetBool(Key, false);
            File.WriteAllText("Library/FlareGimmickRegression.result", (success ? "PASS\n" : "FAIL\n") + result);
            Debug.Log("[FLARE regression] " + result);
            EditorApplication.Exit(success ? 0 : 1);
        }
        private static void Capture(string suffix)
        {
            var cameraObject = new GameObject("FLARE Proof Camera");
            Camera camera = cameraObject.AddComponent<Camera>();
            camera.transform.position = new Vector3(3.3f, 2.4f, -2.7f);
            camera.transform.LookAt(new Vector3(0, 1f, 1.6f));
            camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = new Color(.12f, .15f, .2f);
            var rt = new RenderTexture(960, 640, 24); camera.targetTexture = rt;
            RenderTexture previous = RenderTexture.active; var image = new Texture2D(960, 640, TextureFormat.RGB24, false);
            try { camera.Render(); RenderTexture.active = rt; image.ReadPixels(new Rect(0, 0, 960, 640), 0, 0); image.Apply(); File.WriteAllBytes("Library/Flare-gimmick-" + suffix + ".png", image.EncodeToPNG()); }
            finally { RenderTexture.active = previous; camera.targetTexture = null; UnityEngine.Object.DestroyImmediate(image); UnityEngine.Object.DestroyImmediate(rt); UnityEngine.Object.DestroyImmediate(cameraObject); }
        }
    }
}
