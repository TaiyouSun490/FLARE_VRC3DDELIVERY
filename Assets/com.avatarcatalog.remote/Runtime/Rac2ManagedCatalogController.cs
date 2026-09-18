using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDK3.Components;
using VRC.SDK3.Data;
using VRC.SDK3.Image;
using VRC.SDK3.StringLoading;
using VRC.SDKBase;
using VRC.Udon.Common.Interfaces;

namespace AvatarCatalog.Remote
{
    /// <summary>Polygon-loading style managed catalog backed by fixed VRCUrl banks.</summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public sealed class Rac2ManagedCatalogController : UdonSharpBehaviour
    {
        public const int MaximumItems = 64;
        public const int CardsPerPage = 12;
        public const int LaneCount = 2;

        [Header("Fixed endpoints authored in Unity")]
        public VRCUrl ManifestUrl;
        [Tooltip("Flattened as page * 2 + lane.")]
        public VRCUrl[] AtlasUrls;
        [Tooltip("Optional RAC2 preview URLs flattened as slot * 2 + lane.")]
        public VRCUrl[] Rac2SlotUrls;

        [Header("Catalog targets")]
        public VRCAvatarPedestal TargetPedestal;
        public Rac2RuntimeLoader PreviewLoader;
        public bool LoadRac2PreviewOnCardPress = true;
        public bool WearAvatarOnCardPress;

        [Header("Page UI (12 cards)")]
        public GameObject[] CardRoots;
        public RawImage[] CardImages;
        public Text[] CardNameTexts;
        public Text[] CardCreatorTexts;
        public Image[] CardSelectionFrames;
        public Text PageText;
        public Text StatusText;

        [Header("Selected item UI")]
        public Text SelectedNameText;
        public Text SelectedCreatorText;
        public Text SelectedProductUrlText;
        public GameObject TryAvatarButtonRoot;

        [Header("Runtime diagnostics")]
        public int Status;
        public string LastError = "";
        public string LoadedRevision = "";
        public int LoadedReleaseLane;
        public int LoadedItemCount;
        public int LoadedPageCount;
        public int CurrentPage;
        public int SelectedItem = -1;
        public int SelectedSlot = -1;
        public string SelectedAvatarId = "";
        public int ManifestLoadCount;
        public int AtlasLoadCount;
        public int SelectionCount;

        private string[] _names = new string[0];
        private string[] _creators = new string[0];
        private string[] _productUrls = new string[0];
        private string[] _avatarIds = new string[0];
        private int[] _slots = new int[0];
        private bool[] _trials = new bool[0];
        private VRCImageDownloader _imageDownloader;
        private TextureInfo _textureInfo;
        private IVRCImageDownload _activeAtlas;
        private IUdonEventReceiver _receiver;
        private bool _manifestLoading;
        private bool _atlasLoading;
        private string _parseError = "Invalid managed catalog manifest.";

        private void Start()
        {
            _receiver = (IUdonEventReceiver)this;
            _imageDownloader = new VRCImageDownloader();
            _textureInfo = new TextureInfo();
            _textureInfo.GenerateMipMaps = false;
            _textureInfo.FilterMode = FilterMode.Bilinear;
            _textureInfo.WrapModeU = TextureWrapMode.Clamp;
            _textureInfo.WrapModeV = TextureWrapMode.Clamp;
            ClearCatalog();
            ReloadManifest();
        }

        public void ReloadManifest()
        {
            if (_manifestLoading) return;
            if (VRCUrl.IsNullOrEmpty(ManifestUrl))
            {
                ReportError("Managed catalog ManifestUrl is not configured.");
                return;
            }
            _manifestLoading = true;
            Status = 1;
            SetStatus("Loading catalog...");
            VRCStringDownloader.LoadUrl(ManifestUrl, _receiver);
        }

        public override void OnStringLoadSuccess(IVRCStringDownload result)
        {
            _manifestLoading = false;
            if (!ParseManifest(result.Result))
            {
                ReportError(_parseError);
                return;
            }
            ManifestLoadCount++;
            Status = 2;
            LastError = "";
            CurrentPage = 0;
            SelectedItem = -1;
            SelectedSlot = -1;
            SelectedAvatarId = "";
            RefreshPage();
            SetStatus(LoadedItemCount == 0 ? "Catalog is empty." : "Select an avatar.");
        }

        public override void OnStringLoadError(IVRCStringDownload result)
        {
            _manifestLoading = false;
            ReportError("Catalog download failed: " + result.Error + " (" + result.ErrorCode + ")");
        }

        public override void OnImageLoadSuccess(IVRCImageDownload result)
        {
            _atlasLoading = false;
            if (_activeAtlas != null && _activeAtlas != result) _activeAtlas.Dispose();
            _activeAtlas = result;
            Texture2D atlas = result.Result;
            for (int cell = 0; cell < CardsPerPage; cell++)
            {
                if (CardImages == null || cell >= CardImages.Length || CardImages[cell] == null) continue;
                CardImages[cell].texture = atlas;
                int row = cell / 4;
                int column = cell - row * 4;
                CardImages[cell].uvRect = new Rect(column * 0.25f, 1f - (row + 1) / 3f, 0.25f, 1f / 3f);
            }
            AtlasLoadCount++;
            SetStatus(SelectedItem >= 0 ? "Avatar selected." : "Select an avatar.");
        }

        public override void OnImageLoadError(IVRCImageDownload result)
        {
            _atlasLoading = false;
            SetStatus("Catalog loaded; thumbnail atlas failed: " + result.ErrorMessage);
        }

        public void NextPage()
        {
            if (_manifestLoading || _atlasLoading || LoadedPageCount <= 1) return;
            CurrentPage++;
            if (CurrentPage >= LoadedPageCount) CurrentPage = 0;
            RefreshPage();
        }

        public void PreviousPage()
        {
            if (_manifestLoading || _atlasLoading || LoadedPageCount <= 1) return;
            CurrentPage--;
            if (CurrentPage < 0) CurrentPage = LoadedPageCount - 1;
            RefreshPage();
        }

        public void SelectCard0() { SelectCell(0); }
        public void SelectCard1() { SelectCell(1); }
        public void SelectCard2() { SelectCell(2); }
        public void SelectCard3() { SelectCell(3); }
        public void SelectCard4() { SelectCell(4); }
        public void SelectCard5() { SelectCell(5); }
        public void SelectCard6() { SelectCell(6); }
        public void SelectCard7() { SelectCell(7); }
        public void SelectCard8() { SelectCell(8); }
        public void SelectCard9() { SelectCell(9); }
        public void SelectCard10() { SelectCell(10); }
        public void SelectCard11() { SelectCell(11); }

        public void TrySelectedAvatar()
        {
            if (!ValidSelectedTrial())
            {
                SetStatus("Avatar trial is unavailable.");
                return;
            }
            VRCPlayerApi player = Networking.LocalPlayer;
            if (!Utilities.IsValid(player))
            {
                SetStatus("The local VRChat player is not ready.");
                return;
            }
            TargetPedestal.SwitchAvatar(SelectedAvatarId);
            TargetPedestal.SetAvatarUse(player);
            SetStatus("Avatar switch requested.");
        }

        public void OnRac2LoadStarted() { SetStatus("Loading 3D preview..."); }
        public void OnRac2LoadSucceeded() { SetStatus("3D preview ready."); }
        public void OnRac2LoadFailed()
        {
            SetStatus(PreviewLoader == null ? "3D preview failed." : "3D preview failed: " + PreviewLoader.LastError);
        }
        public void OnRac2Cleared() { }

        private bool ParseManifest(string json)
        {
            _parseError = "Invalid managed catalog manifest.";
            if (string.IsNullOrEmpty(json) || json.Length > 131072)
                return Fail("Catalog manifest is empty or exceeds 128 KB.");
            if (!VRCJson.TryDeserializeFromJson(json, out DataToken rootToken) || rootToken.TokenType != TokenType.DataDictionary)
                return Fail("Catalog manifest is not valid JSON.");
            DataDictionary root = rootToken.DataDictionary;
            if (!GetInt(root, "schemaVersion", out int schema) || schema != 1 ||
                !GetString(root, "revision", out string revision) || string.IsNullOrEmpty(revision) || revision.Length > 80 ||
                !GetInt(root, "releaseLane", out int lane) || lane < 0 || lane >= LaneCount ||
                !GetInt(root, "pageSize", out int pageSize) || pageSize != CardsPerPage ||
                !GetInt(root, "pageCount", out int pageCount) || pageCount < 0 || pageCount > 6 ||
                !GetInt(root, "slotCapacity", out int capacity) || capacity < 1 || capacity > MaximumItems ||
                !root.TryGetValue("items", TokenType.DataList, out DataToken itemsToken))
                return Fail("Catalog manifest header is unsupported.");
            DataList items = itemsToken.DataList;
            if (items == null || items.Count > MaximumItems || pageCount != (items.Count + CardsPerPage - 1) / CardsPerPage)
                return Fail("Catalog manifest item or page count is invalid.");

            int count = items.Count;
            string[] names = new string[count];
            string[] creators = new string[count];
            string[] productUrls = new string[count];
            string[] avatarIds = new string[count];
            int[] slots = new int[count];
            bool[] trials = new bool[count];
            bool[] usedSlots = new bool[capacity];
            for (int index = 0; index < count; index++)
            {
                DataToken itemToken = items[index];
                if (itemToken.TokenType != TokenType.DataDictionary)
                    return Fail("Catalog item is not an object.");
                DataDictionary item = itemToken.DataDictionary;
                if (!GetInt(item, "slot", out int slot) || slot < 0 || slot >= capacity || usedSlots[slot] ||
                    !GetString(item, "name", out string name) || string.IsNullOrEmpty(name) || name.Length > 128 ||
                    !GetString(item, "creator", out string creator) || string.IsNullOrEmpty(creator) || creator.Length > 128 ||
                    !GetString(item, "productUrl", out string productUrl) ||
                    !GetString(item, "avatarId", out string avatarId) || !ValidAvatarId(avatarId) ||
                    !GetBool(item, "trial", out bool trial) ||
                    !GetInt(item, "page", out int page) || page != index / CardsPerPage ||
                    !GetInt(item, "cell", out int cell) || cell != index % CardsPerPage ||
                    productUrl.Length > 1024 || productUrl.Length > 0 && !productUrl.StartsWith("https://"))
                    return Fail("Catalog item metadata is invalid or non-canonical.");
                usedSlots[slot] = true;
                names[index] = name;
                creators[index] = creator;
                productUrls[index] = productUrl;
                avatarIds[index] = avatarId;
                slots[index] = slot;
                trials[index] = trial;
            }
            if (pageCount > 0 && (AtlasUrls == null || AtlasUrls.Length < pageCount * LaneCount))
                return Fail("Catalog atlas VRCUrl bank is incomplete.");
            if (LoadRac2PreviewOnCardPress && count > 0 &&
                (Rac2SlotUrls == null || Rac2SlotUrls.Length < capacity * LaneCount))
                return Fail("Catalog RAC2 VRCUrl bank is incomplete.");

            LoadedRevision = revision;
            LoadedReleaseLane = lane;
            LoadedItemCount = count;
            LoadedPageCount = pageCount;
            _names = names;
            _creators = creators;
            _productUrls = productUrls;
            _avatarIds = avatarIds;
            _slots = slots;
            _trials = trials;
            return true;
        }

        private void RefreshPage()
        {
            int first = CurrentPage * CardsPerPage;
            for (int cell = 0; cell < CardsPerPage; cell++)
            {
                int index = first + cell;
                bool visible = index < LoadedItemCount;
                if (CardRoots != null && cell < CardRoots.Length && CardRoots[cell] != null)
                    CardRoots[cell].SetActive(visible);
                if (!visible) continue;
                if (CardNameTexts != null && cell < CardNameTexts.Length && CardNameTexts[cell] != null)
                    CardNameTexts[cell].text = _names[index];
                if (CardCreatorTexts != null && cell < CardCreatorTexts.Length && CardCreatorTexts[cell] != null)
                    CardCreatorTexts[cell].text = _creators[index];
                if (CardSelectionFrames != null && cell < CardSelectionFrames.Length && CardSelectionFrames[cell] != null)
                    CardSelectionFrames[cell].color = index == SelectedItem ? new Color(.1f, .85f, 1f, 1f) : new Color(.12f, .16f, .22f, 1f);
            }
            if (PageText != null) PageText.text = LoadedPageCount == 0 ? "0 / 0" : (CurrentPage + 1) + " / " + LoadedPageCount;
            LoadAtlas();
        }

        private void LoadAtlas()
        {
            if (LoadedPageCount == 0 || AtlasUrls == null || _imageDownloader == null) return;
            int index = CurrentPage * LaneCount + LoadedReleaseLane;
            if (index < 0 || index >= AtlasUrls.Length || VRCUrl.IsNullOrEmpty(AtlasUrls[index]))
            {
                SetStatus("Thumbnail atlas URL is not configured.");
                return;
            }
            _atlasLoading = true;
            SetStatus("Loading catalog page...");
            _imageDownloader.DownloadImage(AtlasUrls[index], null, _receiver, _textureInfo);
        }

        private void SelectCell(int cell)
        {
            if (_manifestLoading || cell < 0 || cell >= CardsPerPage) return;
            int index = CurrentPage * CardsPerPage + cell;
            if (index < 0 || index >= LoadedItemCount) return;
            SelectedItem = index;
            SelectedSlot = _slots[index];
            SelectedAvatarId = _avatarIds[index];
            SelectionCount++;
            if (SelectedNameText != null) SelectedNameText.text = _names[index];
            if (SelectedCreatorText != null) SelectedCreatorText.text = _creators[index];
            if (SelectedProductUrlText != null) SelectedProductUrlText.text = _productUrls[index];
            if (TryAvatarButtonRoot != null) TryAvatarButtonRoot.SetActive(ValidSelectedTrial());
            if (TargetPedestal != null) TargetPedestal.SwitchAvatar(SelectedAvatarId);
            if (LoadRac2PreviewOnCardPress && PreviewLoader != null && Rac2SlotUrls != null)
            {
                int urlIndex = SelectedSlot * LaneCount + LoadedReleaseLane;
                if (urlIndex >= 0 && urlIndex < Rac2SlotUrls.Length && !VRCUrl.IsNullOrEmpty(Rac2SlotUrls[urlIndex]))
                {
                    PreviewLoader.RuntimeUrl = Rac2SlotUrls[urlIndex];
                    PreviewLoader.LoadRuntimeUrl();
                }
                else SetStatus("The selected RAC2 slot URL is not configured.");
            }
            else SetStatus("Avatar selected.");
            RefreshPageSelection();
            if (WearAvatarOnCardPress) TrySelectedAvatar();
        }

        private void RefreshPageSelection()
        {
            int first = CurrentPage * CardsPerPage;
            for (int cell = 0; cell < CardsPerPage; cell++)
            {
                if (CardSelectionFrames == null || cell >= CardSelectionFrames.Length || CardSelectionFrames[cell] == null) continue;
                CardSelectionFrames[cell].color = first + cell == SelectedItem ? new Color(.1f, .85f, 1f, 1f) : new Color(.12f, .16f, .22f, 1f);
            }
        }

        private bool ValidSelectedTrial()
        {
            return SelectedItem >= 0 && SelectedItem < LoadedItemCount && _trials[SelectedItem] &&
                   ValidAvatarId(SelectedAvatarId) && TargetPedestal != null;
        }

        private bool GetString(DataDictionary source, string key, out string value)
        {
            value = "";
            if (!source.TryGetValue(key, TokenType.String, out DataToken token)) return false;
            value = token.String;
            return value != null;
        }

        private bool GetInt(DataDictionary source, string key, out int value)
        {
            value = 0;
            if (!source.TryGetValue(key, out DataToken token) || !token.IsNumber) return false;
            double number = token.Number;
            if (number < 0d || number > 2147483647d) return false;
            value = (int)number;
            return number == value;
        }

        private bool GetBool(DataDictionary source, string key, out bool value)
        {
            value = false;
            if (!source.TryGetValue(key, TokenType.Boolean, out DataToken token)) return false;
            value = token.Boolean;
            return true;
        }

        private bool ValidAvatarId(string value)
        {
            return !string.IsNullOrEmpty(value) && value.Length == 41 && value.StartsWith("avtr_");
        }

        private void ClearCatalog()
        {
            LoadedRevision = "";
            LoadedReleaseLane = 0;
            LoadedItemCount = 0;
            LoadedPageCount = 0;
            CurrentPage = 0;
            SelectedItem = -1;
            SelectedSlot = -1;
            SelectedAvatarId = "";
            if (TryAvatarButtonRoot != null) TryAvatarButtonRoot.SetActive(false);
            for (int cell = 0; cell < CardsPerPage; cell++)
                if (CardRoots != null && cell < CardRoots.Length && CardRoots[cell] != null) CardRoots[cell].SetActive(false);
        }

        private bool Fail(string message)
        {
            _parseError = message;
            return false;
        }

        private void ReportError(string message)
        {
            Status = 3;
            LastError = message;
            ClearCatalog();
            SetStatus("Error: " + message);
            Debug.LogError("[RAC2 Managed Catalog] " + message);
        }

        private void SetStatus(string message)
        {
            if (StatusText != null) StatusText.text = message;
        }

        private void OnDestroy()
        {
            if (_activeAtlas != null) _activeAtlas.Dispose();
            if (_imageDownloader != null) _imageDownloader.Dispose();
        }
    }
}