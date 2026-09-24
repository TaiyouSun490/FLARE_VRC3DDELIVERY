using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Unity.Jobs;
using VRC.Dynamics;
using Object = UnityEngine.Object;

namespace AvatarCatalog.Remote
{
    /// <summary>
    /// Runs the SDK solver only on an export copy. No Play Mode, global time changes,
    /// scene-wide dynamics update or replacement physics approximation.
    /// The SDK has no public offline step API: keep its version-specific bridge here.
    /// </summary>
    internal sealed class Rac2PhysBoneBakeSession : IDisposable
    {
        internal const string SupportedSdk = "3.10.4";
        private const BindingFlags InternalInstance = BindingFlags.NonPublic | BindingFlags.Instance;
        private static readonly MethodInfo Schedule = typeof(PhysBoneManager).GetMethod("ScheduleExecutionJob", InternalInstance,
            null, new[] { typeof(JobHandle) }, null);
        private static readonly MethodInfo StartBone = typeof(VRCPhysBoneBase).GetMethod("Start", InternalInstance);
        private static readonly MethodInfo DestroyManager = typeof(PhysBoneManager).GetMethod("OnDestroy", InternalInstance);
        private static readonly FieldInfo ManagerBuffer = typeof(PhysBoneManager).GetField("buffer", InternalInstance);
        private static readonly FieldInfo CriticalErrorBuffer = typeof(PhysBoneManager).GetField("errorBuffer", InternalInstance);
        private static bool _busy;
        private readonly List<VRCPhysBoneBase> _bones = new List<VRCPhysBoneBase>();
        private readonly List<VRCPhysBoneColliderBase> _colliders = new List<VRCPhysBoneColliderBase>();
        private readonly Transform[] _transforms;
        private readonly Vector3[] _basePositions;
        private readonly Quaternion[] _baseRotations;
        private readonly Vector3[] _firstOffsets;
        private readonly Quaternion[] _firstRotationOffsets;
        private readonly Vector3[] _outputPositions;
        private readonly Quaternion[] _outputRotations;
        private readonly bool[] _affected;
        private readonly Vector3[] _restPositions;
        private readonly Quaternion[] _restRotations;
        private PhysBoneManager _manager;
        private GameObject _managerObject;
        private bool _ownsBusy;
        private bool _firstRecorded;
        private bool _outputBlended;
        public int ChainCount => _bones.Count;

        internal static int Count(GameObject root)
        {
            if (!root) return 0;
            int count = 0;
            foreach (var bone in root.GetComponentsInChildren<VRCPhysBoneBase>(true))
                if (bone.isActiveAndEnabled) count++;
            return count;
        }

        internal static void ValidateEnvironment()
        {
            var package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(PhysBoneManager).Assembly);
            if (package == null || package.version != SupportedSdk || Schedule == null || StartBone == null
                || DestroyManager == null || ManagerBuffer == null || CriticalErrorBuffer == null)
                throw new NotSupportedException(FlareLocalization.Text("PhysBone VAT supports SDK ") + SupportedSdk
                    + FlareLocalization.Text(". Offline SDK APIs differ. Disable Include PhysBones to use standard VAT."));
            if (EditorApplication.isPlayingOrWillChangePlaymode || _busy || PhysBoneManager.Inst)
                throw new InvalidOperationException(FlareLocalization.Text("Stop other PhysBone simulations and Play Mode before exporting."));
        }

        public Rac2PhysBoneBakeSession(GameObject copy)
        {
            ValidateEnvironment();
            if (!copy || !EditorSceneManager.IsPreviewScene(copy.scene) || EditorUtility.IsPersistent(copy))
                throw new ArgumentException(FlareLocalization.Text("PhysBone baking requires an isolated preview-scene copy."));
            _transforms = copy.GetComponentsInChildren<Transform>(true);
            _basePositions = new Vector3[_transforms.Length];
            _baseRotations = new Quaternion[_transforms.Length];
            _firstOffsets = new Vector3[_transforms.Length];
            _firstRotationOffsets = new Quaternion[_transforms.Length];
            _outputPositions = new Vector3[_transforms.Length];
            _outputRotations = new Quaternion[_transforms.Length];
            _affected = new bool[_transforms.Length];
            _restPositions = new Vector3[_transforms.Length];
            _restRotations = new Quaternion[_transforms.Length];
            for (int i = 0; i < _transforms.Length; i++)
            {
                _restPositions[i] = _transforms[i].localPosition;
                _restRotations[i] = _transforms[i].localRotation;
            }
            try
            {
                foreach (var bone in copy.GetComponentsInChildren<VRCPhysBoneBase>(true))
                {
                    if (!bone.isActiveAndEnabled) continue;
                    RequireInside(copy, bone.GetRootTransform());
                    if (bone.worldImmobileTransform) RequireInside(copy, bone.worldImmobileTransform);
                    foreach (var ignored in bone.ignoreTransforms) if (ignored) RequireInside(copy, ignored);
                    foreach (var collider in bone.colliders)
                    {
                        if (!collider) continue;
                        RequireInside(copy, collider.transform);
                        RequireInside(copy, collider.GetRootTransform());
                    }
                    _bones.Add(bone);
                }
                if (_bones.Count == 0) throw new InvalidOperationException(FlareLocalization.Text("No active PhysBones found."));
                foreach (var collider in copy.GetComponentsInChildren<VRCPhysBoneColliderBase>(true))
                    if (collider.isActiveAndEnabled)
                    {
                        RequireInside(copy, collider.GetRootTransform());
                        _colliders.Add(collider);
                    }
                // Prevent overlapping exports; the singleton exists only inside synchronous calls.
                _busy = _ownsBusy = true;
                _managerObject = new GameObject("FLARE offline PhysBone solver") { hideFlags = HideFlags.HideAndDontSave };
                SceneManager.MoveGameObjectToScene(_managerObject, copy.scene);
                _manager = _managerObject.AddComponent<PhysBoneManager>();
                _manager.enabled = false;
                _manager.IsSDK = true;
                _manager.Init();
                PhysBoneManager.Inst = _manager;
                foreach (var collider in _colliders) _manager.AddCollider(collider);
                foreach (var bone in _bones)
                {
                    StartBone.Invoke(bone, null);
                    bone.InitTransforms(false);
                    foreach (var entry in bone.bones)
                    {
                        if (!entry.transform) continue;
                        RequireInside(copy, entry.transform);
                        int index = Array.IndexOf(_transforms, entry.transform);
                        if (index >= 0) _affected[index] = true;
                    }
                }
            }
            catch { Dispose(); throw; }
            finally { if (PhysBoneManager.Inst == _manager) PhysBoneManager.Inst = null; }
        }

        private static void RequireInside(GameObject root, Transform value)
        {
            if (!value || (value != root.transform && !value.IsChildOf(root.transform)))
                throw new InvalidOperationException(FlareLocalization.Text("PhysBone bones or colliders reference objects outside the Exhibit Root. Include them under the same root."));
        }

        /// <summary>Call after clip and native constraints, once per fixed simulation step.</summary>
        public void Step(float deltaTime)
        {
            if (!_manager) throw new ObjectDisposedException(nameof(Rac2PhysBoneBakeSession));
            if (deltaTime < 0 || deltaTime > 1f / 30f || float.IsNaN(deltaTime))
                throw new ArgumentOutOfRangeException(nameof(deltaTime));
            if (PhysBoneManager.Inst) throw new InvalidOperationException(FlareLocalization.Text("Another PhysBone solver started. Export stopped."));
            RestoreOutput();
            for (int i = 0; i < _transforms.Length; i++)
            {
                _basePositions[i] = _transforms[i].localPosition;
                _baseRotations[i] = _transforms[i].localRotation;
            }
            bool oldTiming = PhysBoneManager.DisableTiming;
            float oldDelta = PhysBoneManager.DebugTimeElapsed;
            try
            {
                PhysBoneManager.Inst = _manager;
                PhysBoneManager.DisableTiming = true;
                PhysBoneManager.DebugTimeElapsed = deltaTime;
                var job = (JobHandle)Schedule.Invoke(_manager, new object[] { default(JobHandle) });
                job.Complete(); // Never yield with a running job or altered SDK globals.
                var errors = CriticalErrorBuffer.GetValue(_manager);
                var error = errors.GetType().GetProperty("Item").GetValue(errors, new object[] { 0 });
                if (Convert.ToInt32(error) != 0)
                    throw new InvalidOperationException(FlareLocalization.Text("SDK PhysBone solver error: ") + error);
                foreach (var bone in _bones)
                {
                    if (!bone.isActiveAndEnabled)
                        throw new NotSupportedException(FlareLocalization.Text("Clip-driven PhysBone activation is not supported."));
                    if (_manager.FindChainIndex(bone.chainId) < 0)
                        throw new InvalidOperationException(FlareLocalization.Text("Cannot build PhysBone chain: ") + bone.name);
                }
                for (int i = 0; i < _transforms.Length; i++)
                {
                    var p = _transforms[i].localPosition;
                    var q = _transforms[i].localRotation;
                    if (!Finite(p.x) || !Finite(p.y) || !Finite(p.z) || !Finite(q.x) || !Finite(q.y) || !Finite(q.z) || !Finite(q.w))
                        throw new InvalidOperationException(FlareLocalization.Text("PhysBone simulation produced non-finite values: ") + _transforms[i].name);
                }
            }
            finally
            {
                PhysBoneManager.DisableTiming = oldTiming;
                PhysBoneManager.DebugTimeElapsed = oldDelta;
                if (PhysBoneManager.Inst == _manager) PhysBoneManager.Inst = null;
            }
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        // Unanimated transforms must not use the preceding physics pose as animation input.
        // The solver keeps its own previous positions/velocities in its chain buffers.
        public void ResetInputPose()
        {
            RestoreOutput();
            for (int i = 0; i < _transforms.Length; i++)
                if (_affected[i])
                {
                    _transforms[i].localPosition = _restPositions[i];
                    _transforms[i].localRotation = _restRotations[i];
                }
        }

        // Blend only physics offsets near the loop seam, not the selected clip/root motion.
        // RestoreOutput prevents this display correction from feeding back into simulation.
        public void PrepareOutput(float seamWeight)
        {
            for (int i = 0; i < _transforms.Length; i++)
            {
                if (!_affected[i]) continue;
                var t = _transforms[i];
                Vector3 offset = t.localPosition - _basePositions[i];
                Quaternion rotation = Quaternion.Inverse(_baseRotations[i]) * t.localRotation;
                if (!_firstRecorded) { _firstOffsets[i] = offset; _firstRotationOffsets[i] = rotation; }
                _outputPositions[i] = t.localPosition;
                _outputRotations[i] = t.localRotation;
                t.localPosition = _basePositions[i] + Vector3.Lerp(offset, _firstOffsets[i], seamWeight);
                t.localRotation = _baseRotations[i] * Quaternion.Slerp(rotation, _firstRotationOffsets[i], seamWeight);
            }
            _firstRecorded = _outputBlended = true;
        }

        public void RestoreOutput()
        {
            if (!_outputBlended) return;
            for (int i = 0; i < _transforms.Length; i++)
                if (_affected[i] && _transforms[i])
                {
                    _transforms[i].localPosition = _outputPositions[i];
                    _transforms[i].localRotation = _outputRotations[i];
                }
            _outputBlended = false;
        }

        public void Dispose()
        {
            RestoreOutput();
            // Copies are about to be destroyed. Avoid their OnDisable using a missing singleton.
            foreach (var bone in _bones) if (bone) bone.chainId = ChainId.Null;
            if (PhysBoneManager.Inst == _manager) PhysBoneManager.Inst = null;
            var manager = _manager;
            try
            {
                if (_managerObject) Object.DestroyImmediate(_managerObject);
                // Non-ExecuteAlways components may never receive OnDestroy in Edit Mode.
                // The SDK nulls buffer in its destructor: only invoke if native cleanup did not run.
                if (!ReferenceEquals(manager, null) && ManagerBuffer.GetValue(manager) != null)
                    DestroyManager.Invoke(manager, null);
            }
            finally
            {
                _managerObject = null;
                _manager = null;
                if (_ownsBusy) { _ownsBusy = false; _busy = false; }
            }
        }
    }
}
