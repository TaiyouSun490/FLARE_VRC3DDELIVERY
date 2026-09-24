// Copy into the remote Editor assembly of an isolated SDK-equipped test project.
// CLI only: no windows, scene playback, assets or EditorPrefs are changed.
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using UdonSharp;
using UdonSharp.Compiler;
using UdonSharpEditor;
namespace AvatarCatalog.Remote
{
    public static class FlareLocalizationNativeCheck
    {
        private static bool errors;
        public static void Run()
        {
            var report = new List<string>();
            GameObject root = null;
            try
            {
                var table = (Dictionary<string, string>)typeof(FlareLocalization).GetField("JapaneseText", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
                foreach (var item in table)
                {
                    Require(FlareLocalization.Text(item.Key, false) == item.Key, "English lookup changed");
                    Require(FlareLocalization.Text(item.Key, true) == item.Value, "Japanese lookup missing");
                    Require(!string.IsNullOrEmpty(item.Value), "Empty translation");
                }
                Require(FlareLocalization.Text("asset-name-123", true) == "asset-name-123", "Unknown keys changed");
                report.Add("PASS: " + table.Count + " JP/EN entries; unknown-key fallback; no EditorPrefs mutation.");
                root = new GameObject("Localization fixture");
                var pad = root.AddUdonSharpComponent<RuntimeRacImagePadControllerV2>();
                var buttonObject = new GameObject("Button", typeof(RectTransform), typeof(Button));
                buttonObject.transform.SetParent(root.transform);
                var labelObject = new GameObject("Text", typeof(RectTransform), typeof(Text));
                labelObject.transform.SetParent(buttonObject.transform);
                var label = labelObject.GetComponent<Text>();
                pad.Rac2Button = buttonObject.GetComponent<Button>();
                pad.SetJapanese(); Require(label.text == "RAC2を読み込む", "Japanese button");
                pad.SetEnglish(); Require(label.text == "LOAD RAC2", "English button");
                pad.SetJapanese(); Require(label.text == "RAC2を読み込む", "Reverse switch");
                pad.StatusText = label;
                pad.RetryLast(); Require(label.text.Contains("履歴"), "Japanese empty-retry feedback");
                pad.SetEnglish(); pad.RetryLast(); Require(label.text.Contains("No previous request"), "English empty-retry feedback");
                report.Add("PASS: JP -> EN -> JP buttons; null optional references; empty retry feedback in both languages (C# proxy, not Udon VM).");
                UnityEngine.Object.DestroyImmediate(root); root = null;
                errors = false; Application.logMessageReceived += Log;
                try { UdonSharpCompilerV1.CompileSync(new UdonSharpCompileOptions { IsEditorBuild = true }); }
                finally { Application.logMessageReceived -= Log; }
                Require(!errors && !UdonSharpProgramAsset.AnyUdonSharpScriptHasError(), "Udon compilation errors");
                report.Add("PASS: isolated project Udon compilation. No VRChat playback or visual/layout test.");
                File.WriteAllLines("localization-check.txt", report); EditorApplication.Exit(0);
            }
            catch (Exception e)
            {
                if (root != null) UnityEngine.Object.DestroyImmediate(root);
                report.Add("FAIL: " + e); File.WriteAllLines("localization-check.txt", report); EditorApplication.Exit(1);
            }
        }
        private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
        private static void Log(string message, string stack, LogType type) { if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert) errors = true; }
    }
}
