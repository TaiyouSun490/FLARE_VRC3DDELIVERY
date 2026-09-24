using System;
using System.Collections;
using UnityEditor;
using UnityEngine;

namespace AvatarCatalog.Remote
{
    /// <summary>Runs a bake across editor/player-loop updates, never by spinning synchronously.</summary>
    public sealed class Rac2EditorBakeRunner : IDisposable
    {
        private IEnumerator _steps;
        private readonly Action<Exception> _finished;

        public Rac2EditorBakeRunner(IEnumerator steps, Action<Exception> finished)
        {
            _steps = steps ?? throw new ArgumentNullException(nameof(steps));
            _finished = finished;
            EditorApplication.update += Tick;
            AssemblyReloadEvents.beforeAssemblyReload += Dispose;
            EditorApplication.quitting += Dispose;
        }

        private void Tick()
        {
            if (_steps == null) return;
            if (EditorApplication.isCompiling || EditorApplication.isPlayingOrWillChangePlaymode)
            {
                Finish(new OperationCanceledException("Bake cancelled because the Editor changed mode."));
                return;
            }
            if (EditorApplication.isUpdating) return;
            bool more;
            try
            {
                more = _steps.MoveNext();
            }
            catch (Exception error) { Finish(error); return; }
            // Invoke completion outside the catch so callback failures never invoke it twice.
            if (!more) { Finish(null); return; }
            // Native Unity constraints run in the player loop, not in SampleAnimationClip.
            EditorApplication.QueuePlayerLoopUpdate();
        }

        private void Finish(Exception error)
        {
            try { Dispose(); }
            catch (Exception cleanupError) { error = error ?? cleanupError; }
            _finished?.Invoke(error);
        }

        public void Dispose()
        {
            EditorApplication.update -= Tick;
            AssemblyReloadEvents.beforeAssemblyReload -= Dispose;
            EditorApplication.quitting -= Dispose;
            var steps = _steps;
            _steps = null;
            (steps as IDisposable)?.Dispose();
        }
    }
}
