using UnityEngine;

namespace AvatarCatalog.Remote
{
    [DisallowMultipleComponent]
    public sealed class Rac2ProductMetadata : MonoBehaviour
    {
        public bool IncludeProductMetadata = true;
        public string ProductName = "";
        public string CreatorName = "";
        public string ProductUrl = "";
        public string AvatarBlueprintId = "";
        public bool TrialEnabled;
    }
}
