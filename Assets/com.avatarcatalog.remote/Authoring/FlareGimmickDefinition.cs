using System;
using UnityEngine;

namespace AvatarCatalog.Remote
{
    // Authoring only; the exporter writes data, never this component/program.
    public sealed class FlareGimmickDefinition : MonoBehaviour
    {
        public string GimmickId;
        public string On = "interact";
        public FlareDeclarativeAction[] Actions = new FlareDeclarativeAction[0];
    }
    public enum FlareActionKind { rotate, move, toggleActive, setActive, playAudio, stopAudio, emitEvent }
    public enum FlareActionAxis { x, y, z }
    public enum FlareActionEase { linear, easeIn, easeOut, easeInOut }
    [Serializable]
    public sealed class FlareDeclarativeAction
    {
        public FlareActionKind Type;
        [Tooltip("Empty means this node. Must belong to the exported hierarchy.")]
        public Transform Target;
        public FlareActionAxis Axis = FlareActionAxis.y;
        public Vector3 Move;
        [Tooltip("Degrees for rotate, 0/1 for setActive, 0..1 volume for audio.")]
        public float Value = 90;
        [Range(0, 30)] public float Duration = 0.5f;
        public FlareActionEase Easing = FlareActionEase.easeInOut;
        public string ClipId = "confirm";
        public string Event = "open";
    }
}
