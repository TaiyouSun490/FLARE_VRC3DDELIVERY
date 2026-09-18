using UdonSharp;
using UnityEngine;

namespace AvatarCatalog.Remote
{
    // All components and programs originate in the world, never in downloaded data.
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public sealed class FlareRuntimeNode : UdonSharpBehaviour
    {
        public MeshFilter Filter;
        public MeshRenderer Renderer;
        public BoxCollider Box;
        public AudioSource Audio;
        [HideInInspector] public FlareGimmickInterpreter Interpreter;
        [HideInInspector] public int NodeIndex;
        private bool _moving, _rotating;
        private Vector3 _fromPosition, _toPosition;
        private Quaternion _fromRotation, _toRotation;
        private Vector3 _rotateAxis;
        private float _rotateDegrees;
        private float _moveStart, _moveDuration, _rotateStart, _rotateDuration;
        private int _moveEase, _rotateEase;

        public override void Interact()
        {
            if (Interpreter != null) Interpreter.Dispatch(NodeIndex, "interact");
        }

        public bool Move(Vector3 delta, float duration, int ease, float maximum)
        {
            float now = Time.timeSinceLevelLoad;
            Tick(now);
            // Validate the same sampled pose that starts the new tween, not last frame's pose.
            Vector3 destination = transform.localPosition + delta;
            if (!(destination.x >= -maximum && destination.x <= maximum &&
                  destination.y >= -maximum && destination.y <= maximum &&
                  destination.z >= -maximum && destination.z <= maximum)) return false;
            _fromPosition = transform.localPosition;
            _toPosition = destination;
            _moveStart = now;
            _moveDuration = duration;
            _moveEase = ease;
            _moving = duration > 0f;
            if (!_moving) transform.localPosition = _toPosition;
            return true;
        }

        public void Rotate(Vector3 axis, float degrees, float duration, int ease)
        {
            Tick(Time.timeSinceLevelLoad);
            _fromRotation = transform.localRotation;
            _rotateAxis = axis; _rotateDegrees = degrees;
            _toRotation = _fromRotation * Quaternion.AngleAxis(degrees, axis);
            _rotateStart = Time.timeSinceLevelLoad;
            _rotateDuration = duration;
            _rotateEase = ease;
            _rotating = duration > 0f;
            if (!_rotating) transform.localRotation = _toRotation;
        }

        // Called by the persistent interpreter, including while this node is inactive.
        public bool Tick(float now)
        {
            if (_moving)
            {
                float t = Mathf.Clamp01((now - _moveStart) / _moveDuration);
                transform.localPosition = Vector3.LerpUnclamped(_fromPosition, _toPosition, Ease(t, _moveEase));
                if (t >= 1f) _moving = false;
            }
            if (_rotating)
            {
                float t = Mathf.Clamp01((now - _rotateStart) / _rotateDuration);
                // Preserve the requested direction and full turns, not Quaternion's shortest arc.
                transform.localRotation = _fromRotation * Quaternion.AngleAxis(_rotateDegrees * Ease(t, _rotateEase), _rotateAxis);
                if (t >= 1f) _rotating = false;
            }
            return _moving || _rotating;
        }

        private float Ease(float t, int kind)
        {
            if (kind == 1) return t * t;
            if (kind == 2) return 1f - (1f - t) * (1f - t);
            if (kind == 3) return t * t * (3f - 2f * t);
            return t;
        }

        public void StopRuntime()
        {
            _moving = false;
            _rotating = false;
            if (Audio != null) Audio.Stop();
            Interpreter = null;
        }
    }
}
