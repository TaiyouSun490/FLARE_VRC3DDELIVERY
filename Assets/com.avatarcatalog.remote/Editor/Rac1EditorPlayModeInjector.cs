using System;
using UnityEditor;

namespace AvatarCatalog.Remote
{
    /// <summary>
    /// Retired local-file injector kept as a fail-closed compatibility entry point.
    /// Use the fixed-URL HTTPS Udon test runner for end-to-end verification.
    /// </summary>
    public static class Rac1EditorPlayModeInjector
    {
        private const string MenuPath = "Tools/FLARE/Developer/Tests/RAC1 Udon Play Mode/Inject Downloaded RAC1 into Selected Loader...";

        [MenuItem(MenuPath)]
        public static void PickAndInjectDownloadedRac1()
        {
            RejectLocalInjection();
        }

        [MenuItem(MenuPath, true)]
        private static bool ValidatePickAndInjectDownloadedRac1()
        {
            return false;
        }

        public static void InjectFileIntoSelectedLoader(string path)
        {
            RejectLocalInjection();
        }

        private static void RejectLocalInjection()
        {
            throw new NotSupportedException(
                "Local RAC1 injection is retired. Use the fixed-URL HTTPS Udon test runner instead.");
        }
    }
}
