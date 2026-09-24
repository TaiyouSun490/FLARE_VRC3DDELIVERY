using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace AvatarCatalog.Remote
{
    /// <summary>Keeps the sampled clip connected while native constraints evaluate between editor ticks.</summary>
    internal sealed class Rac2VatAnimationSampler : IDisposable
    {
        private readonly GameObject _root;
        private readonly AnimationClip _clip;
        private PlayableGraph _graph;
        private Animator _temporaryAnimator;
        private AnimationClipPlayable _playable;

        public Rac2VatAnimationSampler(GameObject root, AnimationClip clip, bool nativeConstraints)
        {
            _root = root;
            _clip = clip;
            if (!nativeConstraints) return;
            try
            {
                var animator = root.GetComponent<Animator>();
                if (animator == null) animator = _temporaryAnimator = root.AddComponent<Animator>();
                _graph = PlayableGraph.Create("RAC2 VAT sampling");
                _graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
                _playable = AnimationClipPlayable.Create(_graph, clip);
                _playable.SetSpeed(0);
                var output = AnimationPlayableOutput.Create(_graph, "VAT", animator);
                output.SetSourcePlayable(_playable);
                _graph.Play();
            }
            catch { Dispose(); throw; }
        }

        public void Sample(float time)
        {
            AnimationMode.BeginSampling();
            try
            {
                if (_graph.IsValid())
                {
                    _playable.SetTime(time);
                    AnimationMode.SamplePlayableGraph(_graph, 0, time);
                }
                else AnimationMode.SampleAnimationClip(_root, _clip, time);
            }
            finally { AnimationMode.EndSampling(); }
        }

        public void Dispose()
        {
            if (_graph.IsValid()) _graph.Destroy();
            if (_temporaryAnimator != null) UnityEngine.Object.DestroyImmediate(_temporaryAnimator);
        }
    }
}
