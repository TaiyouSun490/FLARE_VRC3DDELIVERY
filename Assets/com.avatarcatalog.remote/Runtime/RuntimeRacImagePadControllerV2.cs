using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDK3.Components;
using VRC.SDKBase;
using VRC.Udon;

namespace AvatarCatalog.Remote
{
    /// <summary>Dual-mode local ImagePad: basic direct GLB preview or full RAC2.</summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public sealed class RuntimeRacImagePadControllerV2 : UdonSharpBehaviour
    {
        private const int ModeNone = 0;
        private const int ModeGlb = 1;
        private const int ModeRac2 = 2;

        [Header("Required references")]
        public VRCUrlInputField UrlInput;
        public UdonBehaviour GlbLoaderBehaviour;
        public UdonBehaviour Rac2LoaderBehaviour;
        public Text StatusText;

        [Header("Buttons")]
        public Button GlbButton;
        public Button Rac2Button;
        public Button SampleGlbButton;
        public Button SampleRac2Button;
        public Button RetryButton;
        public Button ClearButton;
        public Button AdaptiveLoadingButton;
        public Text AdaptiveLoadingText;

        [Header("Samples")]
        public VRCUrl SampleGlbUrl;
        public VRCUrl SampleRac2Url;

        [Header("Diagnostics")]
        public string LastRequestedAddress = "";
        public int LastRequestedMode;
        public int LoadRequestCount;
        public bool Rac2AdaptiveLoadingEnabled = true;

        private VRCUrl _lastUrl;
        private bool _hasLastUrl;
        private bool _busy;
        private int _activeMode;
        private float _nextProgressRefresh;

        private void Update()
        {
            if (!_busy || _activeMode != ModeRac2 || Rac2LoaderBehaviour == null ||
                Time.unscaledTime < _nextProgressRefresh) return;
            _nextProgressRefresh = Time.unscaledTime + 0.1f;
            string progress = (string)Rac2LoaderBehaviour.GetProgramVariable("StatusMessage");
            if (!string.IsNullOrEmpty(progress))
                SetStatus(progress, new Color(1f, .78f, .28f, 1f));
        }

        private void Start()
        {
            RefreshAdaptiveLoadingLabel();
            SetReady("Paste a public HTTPS URL, then choose DIRECT GLB or RAC2.");
        }

        public void LoadGlbFromInput() { Begin(InputUrl(), ModeGlb); }
        public void LoadRac2FromInput() { Begin(InputUrl(), ModeRac2); }
        public void LoadSampleGlb() { Begin(SampleGlbUrl, ModeGlb); }
        public void LoadSampleRac2() { Begin(SampleRac2Url, ModeRac2); }

        public void ToggleRac2AdaptiveLoading()
        {
            if (_busy)
            {
                SetLoading("Adaptive loading can be changed after the current load.");
                return;
            }
            Rac2AdaptiveLoadingEnabled = !Rac2AdaptiveLoadingEnabled;
            RefreshAdaptiveLoadingLabel();
            SetReady(Rac2AdaptiveLoadingEnabled
                ? "Adaptive RAC2 loading enabled. Frame budget follows local FPS."
                : "Adaptive RAC2 loading disabled. Inspector debug budget will be used.");
        }

        public void RetryLast()
        {
            if (!_hasLastUrl || LastRequestedMode == ModeNone) { SetError("No previous request is available."); return; }
            Begin(_lastUrl, LastRequestedMode);
        }

        public void ClearDisplay()
        {
            if (_busy) { SetLoading("Wait for the current download to finish before clearing."); return; }
            if (GlbLoaderBehaviour != null) GlbLoaderBehaviour.SendCustomEvent("Clear");
            if (Rac2LoaderBehaviour != null) Rac2LoaderBehaviour.SendCustomEvent("Clear");
            _activeMode = ModeNone;
            SetReady("Display cleared.");
        }

        public void OnGlbLoadStarted() { if (_activeMode == ModeGlb) SetLoading("Downloading and parsing basic GLB..."); }
        public void OnGlbLoadSucceeded()
        {
            if (_activeMode != ModeGlb || GlbLoaderBehaviour == null) return;
            _busy = false;
            int vertices = (int)GlbLoaderBehaviour.GetProgramVariable("LoadedVertexCount");
            int indices = (int)GlbLoaderBehaviour.GetProgramVariable("LoadedIndexCount");
            bool normals = (bool)GlbLoaderBehaviour.GetProgramVariable("LoadedHasNormals");
            bool uv = (bool)GlbLoaderBehaviour.GetProgramVariable("LoadedHasUv0");
            SetReady("READY - direct GLB " + vertices + " vertices / " + indices + " indices" +
                     (normals ? " + normals" : "") + (uv ? " + UV0" : "") + " / no textures");
        }

        public void OnGlbLoadFailed()
        {
            if (_activeMode != ModeGlb) return;
            _busy = false;
            string message = GlbLoaderBehaviour == null ? "GLB load failed." : (string)GlbLoaderBehaviour.GetProgramVariable("LastError");
            SetError(WithTrustHint(message == null || message.Length == 0 ? "GLB load failed." : message));
        }
        public void OnGlbCleared() { }

        public void OnRac2LoadStarted() { if (_activeMode == ModeRac2) SetLoading("Downloading and validating RAC2 response..."); }
        public void OnRac2LoadSucceeded()
        {
            if (_activeMode != ModeRac2 || Rac2LoaderBehaviour == null) return;
            _busy = false;
            bool texture = (bool)Rac2LoaderBehaviour.GetProgramVariable("LoadedHasTexture");
            bool normal = (bool)Rac2LoaderBehaviour.GetProgramVariable("LoadedHasNormalMap");
            int profile = (int)Rac2LoaderBehaviour.GetProgramVariable("LoadedMaterialProfile");
            int vertices = (int)Rac2LoaderBehaviour.GetProgramVariable("LoadedVertexCount");
            int indices = (int)Rac2LoaderBehaviour.GetProgramVariable("LoadedIndexCount");
            SetReady("READY - RAC2 " + vertices + " vertices / " + indices + " indices" +
                     (profile == 1 ? " / lilToon" : "") + (texture ? " + Main" : "") + (normal ? " + Normal" : ""));
        }

        public void OnRac2LoadFailed()
        {
            if (_activeMode != ModeRac2) return;
            _busy = false;
            string message = Rac2LoaderBehaviour == null ? "RAC2 load failed." : (string)Rac2LoaderBehaviour.GetProgramVariable("LastError");
            SetError(WithTrustHint(message == null || message.Length == 0 ? "RAC2 load failed." : message));
        }
        public void OnRac2Cleared() { }

        private VRCUrl InputUrl() { return UrlInput == null ? null : UrlInput.GetUrl(); }

        private void Begin(VRCUrl url, int mode)
        {
            if (_busy) { SetLoading("A load is already active. Please wait."); return; }
            if (VRCUrl.IsNullOrEmpty(url)) { SetError("Enter an HTTPS URL first."); return; }
            string address = url.Get();
            if (address == null || !address.StartsWith("https://")) { SetError("Only lowercase HTTPS URLs are accepted."); return; }
            UdonBehaviour loader = mode == ModeGlb ? GlbLoaderBehaviour : Rac2LoaderBehaviour;
            if (loader == null) { SetError(mode == ModeGlb ? "GLB loader is not connected." : "RAC2 loader is not connected."); return; }

            if (mode == ModeGlb && Rac2LoaderBehaviour != null) Rac2LoaderBehaviour.SendCustomEvent("Clear");
            if (mode == ModeRac2 && GlbLoaderBehaviour != null) GlbLoaderBehaviour.SendCustomEvent("Clear");
            if (mode == ModeRac2)
                loader.SetProgramVariable("AdaptiveLoadingEnabled", Rac2AdaptiveLoadingEnabled);
            _lastUrl = url;
            _hasLastUrl = true;
            LastRequestedAddress = address;
            LastRequestedMode = mode;
            LoadRequestCount++;
            _activeMode = mode;
            _busy = true;
            loader.SetProgramVariable("RuntimeUrl", url);
            SetLoading(mode == ModeGlb ? "Starting direct GLB request..." : "Starting RAC2 request...");
            loader.SendCustomEvent("LoadRuntimeUrl");
        }

        private void RefreshAdaptiveLoadingLabel()
        {
            if (AdaptiveLoadingText != null)
                AdaptiveLoadingText.text = Rac2AdaptiveLoadingEnabled
                    ? "ADAPTIVE LOAD: ON"
                    : "ADAPTIVE LOAD: OFF";
        }

        private string WithTrustHint(string value)
        {
            return value + " For non-trusted domains, enable Allow Untrusted URLs in VRChat.";
        }

        private void SetLoading(string message) { SetStatus(message, new Color(1f, .78f, .28f, 1f)); SetButtons(false, false, false); }
        private void SetReady(string message) { SetStatus(message, new Color(.42f, .95f, .76f, 1f)); SetButtons(true, _hasLastUrl, true); }
        private void SetError(string message) { SetStatus("ERROR - " + message, new Color(1f, .42f, .48f, 1f)); SetButtons(true, _hasLastUrl, true); }
        private void SetStatus(string message, Color color) { if (StatusText == null) return; StatusText.text = message; StatusText.color = color; }

        private void SetButtons(bool canLoad, bool canRetry, bool canClear)
        {
            if (GlbButton != null) GlbButton.interactable = canLoad;
            if (Rac2Button != null) Rac2Button.interactable = canLoad;
            if (SampleGlbButton != null) SampleGlbButton.interactable = canLoad;
            if (SampleRac2Button != null) SampleRac2Button.interactable = canLoad;
            if (RetryButton != null) RetryButton.interactable = canRetry;
            if (ClearButton != null) ClearButton.interactable = canClear;
            if (AdaptiveLoadingButton != null) AdaptiveLoadingButton.interactable = canLoad;
        }
    }
}

