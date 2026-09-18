using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDK3.Components;
using VRC.SDK3.StringLoading;
using VRC.SDKBase;
using VRC.Udon.Common.Interfaces;

namespace AvatarCatalog.Remote
{
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public sealed class FlareGlbDownloader : UdonSharpBehaviour
    {
        public VRCUrl RuntimeUrl;
        public VRCUrl DemoUrl;
        public VRCUrlInputField UrlInput;
        public Text StatusText;
        public FlareGlbSceneLoader SceneLoader;
        public string State = "Idle";
        private bool _inFlight, _cancelled;
        private int _observed;
        public void LoadDemo() { RuntimeUrl = DemoUrl; LoadRuntimeUrl(); }
        public void LoadFromInput()
        {
            if (UrlInput != null) RuntimeUrl = UrlInput.GetUrl();
            LoadRuntimeUrl();
        }
        public void LoadRuntimeUrl()
        {
            if (_inFlight || SceneLoader == null || SceneLoader.Status == 1) return;
            if (VRCUrl.IsNullOrEmpty(RuntimeUrl)) { SetState("Download failed: URL is empty"); return; }
            SceneLoader.Clear(); _observed = 0;
            _inFlight = true; _cancelled = false;
            SetState("Download started");
            VRCStringDownloader.LoadUrl(RuntimeUrl, (IUdonEventReceiver)this);
        }
        public override void OnStringLoadSuccess(IVRCStringDownload result)
        {
            if (!_inFlight) return;
            _inFlight = false;
            if (_cancelled) { SetState("Idle"); return; }
            SetState("Download succeeded; parsing");
            SceneLoader.InputBytes = result.ResultBytes;
            SceneLoader.LoadInput(); _observed = -1;
        }
        public override void OnStringLoadError(IVRCStringDownload result)
        {
            if (!_inFlight) return;
            _inFlight = false;
            if (!_cancelled) SetState("Download failed: " + result.Error);
            else SetState("Idle");
        }
        public void Clear()
        {
            _cancelled = true;
            if (SceneLoader != null) SceneLoader.Clear();
            _observed = 0;
            SetState(_inFlight ? "Cleared; waiting for cancelled request" : "Idle");
        }
        private void Update()
        {
            if (SceneLoader == null || SceneLoader.Status == _observed) return;
            _observed = SceneLoader.Status;
            if (_observed == 2) SetState("Parse succeeded; ready");
            if (_observed == 3) SetState("Parse failed: " + SceneLoader.LastError);
        }
        private void SetState(string state)
        { State = state; if (StatusText != null) StatusText.text = state; Debug.Log("[FLARE] " + state); }
    }
}
