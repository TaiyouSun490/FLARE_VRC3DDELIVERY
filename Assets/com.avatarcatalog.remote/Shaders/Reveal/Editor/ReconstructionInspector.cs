using UnityEditor;
using UnityEngine;

namespace AvatarCatalog.Remote.Editor
{
    // lilToon drawers expect translated, pipe-delimited labels supplied by its inspector.
    // PropertiesDefaultGUI must not be used for the inherited lilToon property block.
    public sealed class ReconstructionInspector : lilToon.lilToonInspector
    {
        private MaterialProperty[] customProperties;
        private MaterialEditor customEditor;

        public override void OnGUI(MaterialEditor editor, MaterialProperty[] properties)
        {
            customEditor = editor;
            base.OnGUI(editor, properties);
        }

        protected override void LoadCustomProperties(MaterialProperty[] properties, Material material)
        {
            isCustomShader = true; // Keep lilToon render-mode controls from replacing our shader.
            customProperties = properties;
        }

        protected override void DrawCustomProperties(Material material)
        {
            // Follow the surrounding lilToon Inspector language, not a separate mixed-language panel.
            bool japanese = lilToon.lilLanguageManager.langSet.languageName == "ja-JP";
            EditorGUILayout.HelpBox(japanese
                ? "読み込み時はローダーの出現エフェクト設定を使用します。テンプレートのプレビューを変更しても、ローダーの設定は変わりません。"
                : "RAC2 loading applies the loader's Reconstruction settings to runtime material instances. Template previews do not change the loader settings.", MessageType.Info);
            foreach (var property in customProperties)
            {
                if (!property.name.StartsWith("_RacReveal")) continue;
                if ((property.flags & MaterialProperty.PropFlags.HideInInspector) != 0) continue;
                customEditor.ShaderProperty(property, japanese ? JapaneseLabel(property.name, property.displayName) : property.displayName);
            }
        }

        private static string JapaneseLabel(string property, string fallback)
        {
            switch (property)
            {
                case "_RacRevealEnabled": return "出現エフェクトを有効化";
                case "_RacRevealStart": return "開始時刻";
                case "_RacRevealDuration": return "出現時間（秒）";
                case "_RacRevealProgressOverride": return "プレビュー進行度（-1で時刻を使用）";
                case "_RacRevealCellSize": return "ブロックの大きさ（展示座標・m）";
                case "_RacRevealScatter": return "ブロックのばらつき";
                case "_RacRevealEdgeWidth": return "発光帯の幅（展示座標・m）";
                case "_RacRevealEdgeColor": return "縁の発光色";
                case "_RacRevealInflation": return "縁の膨らみ（展示座標・m）";
                case "_RacRevealHeight": return "展示物の最低・最高位置";
                default: return fallback;
            }
        }
    }
}
