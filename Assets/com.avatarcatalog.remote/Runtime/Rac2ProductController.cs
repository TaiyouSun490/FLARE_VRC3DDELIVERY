using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDK3.Components;
using VRC.SDKBase;

namespace AvatarCatalog.Remote
{
    /// <summary>Displays portable RAC2 product metadata and exposes an optional avatar trial pedestal.</summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public sealed class Rac2ProductController : UdonSharpBehaviour
    {
        [Header("Product display")]
        public GameObject ProductDisplayRoot;
        public Text ProductNameText;
        public Text CreatorText;
        public Text ProductUrlText;
        public Text StatusText;

        [Header("Trial")]
        public VRCAvatarPedestal TargetPedestal;
        public GameObject PedestalVisualRoot;
        public GameObject TrialButtonRoot;

        [Header("Loaded metadata")]
        public bool HasProduct;
        public string ProductName = "";
        public string CreatorName = "";
        public string ProductUrl = "";
        public string AvatarBlueprintId = "";
        public bool TrialEnabled;
        public int TrialRequestCount;

        private void Start()
        {
            RefreshProduct();
        }

        public void ApplyProduct(string productName, string creatorName, string productUrl, string avatarBlueprintId, bool trialEnabled)
        {
            HasProduct = true;
            ProductName = productName == null ? "" : productName;
            CreatorName = creatorName == null ? "" : creatorName;
            ProductUrl = productUrl == null ? "" : productUrl;
            AvatarBlueprintId = avatarBlueprintId == null ? "" : avatarBlueprintId;
            TrialEnabled = trialEnabled && IsValidAvatarId(AvatarBlueprintId) && TargetPedestal != null;
            if (TrialEnabled) TargetPedestal.SwitchAvatar(AvatarBlueprintId);
            RefreshProduct();
        }

        public void ClearProduct()
        {
            HasProduct = false;
            ProductName = "";
            CreatorName = "";
            ProductUrl = "";
            AvatarBlueprintId = "";
            TrialEnabled = false;
            RefreshProduct();
        }

        public void RefreshProduct()
        {
            if (ProductDisplayRoot != null) ProductDisplayRoot.SetActive(HasProduct);
            if (ProductNameText != null) ProductNameText.text = string.IsNullOrEmpty(ProductName) ? "Untitled product" : ProductName;
            if (CreatorText != null) CreatorText.text = string.IsNullOrEmpty(CreatorName) ? "" : "by " + CreatorName;
            if (ProductUrlText != null) ProductUrlText.text = ProductUrl;
            bool canTry = HasProduct && TrialEnabled && IsValidAvatarId(AvatarBlueprintId) && TargetPedestal != null;
            if (PedestalVisualRoot != null) PedestalVisualRoot.SetActive(canTry);
            if (TrialButtonRoot != null) TrialButtonRoot.SetActive(canTry);
            if (StatusText != null) StatusText.text = canTry ? "Avatar trial available" : HasProduct ? "Product loaded" : "No product metadata";
        }

        public void TryAvatar()
        {
            if (!HasProduct || !TrialEnabled || !IsValidAvatarId(AvatarBlueprintId) || TargetPedestal == null)
            {
                SetStatus("Avatar trial is not available.");
                return;
            }
            VRCPlayerApi localPlayer = Networking.LocalPlayer;
            if (!Utilities.IsValid(localPlayer))
            {
                SetStatus("The local VRChat player is not ready.");
                return;
            }
            TargetPedestal.SwitchAvatar(AvatarBlueprintId);
            TargetPedestal.SetAvatarUse(localPlayer);
            TrialRequestCount++;
            SetStatus("Avatar trial requested.");
        }

        private bool IsValidAvatarId(string value)
        {
            return !string.IsNullOrEmpty(value) && value.Length == 41 && value.StartsWith("avtr_");
        }

        private void SetStatus(string message)
        {
            if (StatusText != null) StatusText.text = message;
            Debug.Log("[RAC2 Product] " + message);
        }
    }
}