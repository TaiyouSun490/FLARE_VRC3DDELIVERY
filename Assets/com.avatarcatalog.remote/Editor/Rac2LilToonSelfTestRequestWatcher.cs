using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace AvatarCatalog.Remote
{
    /// <summary>Editor-only file trigger used by automated validation without entering Play Mode.</summary>
    [InitializeOnLoad]
    public static class Rac2LilToonSelfTestRequestWatcher
    {
        private const string RequestPath = "Library/Rac2LilToonSelfTest.run";

        static Rac2LilToonSelfTestRequestWatcher()
        {
            EditorApplication.update += Consume;
        }

        private static void Consume()
        {
            string path = Path.GetFullPath(RequestPath);
            if (!File.Exists(path)) return;
            File.Delete(path);
            try
            {
                Rac2LilToonSelfTest.Run();
                Debug.Log("[RAC2 lilToon Self-Test Request] PASS");
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                Debug.LogError("[RAC2 lilToon Self-Test Request] FAIL");
            }
        }
    }
}
