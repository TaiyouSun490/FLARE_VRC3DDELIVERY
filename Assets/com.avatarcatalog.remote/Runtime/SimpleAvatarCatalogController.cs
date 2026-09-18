using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDK3.Components;
using VRC.SDKBase;

namespace AvatarCatalog.Remote
{
    /// <summary>
    /// Minimal three-entry avatar catalog. Each icon selects the associated
    /// pedestal avatar and requests that the local player wears it.
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public sealed class SimpleAvatarCatalogController : UdonSharpBehaviour
    {
        private const int EntryCount = 3;

        [Header("Avatar catalog")]
        public VRCAvatarPedestal TargetPedestal;
        public string[] AvatarBlueprintIds = new string[EntryCount];
        public string[] AvatarNames = new string[EntryCount];
        public Sprite[] AvatarIcons = new Sprite[EntryCount];
        public bool WearAvatarWhenIconIsPressed = true;

        [Header("UI")]
        public Image[] SelectionFrames = new Image[EntryCount];
        public Image[] AvatarIconImages = new Image[EntryCount];
        public Text[] AvatarPlaceholderTexts = new Text[EntryCount];
        public Text[] AvatarNameLabels = new Text[EntryCount];
        public Text StatusText;

        [Header("Runtime diagnostics")]
        public int LastSelectedIndex = -1;
        public string LastSelectedBlueprintId = "";
        public int UseRequestCount;

        private readonly Color _selectedColor = new Color(0.12f, 0.82f, 1f, 1f);
        private readonly Color _idleColor = new Color(0.13f, 0.16f, 0.22f, 1f);

        private void Start()
        {
            RefreshCatalog();
        }

        /// <summary>
        /// Re-applies names and optional sprites after catalog data changes.
        /// This is the connection point for a future downloaded manifest parser.
        /// </summary>
        public void RefreshCatalog()
        {
            for (int i = 0; i < EntryCount; i++)
            {
                string displayName = GetName(i);

                if (AvatarNameLabels != null &&
                    i < AvatarNameLabels.Length &&
                    AvatarNameLabels[i] != null)
                {
                    AvatarNameLabels[i].text = displayName;
                }

                bool hasIcon = AvatarIcons != null &&
                    i < AvatarIcons.Length &&
                    AvatarIcons[i] != null;

                if (AvatarIconImages != null &&
                    i < AvatarIconImages.Length &&
                    AvatarIconImages[i] != null)
                {
                    AvatarIconImages[i].sprite = hasIcon ? AvatarIcons[i] : null;
                    AvatarIconImages[i].enabled = hasIcon;
                }

                if (AvatarPlaceholderTexts != null &&
                    i < AvatarPlaceholderTexts.Length &&
                    AvatarPlaceholderTexts[i] != null)
                {
                    AvatarPlaceholderTexts[i].text = GetInitial(displayName);
                    AvatarPlaceholderTexts[i].enabled = !hasIcon;
                }

                SetFrameSelected(i, i == LastSelectedIndex);
            }

            if (LastSelectedIndex < 0)
            {
                SetStatus("Touch an avatar icon to switch.");
            }
        }

        public void ChooseAvatar0()
        {
            ChooseAvatar(0);
        }

        public void ChooseAvatar1()
        {
            ChooseAvatar(1);
        }

        public void ChooseAvatar2()
        {
            ChooseAvatar(2);
        }

        public void WearSelectedAvatar()
        {
            if (LastSelectedIndex < 0 || !IsValidEntry(LastSelectedIndex))
            {
                SetStatus("Select an available avatar first.");
                return;
            }

            VRCPlayerApi localPlayer = Networking.LocalPlayer;
            if (!Utilities.IsValid(localPlayer))
            {
                SetStatus("The local VRChat player is not ready.");
                return;
            }

            TargetPedestal.SetAvatarUse(localPlayer);
            UseRequestCount++;
            SetStatus("Switch requested: " + GetName(LastSelectedIndex));
        }

        private void ChooseAvatar(int index)
        {
            if (!IsValidEntry(index))
            {
                SetStatus("Avatar slot " + (index + 1) + " is not configured.");
                return;
            }

            string blueprintId = AvatarBlueprintIds[index];
            TargetPedestal.SwitchAvatar(blueprintId);
            LastSelectedIndex = index;
            LastSelectedBlueprintId = blueprintId;

            for (int i = 0; i < EntryCount; i++)
            {
                SetFrameSelected(i, i == index);
            }

            SetStatus("Selected: " + GetName(index));
            if (WearAvatarWhenIconIsPressed)
            {
                WearSelectedAvatar();
            }
        }

        private bool IsValidEntry(int index)
        {
            if (TargetPedestal == null ||
                AvatarBlueprintIds == null ||
                index < 0 ||
                index >= EntryCount ||
                index >= AvatarBlueprintIds.Length)
            {
                return false;
            }

            string id = AvatarBlueprintIds[index];
            return !string.IsNullOrEmpty(id) &&
                id.Length == 41 &&
                id.StartsWith("avtr_");
        }

        private string GetName(int index)
        {
            if (AvatarNames != null &&
                index >= 0 &&
                index < AvatarNames.Length &&
                !string.IsNullOrEmpty(AvatarNames[index]))
            {
                return AvatarNames[index];
            }

            return "Avatar " + (index + 1);
        }

        private string GetInitial(string displayName)
        {
            if (string.IsNullOrEmpty(displayName))
            {
                return "?";
            }

            return displayName.Substring(0, 1).ToUpper();
        }

        private void SetFrameSelected(int index, bool selected)
        {
            if (SelectionFrames == null ||
                index < 0 ||
                index >= SelectionFrames.Length ||
                SelectionFrames[index] == null)
            {
                return;
            }

            SelectionFrames[index].color = selected ? _selectedColor : _idleColor;
        }

        private void SetStatus(string message)
        {
            if (StatusText != null)
            {
                StatusText.text = message;
            }

            Debug.Log("[Simple Avatar Catalog] " + message);
        }
    }
}
