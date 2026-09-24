// Opt-in isolated -batchmode -nographics -executeMethod FlareBoundsNativeCheck.Run.
// Copy the two exporter sources and Rac2Capacity alongside this script in Assets/Editor.
using System;
using System.IO;
using System.Collections.Generic;
using AvatarCatalog.Remote;
using UnityEditor;
using UnityEngine;

public static class FlareBoundsNativeCheck
{
    public static void Run()
    {
        var report = new List<string>();
        Mesh mesh = null; Material material = null;
        try
        {
            mesh = new Mesh();
            mesh.vertices = new[] { new Vector3(-.5f,0,-.25f), new Vector3(.5f,0,.25f), new Vector3(0,1,0) };
            mesh.triangles = new[] { 0, 1, 2 };
            // Deliberately reproduce oversized/stale renderer culling bounds.
            mesh.bounds = new Bounds(Vector3.one * 2, Vector3.one * 20);
            var original = mesh.bounds;
            material = new Material(Shader.Find("Unlit/Color"));
            for (int mode = 0; mode < 4; mode++)
            {
                var bundle = new Rac2BinaryExporter.BundleData { Bounds = original };
                var node = new Rac2BinaryExporter.RenderNodeData { Mesh = mesh, Materials = new[] { material } };
                if (mode == 1) { node.LocalRotation = Quaternion.Euler(13,42,-8); node.LocalScale = new Vector3(-2,.5f,1.5f); node.LocalPosition = new Vector3(1,-2,3); }
                if (mode == 2) node.Vat = new Rac2BinaryExporter.VatClipData {
                    Positions = new[] { mesh.vertices, new[] { new Vector3(-1,0,-.25f), new Vector3(.5f,2,.25f), new Vector3(0,1,0) } }
                };
                Bounds expected;
                if (mode == 3)
                {
                    var origin = new Vector3(2,3,4);
                    bundle.Bounds = expected = new Bounds(origin,Vector3.zero);
                    bundle.ParticleEmitters.Add(new Rac2BinaryExporter.ParticleEmitterData {
                        Mesh = mesh, Particle = new Rac2BinaryExporter.ParticleData { Origin = origin }
                    });
                }
                else
                {
                    bundle.RenderNodes.Add(node);
                    var local = mode == 2 ? new Bounds(new Vector3(-.25f,1,0),new Vector3(1.5f,2,.5f))
                        : new Bounds(new Vector3(0,.5f,0),new Vector3(1,1,.5f));
                    expected = TransformBounds(local,node);
                }
                string path = Path.GetFullPath("bounds-check-" + mode + ".rac2");
                Rac2BinaryExporter.ExportBundle(bundle,path,32,false,false);
                byte[] bytes = File.ReadAllBytes(path);
                int offset = (int)BitConverter.ToUInt32(bytes,28);
                var actual = new Bounds(ReadVector(bytes,offset),ReadVector(bytes,offset+12));
                Require((actual.min-expected.min).magnitude < .00001f && (actual.max-expected.max).magnitude < .00001f,
                    "META mismatch in mode " + mode + ": " + actual + " vs " + expected);
                Require(mesh.bounds == original, "Export modified original mesh bounds");
                report.Add("PASS mode=" + mode + " META=" + actual);
            }
            report.Add("PASS: oversized static bounds, rotated/mirrored node, VAT extent and particle-only anchor. Original mesh preserved.");
            File.WriteAllLines("bounds-check.txt",report);
            EditorApplication.Exit(0);
        }
        catch(Exception e) { report.Add("FAIL: " + e); File.WriteAllLines("bounds-check.txt",report); EditorApplication.Exit(1); }
        finally { if(mesh) UnityEngine.Object.DestroyImmediate(mesh); if(material) UnityEngine.Object.DestroyImmediate(material); }
    }
    private static void Require(bool ok,string text) { if(!ok) throw new Exception(text); }
    private static Vector3 ReadVector(byte[] b,int p) => new Vector3(BitConverter.ToSingle(b,p),BitConverter.ToSingle(b,p+4),BitConverter.ToSingle(b,p+8));
    private static Bounds TransformBounds(Bounds local,Rac2BinaryExporter.RenderNodeData node)
    {
        Bounds result = default(Bounds);
        for(int c=0;c<8;c++)
        {
            Vector3 v = new Vector3((c&1)==0?local.min.x:local.max.x,(c&2)==0?local.min.y:local.max.y,(c&4)==0?local.min.z:local.max.z);
            v = node.LocalPosition + node.LocalRotation * Vector3.Scale(v,node.LocalScale);
            if(c==0) result = new Bounds(v,Vector3.zero); else result.Encapsulate(v);
        }
        return result;
    }
}
