using System;
using System.IO;
using UdonSharp;
using UdonSharp.Compiler;
using UdonSharpEditor;
using UnityEditor;
using UnityEditor.Events;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDK3.Components;
using VRC.SDKBase;

namespace AvatarCatalog.Remote
{
    public static class FlareGimmickBuilder
    {
        public const string Folder = "Assets/FLAREGimmicks";
        public const string PlayerPath = Folder + "/FLARE-GLB-Gimmick-Player.prefab";
        public const string DemoPath = Folder + "/Samples/button-door.glb";
        public const string DemoUrl = "https://raw.githubusercontent.com/TaiyouSun490/FLARE_VRC3DDELIVERY/feature/gimmick-metadata-runtime/Assets/FLAREGimmicks/Samples/button-door.glb";

        [MenuItem("Tools/FLARE/Build Gimmick Prefab and Demo")]
        public static void Build()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Exit Play Mode first.");
            EnsureFolder(Folder); EnsureFolder(Folder + "/Programs"); EnsureFolder(Folder + "/Samples");
            EnsureProgram("FlareRuntimeNode"); EnsureProgram("FlareGimmickInterpreter");
            EnsureProgram("FlareGlbSceneLoader"); EnsureProgram("FlareGlbDownloader");
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            UdonSharpCompilerV1.CompileSync(new UdonSharpCompileOptions { IsEditorBuild = true });
            if (UdonSharpProgramAsset.AnyUdonSharpScriptHasError()) throw new InvalidOperationException("FLARE Udon compilation failed.");
            string materialPath = Folder + "/FLARE-Opaque.mat";
            Material material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
            if (material == null)
            {
                material = new Material(Shader.Find("Standard"));
                material.SetFloat("_Metallic", 0f); material.SetFloat("_Glossiness", 0.2f);
                AssetDatabase.CreateAsset(material, materialPath);
            }
            string nodePath = Folder + "/FLARE-RuntimeNode.prefab";
            var nodeObject = new GameObject("FLARE Runtime Node");
            try
            {
                var node = nodeObject.AddUdonSharpComponent<FlareRuntimeNode>();
                node.Filter = nodeObject.AddComponent<MeshFilter>(); node.Renderer = nodeObject.AddComponent<MeshRenderer>();
                node.Box = nodeObject.AddComponent<BoxCollider>(); node.Box.enabled = false;
                node.Audio = nodeObject.AddComponent<AudioSource>(); node.Audio.playOnAwake = false;
                node.Audio.spatialBlend = 1f; node.Audio.minDistance = .5f; node.Audio.maxDistance = 5f;
                node.Audio.rolloffMode = AudioRolloffMode.Linear; node.Renderer.enabled = false;
                var backing = UdonSharpEditorUtility.GetBackingUdonBehaviour(node);
                backing.interactText = "Activate"; backing.proximity = 2f;
                UdonSharpEditorUtility.CopyProxyToUdon(node, ProxySerializationPolicy.All);
                nodeObject.SetActive(false); PrefabUtility.SaveAsPrefabAsset(nodeObject, nodePath);
            }
            finally { UnityEngine.Object.DestroyImmediate(nodeObject); }
            GenerateTone();
            var root = new GameObject("FLARE GLB Gimmick Player");
            try
            {
                var interpreter = root.AddUdonSharpComponent<FlareGimmickInterpreter>();
                interpreter.AudioIds = new[] { "confirm" };
                interpreter.AudioClips = new[] { AssetDatabase.LoadAssetAtPath<AudioClip>(Folder + "/Samples/confirm.wav") };
                var loader = root.AddUdonSharpComponent<FlareGlbSceneLoader>();
                loader.Interpreter = interpreter; loader.RuntimeNodePrefab = AssetDatabase.LoadAssetAtPath<GameObject>(nodePath);
                loader.MaterialTemplate = material;
                var display = new GameObject("Downloaded Scene"); display.transform.SetParent(root.transform, false); loader.DisplayRoot = display.transform;
                var downloader = root.AddUdonSharpComponent<FlareGlbDownloader>();
                downloader.SceneLoader = loader; downloader.RuntimeUrl = new VRCUrl(DemoUrl); downloader.DemoUrl = new VRCUrl(DemoUrl);
                BuildControls(root.transform, downloader);
                UdonSharpEditorUtility.CopyProxyToUdon(interpreter, ProxySerializationPolicy.All);
                UdonSharpEditorUtility.CopyProxyToUdon(loader, ProxySerializationPolicy.All);
                UdonSharpEditorUtility.CopyProxyToUdon(downloader, ProxySerializationPolicy.All);
                PrefabUtility.SaveAsPrefabAsset(root, PlayerPath);
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
            GameObject exhibit = MakeDemo(material);
            try { File.WriteAllBytes(DemoPath, FlareGimmickExporter.Export(exhibit)); PrefabUtility.SaveAsPrefabAsset(exhibit, Folder + "/Samples/Authoring-Demo.prefab"); }
            finally { UnityEngine.Object.DestroyImmediate(exhibit); }
            AssetDatabase.SaveAssets(); AssetDatabase.Refresh();
            Debug.Log("[FLARE] Prefabs and GLB demo built.");
        }

        public static GameObject MakeDemo(Material material)
        {
            var root = new GameObject("Gimmick Demo");
            GameObject pivot = new GameObject("Door"); pivot.transform.SetParent(root.transform, false); pivot.transform.localPosition = new Vector3(.3f, 0, 2f);
            Cube(pivot.transform, "Door Panel", new Vector3(.5f, 1f, 0), new Vector3(1f, 2f, .08f), material);
            GameObject button = Cube(root.transform, "Button", new Vector3(-.5f, 1f, 1.6f), Vector3.one * .25f, material);
            GameObject indicator = Cube(root.transform, "Indicator", new Vector3(-.5f, 1.5f, 1.6f), Vector3.one * .15f, material);
            var door = pivot.AddComponent<FlareGimmickDefinition>(); door.GimmickId = "door"; door.On = "open";
            door.Actions = new[] { new FlareDeclarativeAction { Type = FlareActionKind.rotate, Value = 90, Duration = .5f } };
            var press = button.AddComponent<FlareGimmickDefinition>(); press.GimmickId = "button";
            press.Actions = new[] {
                new FlareDeclarativeAction { Type = FlareActionKind.emitEvent, Target = pivot.transform, Event = "open" },
                new FlareDeclarativeAction { Type = FlareActionKind.move, Target = indicator.transform, Move = new Vector3(.1f, 0, 0), Duration = .25f },
                new FlareDeclarativeAction { Type = FlareActionKind.toggleActive, Target = indicator.transform },
                new FlareDeclarativeAction { Type = FlareActionKind.playAudio, ClipId = "confirm", Value = .5f }
            };
            return root;
        }
        private static GameObject Cube(Transform parent, string name, Vector3 position, Vector3 scale, Material material)
        {
            GameObject item = GameObject.CreatePrimitive(PrimitiveType.Cube); item.name = name; item.transform.SetParent(parent, false);
            item.transform.localPosition = position; item.transform.localScale = scale; item.GetComponent<MeshRenderer>().sharedMaterial = material; return item;
        }
        private static void BuildControls(Transform parent, FlareGlbDownloader downloader)
        {
            GameObject ui = new GameObject("FLARE Controls", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster), typeof(VRCUiShape));
            ui.transform.SetParent(parent, false); ui.transform.localPosition = new Vector3(0, 1.5f, .8f); ui.transform.localScale = Vector3.one * .0015f;
            ((RectTransform)ui.transform).sizeDelta = new Vector2(800, 240); ui.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;
            Label(ui.transform, "FLARE - GLB Gimmicks", new Vector2(0, 95), new Vector2(800, 36));
            var inputObj = Rect("GLB URL", ui.transform, new Vector2(0, 45), new Vector2(760, 45));
            inputObj.AddComponent<Image>().color = new Color(.12f, .12f, .15f);
            var input = inputObj.AddComponent<VRCUrlInputField>(); input.targetGraphic = inputObj.GetComponent<Image>();
            input.textComponent = Label(inputObj.transform, "", Vector2.zero, new Vector2(740, 40));
            input.placeholder = Label(inputObj.transform, "Paste GLB URL, then LOAD", Vector2.zero, new Vector2(740, 40));
            input.characterLimit = 2048; downloader.UrlInput = input;
            var backing = UdonSharpEditorUtility.GetBackingUdonBehaviour(downloader);
            string[] labels = { "LOAD URL", "LOAD DEMO", "CLEAR" }; string[] events = { "LoadFromInput", "LoadDemo", "Clear" };
            for (int i = 0; i < 3; i++)
            {
                GameObject button = Rect(labels[i], ui.transform, new Vector2((i - 1) * 250, -15), new Vector2(230, 45));
                button.AddComponent<Image>().color = new Color(.15f, .32f, .45f); Button b = button.AddComponent<Button>();
                Label(button.transform, labels[i], Vector2.zero, new Vector2(230, 40));
                UnityEventTools.AddStringPersistentListener(b.onClick, backing.SendCustomEvent, events[i]);
            }
            downloader.StatusText = Label(ui.transform, "Ready - choose LOAD DEMO", new Vector2(0, -75), new Vector2(780, 50));
        }
        private static GameObject Rect(string name, Transform parent, Vector2 position, Vector2 size)
        {
            var go = new GameObject(name, typeof(RectTransform)); go.transform.SetParent(parent, false);
            RectTransform rect = (RectTransform)go.transform; rect.anchoredPosition = position; rect.sizeDelta = size; return go;
        }
        private static Text Label(Transform parent, string text, Vector2 position, Vector2 size)
        {
            Text label = Rect("Label", parent, position, size).AddComponent<Text>(); label.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            label.fontSize = 20; label.color = Color.white; label.alignment = TextAnchor.MiddleCenter; label.text = text; label.raycastTarget = false; return label;
        }
        private static void GenerateTone()
        {
            using (var stream = File.Create(Folder + "/Samples/confirm.wav"))
            using (var writer = new BinaryWriter(stream))
            {
                int samples = 22050; writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + samples * 2);
                writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16); writer.Write((short)1); writer.Write((short)1);
                writer.Write(22050); writer.Write(44100); writer.Write((short)2); writer.Write((short)16);
                writer.Write(System.Text.Encoding.ASCII.GetBytes("data")); writer.Write(samples * 2);
                for (int i = 0; i < samples; i++) writer.Write((short)(Math.Sin(i * Math.PI * 2 * 660 / 22050) * 4000 * (1f - (float)i / samples)));
            }
            AssetDatabase.ImportAsset(Folder + "/Samples/confirm.wav", ImportAssetOptions.ForceSynchronousImport);
        }
        private static void EnsureProgram(string name)
        {
            string path = Folder + "/Programs/" + name + ".asset";
            if (AssetDatabase.LoadAssetAtPath<UdonSharpProgramAsset>(path) != null) return;
            var program = ScriptableObject.CreateInstance<UdonSharpProgramAsset>();
            program.sourceCsScript = AssetDatabase.LoadAssetAtPath<MonoScript>("Assets/com.avatarcatalog.remote/Runtime/" + name + ".cs");
            AssetDatabase.CreateAsset(program, path);
        }
        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            string parent = Path.GetDirectoryName(path).Replace('\\', '/'); EnsureFolder(parent); AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
        }
    }
}
