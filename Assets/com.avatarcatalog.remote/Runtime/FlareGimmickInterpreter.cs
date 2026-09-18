using UdonSharp;
using UnityEngine;
using VRC.SDK3.Data;

namespace AvatarCatalog.Remote
{
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public sealed class FlareGimmickInterpreter : UdonSharpBehaviour
    {
        [Header("World-owned audio allowlist (no downloaded clips or URLs)")]
        public string[] AudioIds;
        public AudioClip[] AudioClips;
        [Range(0f, 1f)] public float MaximumVolume = 0.5f;
        [Range(1, 128)] public int MaximumActions = 64;
        [Range(1, 64)] public int MaximumEventsPerDispatch = 32;
        [Range(1f, 100f)] public float MaximumLocalPosition = 20f;
        public FlareRuntimeNode[] Nodes;
        public string[] Ids;
        public bool Ready;
        public int IgnoredActions;
        public int ExecutedActions;
        public int DroppedEvents;
        public string LastError;
        public int ActiveTweens;
        public int TickCalls;
        public int ActionVisits;
        private int[] _targets, _ops, _nextAction, _emitGroups;
        private string[] _emitted;
        private AudioClip[] _clips;
        private int[] _groupSources, _groupHeads, _groupTails;
        private string[] _groupEvents;
        private int _groupCount;
        private int[] _activeNodes;
        private bool[] _active;
        private Vector3[] _vectors;
        private float[] _values, _durations;
        private int[] _eases;
        private int _count;
        private int[] _queue = new int[64];
        private bool _dispatching;
        private float _rateWindow;
        private int _dispatchCount;

        public void Clear()
        {
            Ready = false;
            if (Nodes != null) for (int i = 0; i < Nodes.Length; i++) if (Nodes[i] != null) Nodes[i].StopRuntime();
            Nodes = null; Ids = null; _count = 0;
            _targets = null; _ops = null; _nextAction = null; _emitGroups = null;
            _emitted = null; _clips = null; _vectors = null; _values = null; _durations = null; _eases = null;
            _groupSources = null; _groupHeads = null; _groupTails = null; _groupEvents = null; _groupCount = 0;
            _activeNodes = null; _active = null; ActiveTweens = 0;
            _dispatching = false; _dispatchCount = 0;
            _rateWindow = Time.timeSinceLevelLoad;
        }

        public bool Configure(FlareRuntimeNode[] nodes, DataList definitions)
        {
            Clear();
            LastError = ""; IgnoredActions = 0; ExecutedActions = 0; DroppedEvents = 0;
            TickCalls = 0; ActionVisits = 0;
            if (nodes == null || definitions == null || nodes.Length == 0 || nodes.Length > 32 || nodes.Length != definitions.Count)
                return Fail("Invalid runtime node table.");
            Nodes = nodes;
            int limit = Mathf.Clamp(MaximumActions, 1, 128);
            Ids = new string[nodes.Length];
            _targets = new int[limit]; _ops = new int[limit]; _nextAction = new int[limit]; _emitGroups = new int[limit];
            _emitted = new string[limit]; _clips = new AudioClip[limit];
            _groupSources = new int[limit]; _groupHeads = new int[limit]; _groupTails = new int[limit]; _groupEvents = new string[limit];
            _activeNodes = new int[nodes.Length]; _active = new bool[nodes.Length];
            _vectors = new Vector3[limit]; _values = new float[limit];
            _durations = new float[limit]; _eases = new int[limit];
            for (int n = 0; n < nodes.Length; n++)
            {
                if (nodes[n] == null) return Fail("Missing runtime node.");
                DataDictionary gimmick = Gimmick(definitions[n]);
                Ids[n] = gimmick == null ? "" : Text(gimmick, "gimmickId", "");
                if (Ids[n].Length > 64) return Fail("gimmickId exceeds 64 characters.");
                if (Ids[n].Length > 0)
                    for (int j = 0; j < n; j++) if (Ids[j] == Ids[n]) return Fail("Duplicate gimmickId: " + Ids[n]);
                nodes[n].Interpreter = this; nodes[n].NodeIndex = n;
            }
            int declared = 0;
            for (int n = 0; n < nodes.Length; n++)
            {
                DataDictionary gimmick = Gimmick(definitions[n]);
                if (gimmick == null || (!gimmick.ContainsKey("bindings") && !gimmick.ContainsKey("on") && !gimmick.ContainsKey("actions"))) continue;
                // One binding or an array of bindings; IDs always belong to the node.
                DataList bindings = new DataList();
                if (gimmick.TryGetValue("bindings", TokenType.DataList, out DataToken list)) bindings = list.DataList;
                else bindings.Add(gimmick);
                if (bindings.Count > limit) return Fail("Too many event bindings.");
                for (int b = 0; b < bindings.Count; b++)
                {
                    if (bindings[b].TokenType != TokenType.DataDictionary) return Fail("Invalid binding.");
                    DataDictionary binding = bindings[b].DataDictionary;
                    string eventName = Text(binding, "on", "");
                    if (eventName.Length == 0 || eventName.Length > 64) return Fail("Invalid event name.");
                    if (!binding.TryGetValue("actions", TokenType.DataList, out DataToken actions)) continue;
                    declared += actions.DataList.Count;
                    if (declared > limit) return Fail("Action count exceeds policy.");
                    for (int a = 0; a < actions.DataList.Count; a++)
                    {
                        DataToken item = actions.DataList[a];
                        if (item.TokenType != TokenType.DataDictionary) { IgnoredActions++; continue; }
                        DataDictionary action = item.DataDictionary;
                        string type = Text(action, "type", "");
                        int op = type == "rotate" ? 1 : type == "move" ? 2 : type == "toggleActive" ? 3 :
                            type == "setActive" ? 4 : type == "playAudio" ? 5 : type == "stopAudio" ? 6 : type == "emitEvent" ? 7 : 0;
                        if (op == 0)
                        { IgnoredActions++; continue; }
                        int target = n;
                        if (action.ContainsKey("target")) target = FindId(Text(action, "target", ""));
                        float duration = Scalar(action, "duration", 0f);
                        float value = Scalar(action, "value", type == "playAudio" ? 1f : 0f);
                        if (type == "setActive" && action.TryGetValue("value", TokenType.Boolean, out DataToken activeValue)) value = activeValue.Boolean ? 1f : 0f;
                        Vector3 vector = Vector3.zero;
                        if (type == "move")
                        {
                            if (!action.TryGetValue("value", TokenType.DataList, out DataToken xyz) || xyz.DataList.Count != 3)
                            { IgnoredActions++; continue; }
                            vector = new Vector3(Number(xyz.DataList[0]), Number(xyz.DataList[1]), Number(xyz.DataList[2]));
                            value = 0f;
                        }
                        if (type == "rotate")
                        {
                            string axis = Text(action, "axis", "y");
                            if (axis != "x" && axis != "y" && axis != "z") { IgnoredActions++; continue; }
                            vector = axis == "x" ? Vector3.right : axis == "y" ? Vector3.up : Vector3.forward;
                        }
                        string emitted = Text(action, "event", "");
                        if (target < 0 || !Finite(duration, 30f) || duration < 0f || !Finite(value, 360f) ||
                            !Finite(vector.x, 100f) || !Finite(vector.y, 100f) || !Finite(vector.z, 100f) ||
                            type == "emitEvent" && (emitted.Length == 0 || emitted.Length > 64))
                        { IgnoredActions++; continue; }
                        if (op == 4 && value != 0f && value != 1f) { IgnoredActions++; continue; }
                        AudioClip clip = null;
                        if (op == 5)
                        {
                            string id = Text(action, "clip", "");
                            if (AudioIds != null && AudioClips != null)
                                for (int c = 0; c < AudioIds.Length && c < AudioClips.Length; c++)
                                    if (AudioIds[c] == id && AudioClips[c] != null) { clip = AudioClips[c]; break; }
                            if (clip == null) { IgnoredActions++; continue; }
                        }
                        int group = FindGroup(n, eventName);
                        if (group < 0)
                        {
                            group = _groupCount++;
                            _groupSources[group] = n; _groupEvents[group] = eventName; _groupHeads[group] = _count;
                        }
                        else _nextAction[_groupTails[group]] = _count;
                        _groupTails[group] = _count; _nextAction[_count] = -1;
                        _targets[_count] = target; _ops[_count] = op;
                        _vectors[_count] = vector; _values[_count] = value; _durations[_count] = duration;
                        _clips[_count] = clip; _emitted[_count] = emitted;
                        string easing = Text(action, "easing", "linear");
                        _eases[_count] = easing == "easeIn" ? 1 : easing == "easeOut" ? 2 : easing == "easeInOut" ? 3 : 0;
                        _count++;
                    }
                }
            }
            for (int a = 0; a < _count; a++) _emitGroups[a] = _ops[a] == 7 ? FindGroup(_targets[a], _emitted[a]) : -1;
            _emitted = null;
            Ready = true;
            return true;
        }

        public int FindId(string id)
        {
            if (string.IsNullOrEmpty(id) || Ids == null) return -1;
            for (int n = 0; n < Ids.Length; n++) if (Ids[n] == id) return n;
            return -1;
        }

        public void Dispatch(int source, string eventName)
        {
            if (!Ready || _dispatching || source < 0 || source >= Nodes.Length) return;
            int first = FindGroup(source, eventName);
            if (first < 0) return;
            float now = Time.timeSinceLevelLoad;
            if (now - _rateWindow >= 1f) { _rateWindow = now; _dispatchCount = 0; }
            if (++_dispatchCount > 16) { DroppedEvents++; return; }
            _dispatching = true;
            int count = 1;
            int work = 0;
            int budget = Mathf.Clamp(MaximumEventsPerDispatch, 1, 64);
            _queue[0] = first;
            // Breadth-first, finite transaction: cycles cannot recurse or survive into next frame.
            for (int head = 0; head < count; head++)
            {
                for (int a = _groupHeads[_queue[head]]; a >= 0; a = _nextAction[a])
                {
                    if (++work > 128) { DroppedEvents++; _dispatching = false; return; }
                    ActionVisits++;
                    FlareRuntimeNode target = Nodes[_targets[a]];
                    if (target == null) continue;
                    int op = _ops[a];
                    if (op == 7)
                    {
                        if (_emitGroups[a] < 0) continue;
                        if (count >= budget) { DroppedEvents++; continue; }
                        _queue[count++] = _emitGroups[a];
                    }
                    else if (op == 1)
                    {
                        target.Rotate(_vectors[a], _values[a], _durations[a], _eases[a]);
                        if (_durations[a] > 0f) TrackTween(_targets[a]);
                    }
                    else if (op == 2)
                    {
                        if (!target.Move(_vectors[a], _durations[a], _eases[a], Mathf.Clamp(MaximumLocalPosition, 1f, 100f)))
                        { IgnoredActions++; continue; }
                        if (_durations[a] > 0f) TrackTween(_targets[a]);
                    }
                    else if (op == 3) target.gameObject.SetActive(!target.gameObject.activeSelf);
                    else if (op == 4) target.gameObject.SetActive(_values[a] != 0f);
                    else if (op == 6) { if (target.Audio != null) target.Audio.Stop(); }
                    else if (op == 5 && target.Audio != null)
                    {
                        target.Audio.clip = _clips[a];
                        target.Audio.volume = Mathf.Clamp01(_values[a]) * Mathf.Clamp01(MaximumVolume);
                        target.Audio.Play();
                    }
                    ExecutedActions++;
                }
            }
            _dispatching = false;
        }

        private void Update()
        {
            if (!Ready || ActiveTweens == 0) return;
            float now = Time.timeSinceLevelLoad;
            for (int i = ActiveTweens - 1; i >= 0; i--)
            {
                int n = _activeNodes[i];
                TickCalls++;
                if (Nodes[n] != null && Nodes[n].Tick(now)) continue;
                _active[n] = false;
                _activeNodes[i] = _activeNodes[--ActiveTweens];
            }
        }
        private void TrackTween(int node)
        {
            if (_active[node]) return;
            _active[node] = true; _activeNodes[ActiveTweens++] = node;
        }
        private int FindGroup(int source, string eventName)
        {
            for (int g = 0; g < _groupCount; g++)
                if (_groupSources[g] == source && _groupEvents[g] == eventName) return g;
            return -1;
        }
        private DataDictionary Gimmick(DataToken node)
        {
            if (node.TokenType != TokenType.DataDictionary ||
                !node.DataDictionary.TryGetValue("extras", TokenType.DataDictionary, out DataToken extras) ||
                !extras.DataDictionary.TryGetValue("vrc_gimmick", TokenType.DataDictionary, out DataToken g)) return null;
            return g.DataDictionary;
        }
        private string Text(DataDictionary d, string key, string fallback)
        { return d.TryGetValue(key, TokenType.String, out DataToken t) ? t.String : fallback; }
        private float Scalar(DataDictionary d, string key, float fallback)
        { return d.TryGetValue(key, out DataToken t) ? Number(t) : fallback; }
        private float Number(DataToken t) { return t.IsNumber ? (float)t.Number : float.NaN; }
        private bool Finite(float x, float max) { return x >= -max && x <= max; }
        private bool Fail(string error) { LastError = error; Ready = false; return false; }
    }
}
