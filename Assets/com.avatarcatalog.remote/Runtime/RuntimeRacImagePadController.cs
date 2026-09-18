using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDK3.Components;
using VRC.SDKBase;
using VRC.Udon;

namespace AvatarCatalog.Remote
{
    /// <summary>Local 3D Pad for raw RAC2 URLs and RAC2 API responses.</summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public sealed class RuntimeRacImagePadController : UdonSharpBehaviour
    {
        [Header("Required references")]
        public VRCUrlInputField UrlInput;
        public UdonBehaviour Rac2LoaderBehaviour;
        public Text StatusText;

        [Header("Buttons")]
        public Button Rac2Button;
        public Button SampleRac2Button;
        public Button RetryButton;
        public Button ClearButton;

        [Header("Sample")]
        public VRCUrl SampleRac2Url;

        [Header("Diagnostics")]
        public string LastRequestedAddress = "";
        public int LoadRequestCount;

        private VRCUrl _lastUrl;
        private bool _hasLastUrl;
        private bool _busy;

        private void Start() { SetReady("Paste a RAC2 URL or RAC2 API URL, then press LOAD RAC2."); }
        public void LoadRac2FromInput() { Begin(InputUrl()); }
        public void LoadSampleRac2() { Begin(SampleRac2Url); }

        public void RetryLast()
        {
            if (!_hasLastUrl) { SetError("No previous request is available."); return; }
            Begin(_lastUrl);
        }

        public void ClearDisplay()
        {
            _busy = false;
            if (Rac2LoaderBehaviour != null) Rac2LoaderBehaviour.SendCustomEvent("Clear");
            SetReady("Display cleared.");
        }

        public void OnRac2LoadStarted() { SetLoading("Downloading and validating RAC2 response..."); }

        public void OnRac2LoadSucceeded()
        {
            if (Rac2LoaderBehaviour == null) return;
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
            _busy = false;
            string message = Rac2LoaderBehaviour == null ? "RAC2 load failed." : (string)Rac2LoaderBehaviour.GetProgramVariable("LastError");
            SetError((message == null || message.Length == 0 ? "RAC2 load failed." : message) + " Check Allow Untrusted URLs for non-trusted domains.");
        }

        public void OnRac2Cleared() { }

        private VRCUrl InputUrl() { return UrlInput == null ? null : UrlInput.GetUrl(); }

        private void Begin(VRCUrl url)
        {
            if (_busy) { SetLoading("A load is already active. Wait or press CLEAR."); return; }
            if (VRCUrl.IsNullOrEmpty(url)) { SetError("Enter an HTTPS URL first."); return; }
            string address = url.Get();
            if (address == null || !address.StartsWith("https://")) { SetError("Only lowercase HTTPS URLs are accepted."); return; }
            if (Rac2LoaderBehaviour == null) { SetError("RAC2 loader is not connected."); return; }

            _lastUrl = url;
            _hasLastUrl = true;
            LastRequestedAddress = address;
            LoadRequestCount++;
            _busy = true;
            Rac2LoaderBehaviour.SetProgramVariable("RuntimeUrl", url);
            SetLoading("Starting RAC2 request...");
            Rac2LoaderBehaviour.SendCustomEvent("LoadRuntimeUrl");
        }

        private void SetLoading(string message) { SetStatus(message, new Color(1f, 0.78f, 0.28f, 1f)); SetButtons(false, false, true); }
        private void SetReady(string message) { SetStatus(message, new Color(0.42f, 0.95f, 0.76f, 1f)); SetButtons(true, _hasLastUrl, true); }
        private void SetError(string message) { SetStatus("ERROR - " + message, new Color(1f, 0.42f, 0.48f, 1f)); SetButtons(true, _hasLastUrl, true); }
        private void SetStatus(string message, Color color) { if (StatusText == null) return; StatusText.text = message; StatusText.color = color; }

        private void SetButtons(bool canLoad, bool canRetry, bool canClear)
        {
            if (Rac2Button != null) Rac2Button.interactable = canLoad;
            if (SampleRac2Button != null) SampleRac2Button.interactable = canLoad;
            if (RetryButton != null) RetryButton.interactable = canRetry;
            if (ClearButton != null) ClearButton.interactable = canClear;
        }
    }
}

