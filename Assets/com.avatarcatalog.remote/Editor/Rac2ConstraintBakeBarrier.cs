using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.LowLevel;

namespace AvatarCatalog.Remote
{
    /// <summary>Observes completed native constraint passes, never editor callback counts.</summary>
    internal sealed class Rac2ConstraintBakeBarrier : IDisposable
    {
        private struct CompletedConstraintPass { }
        private readonly Transform[] _transforms;
        private readonly Matrix4x4[] _previous;
        private readonly ParentConstraint[] _parents;
        private readonly PlayerLoopSystem.UpdateFunction _callback;
        private readonly int _maximumPasses;
        private int _passes;
        private int _stablePasses;
        private bool _sampling;
        private double _started;
        private string _problem;
        private const float MatrixTolerance = 0.00001f;

        public Rac2ConstraintBakeBarrier(GameObject root, int maximumPasses)
        {
            _maximumPasses = Mathf.Max(2, maximumPasses);
            _transforms = root.GetComponentsInChildren<Transform>(true);
            _previous = new Matrix4x4[_transforms.Length];
            _parents = root.GetComponentsInChildren<ParentConstraint>(true);
            _callback = AfterConstraints;
            var loop = PlayerLoop.GetCurrentPlayerLoop();
            var marker = new PlayerLoopSystem { type = typeof(CompletedConstraintPass), updateDelegate = _callback };
            if (!InsertAfterConstraints(ref loop, marker))
                throw new NotSupportedException(FlareLocalization.Text("Unity constraint update is unavailable. Clothing VAT export stopped."));
            PlayerLoop.SetPlayerLoop(loop);
        }

        public void BeginFrame()
        {
            _passes = 0;
            _stablePasses = 0;
            _problem = FlareLocalization.Text("Clothing constraints have not stabilized");
            _started = EditorApplication.timeSinceStartup;
            _sampling = true;
        }

        public bool IsReady(int frame)
        {
            if (_stablePasses >= 1 && _passes >= 2) { _sampling = false; return true; }
            if (_passes >= _maximumPasses || EditorApplication.timeSinceStartup - _started > 15d)
                throw new InvalidOperationException(FlareLocalization.Text("VAT frame ") + (frame + 1) + FlareLocalization.Text(": ") + _problem
                    + FlareLocalization.Text(". Check constraint sources and cycles. The evaluation limit is adjustable in the Creator clothing settings."));
            return false;
        }

        private void AfterConstraints()
        {
            if (!_sampling) return;
            bool stable = _passes > 0;
            for (int i = 0; i < _transforms.Length; i++)
            {
                if (_transforms[i] == null) { stable = false; continue; }
                var matrix = _transforms[i].localToWorldMatrix;
                for (int j = 0; j < 16; j++)
                {
                    float value = matrix[j];
                    if (float.IsNaN(value) || float.IsInfinity(value) || Mathf.Abs(value - _previous[i][j]) > MatrixTolerance)
                        stable = false;
                }
                _previous[i] = matrix;
            }
            _passes++;
            // Also catch a stable but unbound, full-weight parent constraint. No name-based bone remapping.
            bool attached = FullWeightParentsAttached();
            _stablePasses = stable && attached ? _stablePasses + 1 : 0;
        }

        private bool FullWeightParentsAttached()
        {
            foreach (var c in _parents)
            {
                if (!c || !c.isActiveAndEnabled || !c.constraintActive || c.weight < .99999f || c.sourceCount != 1) continue;
                var source = c.GetSource(0);
                if (source.weight < .99999f) continue;
                if (!source.sourceTransform)
                {
                    _problem = c.name + FlareLocalization.Text(" has a Parent Constraint with no source");
                    return false;
                }
                // The zero-offset case is independent of parent/source scaling conventions.
                if (c.translationAxis == (Axis.X | Axis.Y | Axis.Z) && c.GetTranslationOffset(0) == Vector3.zero
                    && Vector3.Distance(c.transform.position, source.sourceTransform.position) > .0001f)
                {
                    _problem = c.name + FlareLocalization.Text(" has a Parent Constraint position mismatch");
                    return false;
                }
                if (c.rotationAxis == (Axis.X | Axis.Y | Axis.Z)
                    && Quaternion.Angle(c.transform.rotation,
                        source.sourceTransform.rotation * Quaternion.Euler(c.GetRotationOffset(0))) > .1f)
                {
                    _problem = c.name + FlareLocalization.Text(" has a Parent Constraint rotation mismatch");
                    return false;
                }
            }
            return true;
        }

        internal static bool InsertAfterConstraints(ref PlayerLoopSystem loop, PlayerLoopSystem marker)
        {
            var children = loop.subSystemList;
            if (children == null) return false;
            for (int i = 0; i < children.Length; i++)
            {
                if (children[i].type == typeof(UnityEngine.PlayerLoop.PreLateUpdate.ConstraintManagerUpdate))
                {
                    var next = new PlayerLoopSystem[children.Length + 1];
                    Array.Copy(children, 0, next, 0, i + 1);
                    next[i + 1] = marker;
                    Array.Copy(children, i + 1, next, i + 2, children.Length - i - 1);
                    loop.subSystemList = next;
                    return true;
                }
                var child = children[i];
                if (!InsertAfterConstraints(ref child, marker)) continue;
                var replacement = (PlayerLoopSystem[])children.Clone();
                replacement[i] = child;
                loop.subSystemList = replacement;
                return true;
            }
            return false;
        }

        internal static bool RemoveMarker(ref PlayerLoopSystem loop, PlayerLoopSystem.UpdateFunction callback)
        {
            var children = loop.subSystemList;
            if (children == null) return false;
            bool changed = false;
            var kept = new System.Collections.Generic.List<PlayerLoopSystem>(children.Length);
            foreach (var entry in children)
            {
                if (entry.type == typeof(CompletedConstraintPass) && entry.updateDelegate == callback) { changed = true; continue; }
                var child = entry;
                changed |= RemoveMarker(ref child, callback);
                kept.Add(child);
            }
            if (changed) loop.subSystemList = kept.ToArray();
            return changed;
        }

        public void Dispose()
        {
            _sampling = false;
            var loop = PlayerLoop.GetCurrentPlayerLoop();
            // Remove only our callback: do not restore an old loop over other packages' changes.
            if (RemoveMarker(ref loop, _callback)) PlayerLoop.SetPlayerLoop(loop);
        }
    }
}
