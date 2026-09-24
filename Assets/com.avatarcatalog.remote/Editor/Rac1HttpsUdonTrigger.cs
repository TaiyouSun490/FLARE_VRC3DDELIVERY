#if UNITY_EDITOR
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UdonSharpEditor;
using UnityEditor;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;

namespace AvatarCatalog.Remote
{
    /// <summary>
    /// Test-only, one-shot Editor runner for exercising the compiled Udon download path.
    /// Drop Rac1UdonHttpsTrigger.json beside Assets; the runner never injects RAC1 bytes.
    /// </summary>
    [InitializeOnLoad]
    public static class Rac1HttpsUdonTrigger
    {
        private const string LogPrefix = "[RAC1 HTTPS Udon Test]";
        private const string TargetObjectName = "RAC1 RESTORED - X Bot";
        private const string TriggerFileName = "Rac1UdonHttpsTrigger.json";
        private const string ResultFileName = "Rac1UdonHttpsResult.json";
        private const string MarkerFileName = "Rac1UdonHttpsTrigger.last";
        private const string PendingSessionKey = "AvatarCatalog.Rac1HttpsUdonTrigger.Pending.v1";
        private const string LastHashSessionKey = "AvatarCatalog.Rac1HttpsUdonTrigger.LastHash.v1";

        private const int LoaderStatusIdle = 0;
        private const int LoaderStatusLoading = 1;
        private const int LoaderStatusReady = 2;
        private const int LoaderStatusError = 3;
        private const int LoaderStatusDiscarding = 4;

        private static double _nextPollAt;
        private static string _candidateHash;
        private static double _candidateSeenAt;
        private static TriggerConfig _activeConfig;
        private static UdonBehaviour _activeBacking;
        private static GameObject _activeTarget;
        private static double _startedAt;
        private static bool _observedLoading;
        private static string _activeUrlHash;

        [Serializable]
        private sealed class TriggerConfig
        {
            public string requestId;
            public string url;
            public int expectedVertexCount;
            public int expectedIndexCount;
            public int timeoutSeconds = 60;
            public bool captureScreenshot = true;
        }

        [Serializable]
        private sealed class TriggerResult
        {
            public string requestId;
            public string status;
            public string completedUtc;
            public string message;
            public string urlSha256;
            public int loaderStatus;
            public int loadedVertexCount;
            public int loadedIndexCount;
            public int meshVertexCount;
            public int meshIndexCount;
            public bool loadedHasTexture;
            public float elapsedSeconds;
            public string screenshotPath;
        }

        static Rac1HttpsUdonTrigger()
        {
            EditorApplication.update += OnEditorUpdate;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        [MenuItem("Tools/FLARE/Developer/Tests/RAC1 Udon Play Mode/Run HTTPS Trigger Now")]
        private static void RunTriggerNow()
        {
            PollTriggerFile(true);
        }

        private static string ProjectRoot
        {
            get { return Path.GetFullPath(Path.Combine(Application.dataPath, "..")); }
        }

        private static string TriggerPath
        {
            get { return Path.Combine(ProjectRoot, TriggerFileName); }
        }

        private static string ResultPath
        {
            get { return Path.Combine(ProjectRoot, ResultFileName); }
        }

        private static string MarkerPath
        {
            get { return Path.Combine(ProjectRoot, MarkerFileName); }
        }

        private static void OnEditorUpdate()
        {
            if (_activeConfig != null)
            {
                MonitorActiveRun();
                return;
            }

            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                return;
            }

            string pendingJson = SessionState.GetString(PendingSessionKey, string.Empty);
            if (EditorApplication.isPlaying && !string.IsNullOrEmpty(pendingJson))
            {
                SessionState.EraseString(PendingSessionKey);
                TriggerConfig pending = JsonUtility.FromJson<TriggerConfig>(pendingJson);
                BeginInPlayMode(pending);
                return;
            }

            if (!EditorApplication.isPlayingOrWillChangePlaymode && EditorApplication.timeSinceStartup >= _nextPollAt)
            {
                _nextPollAt = EditorApplication.timeSinceStartup + 0.5d;
                PollTriggerFile(false);
            }
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingPlayMode && _activeConfig != null)
            {
                Finish(false, "Play Mode exited before the HTTPS download completed.");
            }
        }

        private static void PollTriggerFile(bool force)
        {
            if (_activeConfig != null)
            {
                Debug.LogWarning(LogPrefix + " A test is already running.");
                return;
            }

            if (!File.Exists(TriggerPath))
            {
                if (force)
                {
                    Debug.LogError(LogPrefix + " Trigger file not found: " + TriggerPath);
                }

                return;
            }

            string json;
            string hash;
            try
            {
                json = File.ReadAllText(TriggerPath, Encoding.UTF8);
                hash = Sha256(json);
            }
            catch (Exception exception)
            {
                Debug.LogWarning(LogPrefix + " Trigger is not readable yet: " + exception.Message);
                return;
            }

            string lastHash = SessionState.GetString(LastHashSessionKey, string.Empty);
            if (string.IsNullOrEmpty(lastHash) && File.Exists(MarkerPath))
            {
                try
                {
                    lastHash = File.ReadAllText(MarkerPath, Encoding.UTF8).Trim();
                }
                catch (Exception)
                {
                    lastHash = string.Empty;
                }
            }

            if (!force && string.Equals(lastHash, hash, StringComparison.Ordinal))
            {
                return;
            }

            // Require two identical observations so a writer cannot leave us with partial JSON.
            if (!force && (!string.Equals(_candidateHash, hash, StringComparison.Ordinal) ||
                           EditorApplication.timeSinceStartup - _candidateSeenAt < 0.45d))
            {
                if (!string.Equals(_candidateHash, hash, StringComparison.Ordinal))
                {
                    _candidateHash = hash;
                    _candidateSeenAt = EditorApplication.timeSinceStartup;
                }

                return;
            }

            SessionState.SetString(LastHashSessionKey, hash);
            try
            {
                File.WriteAllText(MarkerPath, hash, new UTF8Encoding(false));
            }
            catch (Exception exception)
            {
                Debug.LogWarning(LogPrefix + " Could not persist trigger marker: " + exception.Message);
            }

            TriggerConfig config;
            try
            {
                config = JsonUtility.FromJson<TriggerConfig>(json);
            }
            catch (Exception exception)
            {
                WriteStandaloneFailure("invalid-trigger", string.Empty, "Invalid trigger JSON: " + exception.Message);
                return;
            }

            string validationError;
            if (!ValidateConfig(config, out validationError))
            {
                WriteStandaloneFailure(config != null ? config.requestId : "invalid-trigger", string.Empty, validationError);
                return;
            }

            config.timeoutSeconds = Mathf.Clamp(config.timeoutSeconds <= 0 ? 60 : config.timeoutSeconds, 10, 300);
            string urlHash = Sha256(config.url);
            WriteProgress(config, urlHash, EditorApplication.isPlaying ? "STARTING" : "WAITING_FOR_PLAY_MODE");

            if (EditorApplication.isPlaying)
            {
                BeginInPlayMode(config);
                return;
            }

            RemoteAvatarCatalogLoader loader;
            UdonBehaviour backing;
            GameObject target;
            string error;
            if (!TryFindHarness(out loader, out backing, out target, out error) ||
                !TryApplyInspectorUrl(loader, backing, config.url, false, out error))
            {
                WriteStandaloneFailure(config.requestId, urlHash, error);
                return;
            }

            SessionState.SetString(PendingSessionKey, JsonUtility.ToJson(config));
            Debug.Log(LogPrefix + " request=" + config.requestId +
                      " armed; entering Play Mode with an inspector-serialized VRCUrl (urlSha256=" + urlHash + ").");
            EditorApplication.isPlaying = true;
        }

        private static bool ValidateConfig(TriggerConfig config, out string error)
        {
            if (config == null)
            {
                error = "Trigger JSON did not contain an object.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(config.requestId) || config.requestId.Length > 128)
            {
                error = "requestId is required and must be at most 128 characters.";
                return false;
            }

            for (int index = 0; index < config.requestId.Length; index++)
            {
                if (char.IsControl(config.requestId[index]))
                {
                    error = "requestId may not contain control characters.";
                    return false;
                }
            }

            Uri uri;
            if (!Uri.TryCreate(config.url, UriKind.Absolute, out uri) ||
                !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(uri.Host) ||
                !string.IsNullOrEmpty(uri.UserInfo))
            {
                error = "url must be an absolute HTTPS URL without embedded credentials.";
                return false;
            }

            if (config.expectedVertexCount < 0 || config.expectedIndexCount < 0)
            {
                error = "Expected counts must be zero (unchecked) or positive.";
                return false;
            }

            error = string.Empty;
            return true;
        }

        private static void BeginInPlayMode(TriggerConfig config)
        {
            if (config == null)
            {
                WriteStandaloneFailure("invalid-trigger", string.Empty, "Pending trigger could not be restored after entering Play Mode.");
                return;
            }

            RemoteAvatarCatalogLoader loader;
            UdonBehaviour backing;
            GameObject target;
            string error;
            if (!TryFindHarness(out loader, out backing, out target, out error))
            {
                WriteStandaloneFailure(config.requestId, Sha256(config.url), error);
                return;
            }

            int status;
            if (!backing.TryGetProgramVariable("Status", out status))
            {
                WriteStandaloneFailure(config.requestId, Sha256(config.url),
                    "The backing UdonBehaviour has no live compiled Status variable.");
                return;
            }

            if (status == LoaderStatusLoading || status == LoaderStatusDiscarding)
            {
                WriteStandaloneFailure(config.requestId, Sha256(config.url),
                    "The loader is busy (status=" + status + "). Retry with a new requestId after it settles.");
                return;
            }

            if (!TryApplyInspectorUrl(loader, backing, config.url, true, out error))
            {
                WriteStandaloneFailure(config.requestId, Sha256(config.url), error);
                return;
            }

            _activeConfig = config;
            _activeBacking = backing;
            _activeTarget = target;
            _activeUrlHash = Sha256(config.url);
            _startedAt = EditorApplication.timeSinceStartup;
            _observedLoading = false;

            try
            {
                // This is deliberately the compiled Udon entry point. No C# proxy method and no byte injection.
                backing.SendCustomEvent("LoadSelectedSlot");
            }
            catch (Exception exception)
            {
                Finish(false, "SendCustomEvent(LoadSelectedSlot) failed: " + exception.Message);
                return;
            }

            if (backing.TryGetProgramVariable("Status", out status) && status == LoaderStatusLoading)
            {
                _observedLoading = true;
            }

            Debug.Log(LogPrefix + " request=" + config.requestId +
                      " fired backing UdonBehaviour.LoadSelectedSlot (urlSha256=" + _activeUrlHash + ").");
        }

        private static bool TryFindHarness(
            out RemoteAvatarCatalogLoader loader,
            out UdonBehaviour backing,
            out GameObject target,
            out string error)
        {
            target = GameObject.Find(TargetObjectName);
            loader = target != null ? target.GetComponent<RemoteAvatarCatalogLoader>() : null;
            if (loader == null)
            {
                loader = UnityEngine.Object.FindObjectOfType<RemoteAvatarCatalogLoader>();
            }

            if (loader == null)
            {
                backing = null;
                error = "RemoteAvatarCatalogLoader was not found. Open the XBot-Udon-PlayMode scene first.";
                return false;
            }

            if (target == null)
            {
                target = loader.gameObject;
            }

            backing = UdonSharpEditorUtility.GetBackingUdonBehaviour(loader);
            if (backing == null)
            {
                error = "The loader has no compiled backing UdonBehaviour.";
                return false;
            }

            error = string.Empty;
            return true;
        }

        private static bool TryApplyInspectorUrl(
            RemoteAvatarCatalogLoader loader,
            UdonBehaviour backing,
            string url,
            bool inPlayMode,
            out string error)
        {
            try
            {
                VRCUrl[] catalogUrls = { new VRCUrl(url) };
                loader.CatalogUrls = catalogUrls;
                loader.SelectedSlot = 0;
                loader.LoadOnInteract = false;
                loader.VerboseLogging = true;

                if (inPlayMode)
                {
                    // Copy only the inspector-authored inputs so live Status/output fields cannot be reset.
                    backing.SetProgramVariable("CatalogUrls", catalogUrls);
                    backing.SetProgramVariable("SelectedSlot", 0);
                    backing.SetProgramVariable("LoadOnInteract", false);
                    backing.SetProgramVariable("VerboseLogging", true);
                }
                else
                {
                    UdonSharpEditorUtility.CopyProxyToUdon(loader, ProxySerializationPolicy.All);
                }

                error = string.Empty;
                return true;
            }
            catch (Exception exception)
            {
                error = "Could not serialize the inspector VRCUrl into the backing UdonBehaviour: " + exception.Message;
                return false;
            }
        }

        private static void MonitorActiveRun()
        {
            if (!EditorApplication.isPlaying || _activeBacking == null)
            {
                Finish(false, "The live backing UdonBehaviour disappeared during the test.");
                return;
            }

            int status;
            if (!_activeBacking.TryGetProgramVariable("Status", out status))
            {
                Finish(false, "The compiled Status variable became unavailable.");
                return;
            }

            if (status == LoaderStatusLoading)
            {
                _observedLoading = true;
            }

            if (status == LoaderStatusError)
            {
                string lastError = ReadString(_activeBacking, "LastError");
                Finish(false, string.IsNullOrEmpty(lastError) ? "The Udon loader reported an error." : lastError);
                return;
            }

            if (status == LoaderStatusReady && _observedLoading)
            {
                ValidateReadyResult();
                return;
            }

            double elapsed = EditorApplication.timeSinceStartup - _startedAt;
            if (elapsed > _activeConfig.timeoutSeconds)
            {
                string statusMessage = ReadString(_activeBacking, "StatusMessage");
                Finish(false, "Timed out after " + _activeConfig.timeoutSeconds + "s; loader status=" + status +
                              (string.IsNullOrEmpty(statusMessage) ? "." : " (" + statusMessage + ")."));
            }
        }

        private static void ValidateReadyResult()
        {
            int loadedVertices = ReadInt(_activeBacking, "LoadedVertexCount");
            int loadedIndices = ReadInt(_activeBacking, "LoadedIndexCount");
            bool hasTexture = ReadBool(_activeBacking, "LoadedHasTexture");
            MeshFilter meshFilter = _activeTarget != null ? _activeTarget.GetComponent<MeshFilter>() : null;
            Mesh mesh = meshFilter != null ? meshFilter.sharedMesh : null;
            if (mesh == null)
            {
                Finish(false, "Status reached Ready, but the restored MeshFilter has no mesh.");
                return;
            }

            long indexTotal = 0L;
            for (int subMesh = 0; subMesh < mesh.subMeshCount; subMesh++)
            {
                indexTotal += (long)mesh.GetIndexCount(subMesh);
            }

            if (indexTotal > int.MaxValue)
            {
                Finish(false, "The restored mesh index count exceeds Int32.MaxValue.");
                return;
            }

            int meshIndices = (int)indexTotal;
            if (loadedVertices <= 0 || loadedIndices <= 0 ||
                mesh.vertexCount != loadedVertices || meshIndices != loadedIndices)
            {
                Finish(false, "Loader/mesh count mismatch: loader=" + loadedVertices + "/" + loadedIndices +
                              ", mesh=" + mesh.vertexCount + "/" + meshIndices + ".");
                return;
            }

            if (_activeConfig.expectedVertexCount > 0 && loadedVertices != _activeConfig.expectedVertexCount)
            {
                Finish(false, "Expected " + _activeConfig.expectedVertexCount + " vertices, got " + loadedVertices + ".");
                return;
            }

            if (_activeConfig.expectedIndexCount > 0 && loadedIndices != _activeConfig.expectedIndexCount)
            {
                Finish(false, "Expected " + _activeConfig.expectedIndexCount + " indices, got " + loadedIndices + ".");
                return;
            }

            Finish(true, "Compiled Udon downloaded and restored the RAC1 mesh.");
        }

        private static int ReadInt(UdonBehaviour backing, string variableName)
        {
            int value;
            return backing != null && backing.TryGetProgramVariable(variableName, out value) ? value : 0;
        }

        private static bool ReadBool(UdonBehaviour backing, string variableName)
        {
            bool value;
            return backing != null && backing.TryGetProgramVariable(variableName, out value) && value;
        }

        private static string ReadString(UdonBehaviour backing, string variableName)
        {
            string value;
            return backing != null && backing.TryGetProgramVariable(variableName, out value) ? value : string.Empty;
        }

        private static void Finish(bool pass, string message)
        {
            TriggerConfig config = _activeConfig;
            UdonBehaviour backing = _activeBacking;
            GameObject target = _activeTarget;
            string urlHash = _activeUrlHash;
            double elapsed = Math.Max(0d, EditorApplication.timeSinceStartup - _startedAt);
            int status = ReadInt(backing, "Status");
            int loadedVertices = ReadInt(backing, "LoadedVertexCount");
            int loadedIndices = ReadInt(backing, "LoadedIndexCount");
            bool hasTexture = ReadBool(backing, "LoadedHasTexture");
            int meshVertices = 0;
            int meshIndices = 0;
            MeshFilter meshFilter = target != null ? target.GetComponent<MeshFilter>() : null;
            Mesh mesh = meshFilter != null ? meshFilter.sharedMesh : null;
            if (mesh != null)
            {
                meshVertices = mesh.vertexCount;
                long total = 0L;
                for (int subMesh = 0; subMesh < mesh.subMeshCount; subMesh++)
                {
                    total += (long)mesh.GetIndexCount(subMesh);
                }

                meshIndices = total <= int.MaxValue ? (int)total : -1;
            }

            string safeMessage = RedactUrl(message, config != null ? config.url : string.Empty);
            string screenshotPath = string.Empty;
            if (pass && config != null && config.captureScreenshot && EditorApplication.isPlaying)
            {
                screenshotPath = Path.Combine(ProjectRoot,
                    "Rac1UdonHttps-" + SafeFilePart(config.requestId) + ".png");
                ScreenCapture.CaptureScreenshot(screenshotPath);
            }

            TriggerResult result = new TriggerResult
            {
                requestId = config != null ? config.requestId : "unknown",
                status = pass ? "PASS" : "FAIL",
                completedUtc = DateTime.UtcNow.ToString("o"),
                message = safeMessage,
                urlSha256 = urlHash,
                loaderStatus = status,
                loadedVertexCount = loadedVertices,
                loadedIndexCount = loadedIndices,
                meshVertexCount = meshVertices,
                meshIndexCount = meshIndices,
                loadedHasTexture = hasTexture,
                elapsedSeconds = (float)elapsed,
                screenshotPath = screenshotPath
            };
            WriteResult(result);

            string marker = pass ? "PASS" : "FAIL";
            string log = LogPrefix + " " + marker + " request=" + result.requestId +
                         " vertices=" + loadedVertices + " indices=" + loadedIndices +
                         " texture=" + hasTexture + " elapsed=" + elapsed.ToString("0.00") + "s" +
                         " message=" + safeMessage;
            if (pass)
            {
                Debug.Log(log);
            }
            else
            {
                Debug.LogError(log);
            }

            _activeConfig = null;
            _activeBacking = null;
            _activeTarget = null;
            _activeUrlHash = string.Empty;
            _observedLoading = false;
        }

        private static void WriteProgress(TriggerConfig config, string urlHash, string status)
        {
            WriteResult(new TriggerResult
            {
                requestId = config.requestId,
                status = status,
                completedUtc = DateTime.UtcNow.ToString("o"),
                message = "Trigger accepted.",
                urlSha256 = urlHash
            });
        }

        private static void WriteStandaloneFailure(string requestId, string urlHash, string message)
        {
            TriggerResult result = new TriggerResult
            {
                requestId = string.IsNullOrEmpty(requestId) ? "unknown" : requestId,
                status = "FAIL",
                completedUtc = DateTime.UtcNow.ToString("o"),
                message = message,
                urlSha256 = urlHash
            };
            WriteResult(result);
            Debug.LogError(LogPrefix + " FAIL request=" + result.requestId + " message=" + message);
        }

        private static void WriteResult(TriggerResult result)
        {
            try
            {
                string tempPath = ResultPath + ".tmp";
                File.WriteAllText(tempPath, JsonUtility.ToJson(result, true), new UTF8Encoding(false));
                File.Copy(tempPath, ResultPath, true);
                File.Delete(tempPath);
            }
            catch (Exception exception)
            {
                Debug.LogError(LogPrefix + " Could not write result JSON: " + exception.Message);
            }
        }

        private static string RedactUrl(string message, string url)
        {
            if (string.IsNullOrEmpty(message) || string.IsNullOrEmpty(url))
            {
                return message ?? string.Empty;
            }

            return message.Replace(url, "<HTTPS URL redacted>");
        }

        private static string SafeFilePart(string value)
        {
            StringBuilder builder = new StringBuilder();
            if (!string.IsNullOrEmpty(value))
            {
                for (int index = 0; index < value.Length && builder.Length < 48; index++)
                {
                    char character = value[index];
                    if (char.IsLetterOrDigit(character) || character == '-' || character == '_')
                    {
                        builder.Append(character);
                    }
                }
            }

            return builder.Length > 0 ? builder.ToString() : "run";
        }

        private static string Sha256(string value)
        {
            using (SHA256 algorithm = SHA256.Create())
            {
                byte[] bytes = algorithm.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty));
                StringBuilder builder = new StringBuilder(bytes.Length * 2);
                for (int index = 0; index < bytes.Length; index++)
                {
                    builder.Append(bytes[index].ToString("x2"));
                }

                return builder.ToString();
            }
        }
    }
}
#endif
