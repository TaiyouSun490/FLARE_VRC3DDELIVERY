using System;
using System.Collections;
using UnityEditor;
using UnityEngine;
using VRC.Dynamics;

namespace AvatarCatalog.Remote
{
    /// <summary>Sequential clip -> constraints -> SDK physics -> mesh-capture poses.</summary>
    internal sealed class Rac2VatFrameBaker : IDisposable
    {
        private Rac2VatAnimationSampler _sampler;
        private Rac2ConstraintBakeBarrier _barrier;
        private Rac2PhysBoneBakeSession _physics;
        private readonly AnimationClip _clip;
        private int _nextFrame;
        private float _lastTime;

        internal static void ValidateClip(AnimationClip clip)
        {
            foreach (var binding in AnimationUtility.GetCurveBindings(clip))
                if (typeof(VRCPhysBoneBase).IsAssignableFrom(binding.type)
                    || typeof(VRCPhysBoneColliderBase).IsAssignableFrom(binding.type)
                    || binding.type == typeof(GameObject) && binding.propertyName == "m_IsActive")
                    throw new NotSupportedException(FlareLocalization.Text("PhysBone VAT does not support clip-driven PhysBone/Collider settings or object activation. Curve: ")
                        + binding.path + " / " + binding.propertyName);
        }

        public Rac2VatFrameBaker(GameObject root, AnimationClip clip, bool constraints, int maximumPasses, bool physics)
        {
            _clip = clip;
            try
            {
                if (physics)
                {
                    ValidateClip(clip);
                    _physics = new Rac2PhysBoneBakeSession(root);
                }
                _sampler = new Rac2VatAnimationSampler(root, clip, constraints);
                if (constraints) _barrier = new Rac2ConstraintBakeBarrier(root, maximumPasses);
            }
            catch { Dispose(); throw; }
        }

        public IEnumerator WarmUp(float seconds)
        {
            if (_physics == null) yield break;
            int count = Mathf.CeilToInt(Mathf.Clamp(seconds, 0, 10) * 60);
            // First zero-delta evaluation initializes buffers even with warmup=0.
            for (int i = 0; i <= count; i++)
            {
                var step = Evaluate(0, i == 0 ? 0 : 1f / 60f, 0);
                try { while (step.MoveNext()) yield return step.Current; }
                finally { (step as IDisposable)?.Dispose(); }
                if (!Application.isBatchMode && EditorUtility.DisplayCancelableProgressBar(
                    FlareLocalization.Text("Creating RAC2 VAT"), FlareLocalization.Text("Stabilizing the initial PhysBone pose"), i / (float)Mathf.Max(1, count)))
                    throw new OperationCanceledException();
                yield return null;
            }
        }

        public IEnumerator SampleFrame(int frame, int count, bool loop, float seamSeconds)
        {
            if (frame != _nextFrame || count < 2) throw new InvalidOperationException(FlareLocalization.Text("VAT frames must be sampled in order."));
            float interval = _clip.length / (loop ? count : count - 1f);
            float time = Mathf.Min(_clip.length, frame * interval);
            // Uniform substeps within each output interval, never a wall-clock delta or a time seek.
            int steps = _physics != null && frame > 0 ? Mathf.Max(1, Mathf.CeilToInt(interval * 60)) : 1;
            float dt = (time - _lastTime) / steps;
            for (int i = 1; i <= steps; i++)
            {
                var step = Evaluate(_lastTime + (time - _lastTime) * i / steps, dt, frame);
                try { while (step.MoveNext()) yield return step.Current; }
                finally { (step as IDisposable)?.Dispose(); }
                // Do not yield between the last physics result and the caller's BakeMesh.
                if (i < steps) yield return null;
            }
            _lastTime = time;
            _nextFrame++;
            if (_physics != null)
            {
                float duration = Mathf.Min(Mathf.Max(0, seamSeconds), _clip.length * .5f);
                float weight = loop && duration > 0 ? Mathf.SmoothStep(0, 1, (time - (_clip.length - duration)) / duration) : 0;
                _physics.PrepareOutput(weight);
            }
        }

        private IEnumerator Evaluate(float time, float delta, int frame)
        {
            _physics?.ResetInputPose();
            _sampler.Sample(time);
            if (_barrier != null)
            {
                _barrier.BeginFrame();
                while (!_barrier.IsReady(frame)) yield return null;
            }
            _physics?.Step(delta);
        }

        public void RestoreOutput() { _physics?.RestoreOutput(); }

        public void Dispose()
        {
            try { _physics?.Dispose(); }
            finally { try { _barrier?.Dispose(); } finally { _sampler?.Dispose(); } }
            _physics = null; _barrier = null; _sampler = null;
        }
    }
}
