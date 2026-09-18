using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace AvatarCatalog.Remote
{
    [InitializeOnLoad]
    internal static class Rac2GlbUpgradeRequestWatcher
    {
        private const string RequestPath = "Library/NightSlotRac2Pad.upgrade";

        static Rac2GlbUpgradeRequestWatcher()
        {
            EditorApplication.delayCall += Consume;
        }

        private static void Consume()
        {
            string path = Path.GetFullPath(RequestPath);
            if (!File.Exists(path)) return;
            try
            {
                File.Delete(path);
                Rac2GlbImagePadInstaller.UpgradePrefab();
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
            }
        }
    }
}

