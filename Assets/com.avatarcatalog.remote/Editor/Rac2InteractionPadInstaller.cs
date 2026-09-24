using System;
using UdonSharp.Compiler;
using UdonSharpEditor;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Components;

namespace AvatarCatalog.Remote
{
    public static class Rac2InteractionPadInstaller
    {
        private const string PrefabPath = "Assets/NightSlotMall/Prefabs/RAC-3D-ImagePad.prefab";

        [MenuItem("Tools/FLARE/Developer/Installers/Install RAC2 Interaction Support")]
        public static void Install()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException("Exit Play Mode before installing RAC2 interaction support.");

            UdonSharpCompilerV1.CompileSync(new UdonSharpCompileOptions { IsEditorBuild = true });
            GameObject root = PrefabUtility.LoadPrefabContents(PrefabPath);
            try
            {
                Rac2RuntimeLoader loader = root.GetComponentInChildren<Rac2RuntimeLoader>(true);
                if (loader == null) throw new InvalidOperationException("RAC2 Runtime Loader is missing from the Pad.");
                Configure(loader);
                UdonSharpEditorUtility.CopyProxyToUdon(loader, ProxySerializationPolicy.All);
                PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
                AssetDatabase.SaveAssets();
                Debug.Log("[RAC2 Interaction] BoxCollider, Pickup, ObjectSync and load-position reset installed.");
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        public static void Configure(Rac2RuntimeLoader loader)
        {
            GameObject target = loader.gameObject;
            BoxCollider collider = target.GetComponent<BoxCollider>();
            if (collider == null) collider = target.AddComponent<BoxCollider>();
            collider.enabled = false;
            collider.isTrigger = false;

            Rigidbody body = target.GetComponent<Rigidbody>();
            if (body == null) body = target.AddComponent<Rigidbody>();
            body.useGravity = false;
            body.isKinematic = true;
            body.velocity = Vector3.zero;
            body.angularVelocity = Vector3.zero;

            VRCPickup pickup = target.GetComponent<VRCPickup>();
            if (pickup == null) pickup = target.AddComponent<VRCPickup>();
            pickup.pickupable = false;
            pickup.InteractionText = "Pickup RAC2 Exhibit";
            pickup.UseText = "Use";
            pickup.proximity = 2f;

            VRCObjectSync objectSync = target.GetComponent<VRCObjectSync>();
            if (objectSync == null) objectSync = target.AddComponent<VRCObjectSync>();

            loader.TargetCollider = collider;
            loader.TargetRigidbody = body;
            loader.TargetPickup = pickup;
            loader.TargetObjectSync = objectSync;
            loader.InitialLocalPosition = target.transform.localPosition;
            loader.InitialLocalEulerAngles = target.transform.localEulerAngles;
        }
    }
}
