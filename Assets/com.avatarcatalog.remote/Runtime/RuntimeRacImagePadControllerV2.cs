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

        [Header("Language / 言語")]
        [Tooltip("On: Japanese. Off: English. Local display only; not synchronized.")]
        public bool UseJapanese = true;

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
                SetStatus(LocalizeProgress(progress), new Color(1f, .78f, .28f, 1f));
        }

        private void Start()
        {
            RefreshLabels();
            SetReady(L("Paste a public HTTPS URL, then choose DIRECT GLB or RAC2."));
        }

        public void LoadGlbFromInput() { Begin(InputUrl(), ModeGlb); }
        public void LoadRac2FromInput() { Begin(InputUrl(), ModeRac2); }
        public void LoadSampleGlb() { Begin(SampleGlbUrl, ModeGlb); }
        public void LoadSampleRac2() { Begin(SampleRac2Url, ModeRac2); }

        public void ToggleRac2AdaptiveLoading()
        {
            if (_busy)
            {
                SetLoading(L("Adaptive loading can be changed after the current load."));
                return;
            }
            Rac2AdaptiveLoadingEnabled = !Rac2AdaptiveLoadingEnabled;
            RefreshAdaptiveLoadingLabel();
            SetReady(Rac2AdaptiveLoadingEnabled
                ? L("Adaptive RAC2 loading enabled. Frame budget follows local FPS.")
                : L("Adaptive RAC2 loading disabled. Inspector debug budget will be used."));
        }

        public void RetryLast()
        {
            if (!_hasLastUrl || LastRequestedMode == ModeNone) { SetError(L("No previous request is available.")); return; }
            Begin(_lastUrl, LastRequestedMode);
        }

        public void ClearDisplay()
        {
            if (_busy) { SetLoading(L("Wait for the current download to finish before clearing.")); return; }
            if (GlbLoaderBehaviour != null) GlbLoaderBehaviour.SendCustomEvent("Clear");
            if (Rac2LoaderBehaviour != null) Rac2LoaderBehaviour.SendCustomEvent("Clear");
            _activeMode = ModeNone;
            SetReady(L("Display cleared."));
        }

        public void OnGlbLoadStarted() { if (_activeMode == ModeGlb) SetLoading(L("Downloading and parsing basic GLB...")); }
        public void OnGlbLoadSucceeded()
        {
            if (_activeMode != ModeGlb || GlbLoaderBehaviour == null) return;
            _busy = false;
            int vertices = (int)GlbLoaderBehaviour.GetProgramVariable("LoadedVertexCount");
            int indices = (int)GlbLoaderBehaviour.GetProgramVariable("LoadedIndexCount");
            bool normals = (bool)GlbLoaderBehaviour.GetProgramVariable("LoadedHasNormals");
            bool uv = (bool)GlbLoaderBehaviour.GetProgramVariable("LoadedHasUv0");
            SetReady(L("READY - direct GLB ") + vertices + L(" vertices / ") + indices + L(" indices") +
                     (normals ? L(" + normals") : "") + (uv ? " + UV0" : "") + L(" / no textures"));
        }

        public void OnGlbLoadFailed()
        {
            if (_activeMode != ModeGlb) return;
            _busy = false;
            string message = GlbLoaderBehaviour == null ? L("GLB load failed.") : (string)GlbLoaderBehaviour.GetProgramVariable("LastError");
            SetError(WithTrustHint(FriendlyLoadError(message, false)));
        }
        public void OnGlbCleared() { }

        public void OnRac2LoadStarted() { if (_activeMode == ModeRac2) SetLoading(L("Downloading and validating RAC2 response...")); }
        public void OnRac2LoadSucceeded()
        {
            if (_activeMode != ModeRac2 || Rac2LoaderBehaviour == null) return;
            _busy = false;
            bool texture = (bool)Rac2LoaderBehaviour.GetProgramVariable("LoadedHasTexture");
            bool normal = (bool)Rac2LoaderBehaviour.GetProgramVariable("LoadedHasNormalMap");
            int profile = (int)Rac2LoaderBehaviour.GetProgramVariable("LoadedMaterialProfile");
            int vertices = (int)Rac2LoaderBehaviour.GetProgramVariable("LoadedVertexCount");
            int indices = (int)Rac2LoaderBehaviour.GetProgramVariable("LoadedIndexCount");
            int triangles = indices / 3;
            int meshes = (int)Rac2LoaderBehaviour.GetProgramVariable("LoadedRenderNodeCount");
            int slots = (int)Rac2LoaderBehaviour.GetProgramVariable("LoadedMaterialCount");
            int emitters = (int)Rac2LoaderBehaviour.GetProgramVariable("LoadedParticleEmitterCount");
            int particles = (int)Rac2LoaderBehaviour.GetProgramVariable("LoadedParticleMaximum");
            if(meshes == 0 && vertices > 0) { meshes = 1; slots = 1; }
            if(emitters == 0 && particles > 0) emitters = 1;
            // Drawing subset only: never present this as the SDK's full avatar performance rank.
            int rank = DrawingRank(triangles,32000,70000,70000,70000);
            rank = Mathf.Max(rank,DrawingRank(slots,4,8,16,32));
            rank = Mathf.Max(rank,DrawingRank(meshes,4,8,16,24));
            rank = Mathf.Max(rank,DrawingRank(emitters,0,4,8,16));
            rank = Mathf.Max(rank,DrawingRank(particles,0,300,1000,2500));
            string badge = rank==0 ? "[*] Excellent" : rank==1 ? "[+] Good" : rank==2 ? "[=] Medium" : rank==3 ? "[!] Poor" : "[!!] Very Poor";
            SetReady(L("READY  ") + badge + L(" (PC draw estimate, not SDK rank)\n") + triangles + L(" tris / ") + slots + L(" slots / ") + meshes + L(" meshes\nTextures, shader cost and peak memory not rated."));
            if(StatusText != null) StatusText.color = rank>=4 ? new Color(1f,.35f,.4f) : rank==3 ? new Color(1f,.6f,.25f) : rank==2 ? new Color(1f,.85f,.3f) : new Color(.4f,1f,.65f);
        }

        private int DrawingRank(int value,int excellent,int good,int medium,int poor)
        { return value<=excellent ? 0 : value<=good ? 1 : value<=medium ? 2 : value<=poor ? 3 : 4; }

        public void OnRac2LoadFailed()
        {
            if (_activeMode != ModeRac2) return;
            _busy = false;
            string message = Rac2LoaderBehaviour == null ? L("RAC2 load failed.") : (string)Rac2LoaderBehaviour.GetProgramVariable("LastError");
            SetError(WithTrustHint(FriendlyLoadError(message, true)));
        }
        public void OnRac2Cleared() { }

        private VRCUrl InputUrl() { return UrlInput == null ? null : UrlInput.GetUrl(); }

        private void Begin(VRCUrl url, int mode)
        {
            if (_busy) { SetLoading(L("A load is already active. Please wait.")); return; }
            if (VRCUrl.IsNullOrEmpty(url)) { SetError(L("Enter an HTTPS URL first.")); return; }
            string address = url.Get();
            if (address == null || !address.StartsWith("https://")) { SetError(L("Only lowercase HTTPS URLs are accepted.")); return; }
            UdonBehaviour loader = mode == ModeGlb ? GlbLoaderBehaviour : Rac2LoaderBehaviour;
            if (loader == null) { SetError(mode == ModeGlb ? L("GLB loader is not connected.") : L("RAC2 loader is not connected.")); return; }

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
            SetLoading(mode == ModeGlb ? L("Starting direct GLB request...") : L("Starting RAC2 request..."));
            loader.SendCustomEvent("LoadRuntimeUrl");
        }

        private void RefreshAdaptiveLoadingLabel()
        {
            if (AdaptiveLoadingText != null)
                AdaptiveLoadingText.text = Rac2AdaptiveLoadingEnabled
                    ? L("ADAPTIVE LOAD: ON")
                    : L("ADAPTIVE LOAD: OFF");
        }

        private string WithTrustHint(string value)
        {
            return value + L(" For non-trusted domains, enable Allow Untrusted URLs in VRChat.");
        }

        private void SetLoading(string message) { SetStatus(message, new Color(1f, .78f, .28f, 1f)); SetButtons(false, false, false); }
        private void SetReady(string message) { SetStatus(message, new Color(.42f, .95f, .76f, 1f)); SetButtons(true, _hasLastUrl, true); }
        private void SetError(string message) { SetStatus(L("ERROR - ") + message, new Color(1f, .42f, .48f, 1f)); SetButtons(true, _hasLastUrl, true); }
        private void SetStatus(string message, Color color) { if (StatusText == null) return; StatusText.text = message; StatusText.color = color; }

private string L(string english)
        {
            if (!UseJapanese) return english;
            switch (english)
            {
                case "Paste a public HTTPS URL, then choose DIRECT GLB or RAC2.": return "公開HTTPS URLを入力し、GLBまたはRAC2を読み込んでください。";
                case "Adaptive loading can be changed after the current load.": return "自動負荷調整は読み込み完了後に変更できます。";
                case "Adaptive RAC2 loading enabled. Frame budget follows local FPS.": return "自動負荷調整を有効にしました。この端末のFPSに応じて読み込み負荷を調整します。";
                case "Adaptive RAC2 loading disabled. Inspector debug budget will be used.": return "自動負荷調整を無効にしました。Inspectorの固定処理量を使用します。";
                case "No previous request is available.": return "再試行できる読み込み履歴がありません。";
                case "Wait for the current download to finish before clearing.": return "読み込みが完了してから表示を消してください。";
                case "Display cleared.": return "表示を消しました。";
                case "Downloading and parsing basic GLB...": return "GLBをダウンロード・解析中…";
                case "READY - direct GLB ": return "読み込み完了 — GLB ";
                case " vertices / ": return " 頂点 / ";
                case " indices": return " インデックス";
                case " + normals": return " ＋法線";
                case " / no textures": return " / テクスチャなし";
                case "GLB load failed.": return "GLBの読み込みに失敗しました。";
                case "Downloading and validating RAC2 response...": return "RAC2をダウンロード・検証中…";
                case "READY  ": return "読み込み完了  ";
                case " (PC draw estimate, not SDK rank)\n": return "（PC描画負荷の参考値・SDKの総合評価ではありません）\n";
                case " tris / ": return " 三角形 / ";
                case " slots / ": return " マテリアル / ";
                case " meshes\nTextures, shader cost and peak memory not rated.": return " メッシュ\nテクスチャ・シェーダー・最大メモリ量は未評価です。";
                case "RAC2 load failed.": return "RAC2の読み込みに失敗しました。";
                case "A load is already active. Please wait.": return "読み込み中です。しばらくお待ちください。";
                case "Enter an HTTPS URL first.": return "HTTPS URLを入力してください。";
                case "Only lowercase HTTPS URLs are accepted.": return "URLは小文字の https:// で始めてください。";
                case "GLB loader is not connected.": return "GLBローダーが接続されていません。";
                case "RAC2 loader is not connected.": return "RAC2ローダーが接続されていません。";
                case "Starting direct GLB request...": return "GLBの読み込みを開始します…";
                case "Starting RAC2 request...": return "RAC2の読み込みを開始します…";
                case "ADAPTIVE LOAD: ON": return "自動負荷調整: オン";
                case "ADAPTIVE LOAD: OFF": return "自動負荷調整: オフ";
                case " For non-trusted domains, enable Allow Untrusted URLs in VRChat.": return " 信頼済み以外の配信先では、VRChatの「Allow Untrusted URLs」を有効にしてください。";
                case "ERROR - ": return "エラー — ";
                case "LOAD RAC2": return "RAC2を読み込む";
                case "DIRECT GLB": return "GLBを読み込む";
                case "SAMPLE GLB": return "GLBサンプル";
                case "SAMPLE RAC2": return "RAC2サンプル";
                case "RETRY": return "再試行";
                case "CLEAR": return "表示を消す";
                case "The model or animation exceeds the booth. Reduce its size in Creator and export again.": return "モデルまたはアニメーションがブースより大きすぎます。Creatorで縮小して再作成してください。";
                case "Invalid or unsupported file. Re-export with the current Creator. See the Unity Console for technical details.": return "ファイルの形式が未対応、またはデータが不正です。最新版のCreatorで再作成してください。技術情報はUnityのConsoleに記録されます。";
                case "Download failed. Check the URL and that the file is public.": return "ダウンロードに失敗しました。URLとファイルの公開設定を確認してください。";
                case "Language changed.": return "表示言語を変更しました。";
            }
            return english;
        }

        private string LocalizeProgress(string progress)
        {
            if (!UseJapanese) return progress;
            if (progress.StartsWith("Expanding RAC2 section ")) return progress.Replace("Expanding RAC2 section ", "RAC2セクションを展開中 ");
            if (progress.StartsWith("Verifying RAC2 section ")) return progress.Replace("Verifying RAC2 section ", "RAC2セクションを検証中 ");
            if (progress.StartsWith("Restoring RAC2 mesh ")) return progress.Replace("Restoring RAC2 mesh ", "RAC2メッシュを復元中 ");
            if (progress.StartsWith("Expanding RAC2 ")) return "RAC2を展開中…";
            if (progress == "Downloading RAC2...") return "RAC2をダウンロード中…";
            return "RAC2を処理中…";
        }

        private string FriendlyLoadError(string detail, bool rac2)
        {
            // Keep the original diagnostics in Console, not mixed into the localized player UI.
            if (!string.IsNullOrEmpty(detail)) Debug.LogError("[FLARE] " + detail);
            if (!string.IsNullOrEmpty(detail) && detail.Contains("booth"))
                return L("The model or animation exceeds the booth. Reduce its size in Creator and export again.");
            if (!string.IsNullOrEmpty(detail) && (detail.Contains("download") || detail.Contains("Download") || detail.Contains("HTTP")))
                return L("Download failed. Check the URL and that the file is public.");
            if (!UseJapanese && !string.IsNullOrEmpty(detail)) return detail;
            return L("Invalid or unsupported file. Re-export with the current Creator. See the Unity Console for technical details.");
        }

        private void Label(Button button, string text)
        {
            if (button == null) return;
            Text label = button.GetComponentInChildren<Text>(true);
            if (label != null)
            {
                label.text = text;
                label.resizeTextForBestFit = true;
                label.resizeTextMinSize = Mathf.Min(12, label.fontSize);
                label.resizeTextMaxSize = label.fontSize;
            }
        }

        private void RefreshLabels()
        {
            // Only known built-in captions; never translate product names or URLs supplied by users.
            Text[] captions = GetComponentsInChildren<Text>(true);
            for (int i = 0; i < captions.Length; i++)
            {
                string value = captions[i].text;
                if (value == "RAC 3D IMAGEPAD / STATIC + VAT + PARTICLE" || value == "FLARE ImagePad / 静止モデル・VAT・パーティクル" || value == "FLARE ImagePad / Static + VAT + Particles")
                    captions[i].text = UseJapanese ? "FLARE ImagePad / 静止モデル・VAT・パーティクル" : "FLARE ImagePad / Static + VAT + Particles";
                if (value == "DIRECT GLB = basic one-mesh preview / RAC2 = full validated preview" || value == "GLB: 単一メッシュの簡易表示 / RAC2: モデル・アニメーション・パーティクル")
                    captions[i].text = UseJapanese ? "GLB: 単一メッシュの簡易表示 / RAC2: モデル・アニメーション・パーティクル" : "DIRECT GLB = basic one-mesh preview / RAC2 = full validated preview";
                if (value == "https://example.com/model.glb   or   https://example.com/model.rac2" || value == "https://example.com/model.rac2")
                    captions[i].text = "https://example.com/model.rac2";
            }
            Label(GlbButton, L("DIRECT GLB"));
            Label(Rac2Button, L("LOAD RAC2"));
            Label(SampleGlbButton, L("SAMPLE GLB"));
            Label(SampleRac2Button, L("SAMPLE RAC2"));
            Label(RetryButton, L("RETRY"));
            Label(ClearButton, L("CLEAR"));
            RefreshAdaptiveLoadingLabel();
        }

        // May also be connected to a world's own visible language controls.
        public void SetJapanese() { UseJapanese = true; RefreshLabels(); if (!_busy) SetReady(L("Language changed.")); }
        public void SetEnglish() { UseJapanese = false; RefreshLabels(); if (!_busy) SetReady(L("Language changed.")); }

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

