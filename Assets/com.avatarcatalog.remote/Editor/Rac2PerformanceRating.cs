using System;
using UnityEngine;

namespace AvatarCatalog.Remote
{
    // PC thresholds checked 2026-09-21 against the official performance-ranking page.
    // This is a subset for EXPORTED draw geometry, not the original avatar's SDK rank.
    public static class Rac2PerformanceRating
    {
        public static readonly string[] Names = { "Excellent", "Good", "Medium", "Poor", "Very Poor" };
        public static int Rank(long value, long excellent, long good, long medium, long poor)
        { return value <= excellent ? 0 : value <= good ? 1 : value <= medium ? 2 : value <= poor ? 3 : 4; }
        public sealed class Stats
        {
            public long Vertices, Triangles, VatBytes;
            public int Meshes, Materials, Emitters, Particles;
            public int TriangleRank => Rank(Triangles,32000,70000,70000,70000);
            public int MaterialRank => Rank(Materials,4,8,16,32);
            public int MeshRank => Rank(Meshes,4,8,16,24); // VAT loads as basic MeshRenderer, not skinned.
            public int ParticleRank => Math.Max(Rank(Emitters,0,4,8,16),Rank(Particles,0,300,1000,2500));
            public int Overall => Math.Max(Math.Max(TriangleRank,MaterialRank),Math.Max(MeshRank,ParticleRank));
        }
        public static Stats Measure(GameObject root, int frames, bool normals)
        {
            var result = new Stats();
            if(!root) return result;
            foreach(var renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                if(!renderer.enabled || !renderer.gameObject.activeInHierarchy || renderer.GetComponentInParent<ParticleSystem>()) continue;
                var skinned = renderer as SkinnedMeshRenderer;
                var filter = renderer.GetComponent<MeshFilter>();
                Mesh mesh = skinned ? skinned.sharedMesh : renderer is MeshRenderer && filter ? filter.sharedMesh : null;
                if(!mesh) continue;
                result.Meshes++; result.Vertices += mesh.vertexCount; result.Materials += mesh.subMeshCount;
                for(int s=0;s<mesh.subMeshCount;s++) result.Triangles += (long)mesh.GetIndexCount(s)/3;
                if(skinned && frames>0)
                {
                    int width=Mathf.Min(2048,Mathf.NextPowerOfTwo(mesh.vertexCount));
                    if(width>0) result.VatBytes += (long)width*((mesh.vertexCount+width-1)/width)*frames*(normals?12:8);
                }
            }
            foreach(var particle in root.GetComponentsInChildren<ParticleSystem>(true))
            {
                var renderer=particle.GetComponent<ParticleSystemRenderer>();
                if(!particle.gameObject.activeInHierarchy || !renderer || !renderer.enabled) continue;
                result.Emitters++; result.Materials++; result.Particles += particle.main.maxParticles;
            }
            return result;
        }
    }
}
