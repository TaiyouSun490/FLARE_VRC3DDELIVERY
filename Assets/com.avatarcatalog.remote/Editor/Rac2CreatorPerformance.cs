using UnityEditor;
using UnityEngine;

namespace AvatarCatalog.Remote
{
    public sealed partial class Rac2CreatorWindow
    {
        private void DrawPerformanceRating()
        {
            if(!_root) return;
            var s=Rac2PerformanceRating.Measure(_root,CalculateFrameCount(),_includeVatNormals);
            using(new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField(L("PC drawing estimate (RAC2 output only)"),EditorStyles.boldLabel);
                if(s.Meshes+s.Emitters==0) { EditorGUILayout.HelpBox(L("No supported objects to rate."),MessageType.Info); return; }
                DrawRankBadge(s.Overall);
                EditorGUILayout.LabelField(L("Triangles"),s.Triangles.ToString("N0")+"  /  "+Rac2PerformanceRating.Names[s.TriangleRank]);
                EditorGUILayout.LabelField(L("Material slots"),s.Materials+"  /  "+Rac2PerformanceRating.Names[s.MaterialRank]);
                EditorGUILayout.LabelField(L("Restored meshes"),s.Meshes+"  /  "+Rac2PerformanceRating.Names[s.MeshRank]);
                EditorGUILayout.LabelField(L("Particle count"),s.Emitters+L(" emitters / maximum ")+s.Particles+"  /  "+Rac2PerformanceRating.Names[s.ParticleRank]);
                EditorGUILayout.LabelField(L("Vertices / estimated VAT data"),s.Vertices.ToString("N0")+" / "+(s.VatBytes/1048576d).ToString("F1")+" MiB");
                EditorGUILayout.HelpBox(L("Not an official VRChat avatar rank. Uses the worst displayed metric. Inactive objects are excluded. Standard textures, shaders, particle mesh cost and temporary memory are not rated. VAT storage is listed separately; actual RAM use is higher."),MessageType.Info);
                if(s.Overall>=3) EditorGUILayout.HelpBox(L("High rendering cost: take care in crowded worlds. Optimization is recommended, but this estimate alone does not prevent export."),MessageType.Warning);
                EditorGUILayout.LabelField(L("PC safety limits: 250,000 vertices / 500,000 triangles / 128 MiB stored and expanded"),EditorStyles.wordWrappedMiniLabel);
                if(s.Vertices>Rac2Capacity.MaxVertices || s.Triangles>Rac2Capacity.MaxIndices/3 || s.VatBytes>Rac2Capacity.MaxBytes)
                    EditorGUILayout.HelpBox(L("Safety limit exceeded. Reduce mesh complexity or VAT frames before exporting."),MessageType.Error);
                if(GUILayout.Button(L("Open VRChat PC ranking criteria"))) Application.OpenURL("https://creators.vrchat.com/avatars/avatar-performance-ranking-system/");
            }
        }
        private static void DrawRankBadge(int rank)
        {
            Color[] colors={new Color(.3f,.9f,.65f),new Color(.55f,.85f,.3f),new Color(1f,.85f,.25f),new Color(1f,.55f,.2f),new Color(1f,.3f,.35f)};
            var rect=GUILayoutUtility.GetRect(240,32);
            var icon=new Rect(rect.x+4,rect.y+3,26,26);
            EditorGUI.DrawRect(icon,colors[rank]);
            GUI.Label(icon,rank==0?"★":rank<=2?"●":rank==3?"!":"!!",new GUIStyle(EditorStyles.boldLabel){alignment=TextAnchor.MiddleCenter,normal={textColor=Color.black}});
            GUI.Label(new Rect(rect.x+38,rect.y+4,rect.width-38,26),Rac2PerformanceRating.Names[rank]+(rank==4?L(" — very heavy"):rank==3?L(" — heavy"):""),EditorStyles.boldLabel);
        }
    }
}
