using UdonSharp;
using UnityEngine;

namespace AvatarCatalog.Remote
{
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public sealed class Rac2ParticlePlayer : UdonSharpBehaviour
    {
        [Header("Preallocated renderer pool")]
        public GameObject[] PoolObjects;
        public Material AlphaTemplate;
        public Material AdditiveTemplate;

        [HideInInspector] public Mesh ParticleMesh;
        [HideInInspector] public Texture2D ParticleTexture;
        [HideInInspector] public bool Loop;
        [HideInInspector] public bool Billboard;
        [HideInInspector] public int ShaderProfile;
        [HideInInspector] public int MaximumParticles;
        [HideInInspector] public float Duration;
        [HideInInspector] public float Lifetime;
        [HideInInspector] public float EmissionRate;
        [HideInInspector] public float SpeedMinimum;
        [HideInInspector] public float SpeedMaximum;
        [HideInInspector] public float SizeMinimum;
        [HideInInspector] public float SizeMaximum;
        [HideInInspector] public float Gravity;
        [HideInInspector] public float AngularSpeed;
        [HideInInspector] public Vector3 Origin;
        [HideInInspector] public Vector3 Direction = Vector3.up;
        [HideInInspector] public int Shape;
        [HideInInspector] public float ShapeRadius;
        [HideInInspector] public float ShapeAngle;
        [HideInInspector] public Vector3 ShapeScale = Vector3.one;
        [HideInInspector] public Color StartColor = Color.white;
        [HideInInspector] public Color EndColor = new Color(1f, 1f, 1f, 0f);
        [HideInInspector] public int FlipbookColumns = 1;
        [HideInInspector] public int FlipbookRows = 1;
        [HideInInspector] public bool ClipToBooth = true;
        [HideInInspector] public float BoothWidth = 3f;
        [HideInInspector] public float BoothDepth = 3f;
        [HideInInspector] public float BoothHeight = 2.7f;
        [HideInInspector] public float BoothBoundaryTolerance = 0.1f;

        public bool IsConfigured;
        public int ActiveParticleCount;

        private Transform[] _transforms;
        private MeshRenderer[] _renderers;
        private Material[] _materials;
        private Vector3[] _velocities;
        private Vector3[] _origins;
        private float[] _birthTimes;
        private bool _hasColor;
        private bool _hasFrame;
        private bool _hasSpin;
        private float[] _ages;
        private float[] _sizes;
        private float[] _spins;
        private bool[] _active;
        private float _elapsed;
        private float _emissionAccumulator;
        private float _randomState = 0.371f;

        public void ApplyConfiguration()
        {
            ClearParticles();
            if (PoolObjects == null || PoolObjects.Length == 0 || ParticleMesh == null) return;
            Material template = ShaderProfile == 1 ? AdditiveTemplate : AlphaTemplate;
            if (template == null) return;

            int count = MaximumParticles;
            if (count < 1) count = 1;
            if (count > PoolObjects.Length) count = PoolObjects.Length;
            if (count > 32) count = 32;
            MaximumParticles = count;

            _transforms = new Transform[count];
            _renderers = new MeshRenderer[count];
            _materials = new Material[count];
            _velocities = new Vector3[count];
            _origins = new Vector3[count];
            _birthTimes = new float[count];
            _hasColor = template.HasProperty("_Color");
            _hasFrame = template.HasProperty("_Frame");
            _hasSpin = template.HasProperty("_Spin");
            _ages = new float[count];
            _sizes = new float[count];
            _spins = new float[count];
            _active = new bool[count];

            for (int index = 0; index < count; index++)
            {
                GameObject item = PoolObjects[index];
                if (item == null) continue;
                MeshFilter filter = item.GetComponent<MeshFilter>();
                MeshRenderer renderer = item.GetComponent<MeshRenderer>();
                if (filter == null || renderer == null) continue;
                filter.sharedMesh = ParticleMesh;
                renderer.sharedMaterial = template;
                Material material = renderer.material;
                material.name = "RAC2 Particle " + index;
                if (material.HasProperty("_MainTex")) material.SetTexture("_MainTex", ParticleTexture);
                if (material.HasProperty("_Columns")) material.SetFloat("_Columns", FlipbookColumns);
                if (material.HasProperty("_Rows")) material.SetFloat("_Rows", FlipbookRows);
                if (material.HasProperty("_Billboard")) material.SetFloat("_Billboard", Billboard ? 1f : 0f);
                renderer.enabled = false;
                _transforms[index] = item.transform;
                _renderers[index] = renderer;
                _materials[index] = material;
            }

            _elapsed = 0f;
            _emissionAccumulator = 0f;
            ActiveParticleCount = 0;
            IsConfigured = true;
            _randomState = 0.371f;
            if (EmissionRate > 0f) SpawnOne(0f);
        }

        public void ClearParticles()
        {
            if (_renderers != null)
            {
                for (int index = 0; index < _renderers.Length; index++)
                    if (_renderers[index] != null) _renderers[index].enabled = false;
            }
            if (_materials != null)
            {
                for (int index = 0; index < _materials.Length; index++)
                    if (_materials[index] != null) Destroy(_materials[index]);
                _materials = null;
            }
            IsConfigured = false;
            ActiveParticleCount = 0;
            _elapsed = 0f;
            _emissionAccumulator = 0f;
        }

        private void Update()
        {
            if (IsConfigured) SimulateStep(Time.deltaTime);
        }

        public void SimulateStep(float deltaTime)
        {
            if (!IsConfigured || deltaTime <= 0f) return;
            float previousTime = _elapsed;
            _elapsed += deltaTime;
            float emissionEnd = Loop ? _elapsed : Mathf.Min(_elapsed, Duration);
            float emissionStart = Loop ? previousTime : Mathf.Min(previousTime, Duration);
            if (EmissionRate > 0f && emissionEnd > emissionStart)
            {
                _emissionAccumulator += EmissionRate * (emissionEnd - emissionStart);
                int births = Mathf.FloorToInt(_emissionAccumulator + 0.00001f);
                _emissionAccumulator = Mathf.Max(0f, _emissionAccumulator - births);
                // Only the newest pool-sized cohort can survive an arbitrarily long stall.
                int firstBirth = Mathf.Max(0, births - MaximumParticles);
                for (int birth = firstBirth; birth < births; birth++)
                {
                    float birthTime = emissionEnd - (_emissionAccumulator + births - 1 - birth) / EmissionRate;
                    if (_elapsed - birthTime < Lifetime) SpawnOne(birthTime);
                }
            }

            int frames = FlipbookColumns * FlipbookRows;
            if (frames < 1) frames = 1;
            ActiveParticleCount = 0;
            for (int index = 0; index < MaximumParticles; index++)
            {
                if (!_active[index]) continue;
                _ages[index] = Mathf.Max(0f, _elapsed - _birthTimes[index]);
                if (_ages[index] >= Lifetime)
                {
                    _active[index] = false;
                    if (_renderers[index] != null) _renderers[index].enabled = false;
                    continue;
                }

                ActiveParticleCount++;
                Transform item = _transforms[index];
                if (item != null)
                {
                    float age = _ages[index];
                    Vector3 position = _origins[index] + _velocities[index] * age + Vector3.down * (0.5f * Gravity * age * age);
                    item.localPosition = position;
                    float spin = _spins[index] + AngularSpeed * age;
                    if (!Billboard) item.localRotation = Quaternion.Euler(0f, spin, spin * 0.37f);
                    float normalized = _ages[index] / Lifetime;
                    float size = _sizes[index] * (1f - normalized * 0.2f);
                    item.localScale = new Vector3(size, size, size);
                    Material material = _materials[index];
                    if (material != null)
                    {
                        if (_hasColor) material.SetColor("_Color", Color.Lerp(StartColor, EndColor, normalized));
                        if (_hasFrame && frames > 1) material.SetFloat("_Frame", Mathf.Min(frames - 1, Mathf.FloorToInt(normalized * frames)));
                        if (_hasSpin && Billboard) material.SetFloat("_Spin", spin * Mathf.Deg2Rad);
                    }
                    MeshRenderer renderer = _renderers[index];
                    if (renderer != null)
                    {
                        bool visible = ParticleInsideBooth(position);
                        if (renderer.enabled != visible) renderer.enabled = visible;
                    }
                }
                else if (_renderers[index] != null)
                {
                    _renderers[index].enabled = false;
                }
            }
        }

        private void SpawnOne(float birthTime)
        {
            if (!IsConfigured || _active == null) return;
            int slot = -1;
            float oldestBirth = float.MaxValue;
            for (int index = 0; index < MaximumParticles; index++)
            {
                if (!_active[index]) { slot = index; break; }
                // Birth times stay ordered even when several particles spawn in one frame.
                if (_birthTimes[index] < oldestBirth) { oldestBirth = _birthTimes[index]; slot = index; }
            }
            if (slot < 0 || _transforms[slot] == null || _renderers[slot] == null) return;

            Vector3 position = Origin;
            Vector3 direction = Direction.sqrMagnitude > 0.000001f ? Direction.normalized : Vector3.up;
            if (Shape == 1)
            {
                position += RandomUnitVector() * (ShapeRadius * Random01());
            }
            else if (Shape == 2)
            {
                direction = RandomCone(direction, ShapeAngle);
            }
            else if (Shape == 3)
            {
                position += new Vector3((Random01() - 0.5f) * ShapeScale.x,
                                        (Random01() - 0.5f) * ShapeScale.y,
                                        (Random01() - 0.5f) * ShapeScale.z);
            }

            _active[slot] = true;
            _ages[slot] = 0f;
            _birthTimes[slot] = birthTime;
            _origins[slot] = position;
            _sizes[slot] = Mathf.Lerp(SizeMinimum, SizeMaximum, Random01());
            _spins[slot] = Random01() * 360f;
            _velocities[slot] = direction * Mathf.Lerp(SpeedMinimum, SpeedMaximum, Random01());
            Transform item = _transforms[slot];
            item.localPosition = position;
            item.localRotation = Quaternion.Euler(0f, _spins[slot], _spins[slot] * 0.37f);
            item.localScale = Vector3.one * _sizes[slot];
            Material material = _materials[slot];
            if (material != null)
            {
                if (_hasColor) material.SetColor("_Color", StartColor);
                if (_hasFrame) material.SetFloat("_Frame", 0f);
                if (_hasSpin) material.SetFloat("_Spin", _spins[slot] * Mathf.Deg2Rad);
            }
            _renderers[slot].enabled =
                ParticleInsideBooth(item.localPosition);
        }

        private bool ParticleInsideBooth(Vector3 position)
        {
            if (!ClipToBooth) return true;
            float halfWidth = BoothWidth * 0.5f;
            float halfDepth = BoothDepth * 0.5f;
            float tolerance = BoothBoundaryTolerance;
            return
                position.x >= -halfWidth - tolerance &&
                position.x <= halfWidth + tolerance &&
                position.y >= -tolerance &&
                position.y <= BoothHeight + tolerance &&
                position.z >= -halfDepth - tolerance &&
                position.z <= halfDepth + tolerance;
        }

        private Vector3 RandomCone(Vector3 forward, float angle)
        {
            Vector3 side = Vector3.Cross(forward, Mathf.Abs(forward.y) < 0.99f ? Vector3.up : Vector3.right).normalized;
            Vector3 up = Vector3.Cross(side, forward).normalized;
            float radians = angle * Mathf.Deg2Rad;
            float radius = Mathf.Tan(radians) * Mathf.Sqrt(Random01());
            float phase = Random01() * Mathf.PI * 2f;
            return (forward + side * (Mathf.Cos(phase) * radius) + up * (Mathf.Sin(phase) * radius)).normalized;
        }

        private Vector3 RandomUnitVector()
        {
            float z = Random01() * 2f - 1f;
            float phase = Random01() * Mathf.PI * 2f;
            float radius = Mathf.Sqrt(Mathf.Max(0f, 1f - z * z));
            return new Vector3(radius * Mathf.Cos(phase), z, radius * Mathf.Sin(phase));
        }

        private float Random01()
        {
            _randomState = Mathf.Repeat(_randomState * 12.9898f + 78.233f, 43758.5453f);
            return Mathf.Repeat(Mathf.Sin(_randomState) * 43758.5453f, 1f);
        }
    }
}
