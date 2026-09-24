#ifndef FLARE_RECONSTRUCTION_INCLUDED
#define FLARE_RECONSTRUCTION_INCLUDED
// This insertion runs after lil_common and before the pass/appdata includes.
float _RacRevealEnabled, _RacRevealStart, _RacRevealDuration, _RacRevealProgressOverride;
float _RacRevealCellSize, _RacRevealScatter, _RacRevealEdgeWidth, _RacRevealInflation;
float4 _RacRevealEdgeColor, _RacRevealHeight;
float4x4 _RacRevealObjectToRoot;
float _RacVatEnabled, _VatFrameCount, _VatRowsPerFrame, _VatTextureHeight;
float _VatFps, _VatSpeed, _VatLoop, _VatHasNormal, _VatStartTime;
float4 _VatBoundsMin, _VatBoundsSize;
sampler2D _VatPositionTex, _VatNormalTex;

float FlareProgress()
{
    if (_RacRevealEnabled < 0.5 || _RacRevealDuration <= 0) return 1;
    return _RacRevealProgressOverride >= 0 ? saturate(_RacRevealProgressOverride) : saturate((_Time.y - _RacRevealStart) / _RacRevealDuration);
}

float FlareHash(float3 cell)
{
    cell = frac(cell * float3(0.1031, 0.1030, 0.0973));
    cell += dot(cell, cell.yxz + 33.33);
    return frac((cell.x + cell.y) * cell.z);
}

float FlareDistance(float3 position, float progress)
{
    float cellSize = max(0.005, _RacRevealCellSize);
    float3 cell = floor(position / cellSize);
    float padding = cellSize * (1 + _RacRevealScatter);
    float front = lerp(_RacRevealHeight.x - padding, _RacRevealHeight.y + padding, progress);
    float threshold = (cell.y + 0.5 + (FlareHash(cell) - 0.5) * _RacRevealScatter) * cellSize;
    return front - threshold;
}

float FlareBand(float distance)
{
    return 1 - smoothstep(0, max(0.001, _RacRevealEdgeWidth), abs(distance));
}

float FlareClip(float3 position)
{
    float progress = FlareProgress();
    if (progress >= 1) return 0;
    if (progress <= 0) clip(-1);
    float distance = FlareDistance(position, progress);
    clip(distance);
    float3 cellEdge = abs(frac(position / max(0.005, _RacRevealCellSize)) - 0.5);
    float grid = smoothstep(0.43, 0.49, max(cellEdge.x, max(cellEdge.y, cellEdge.z)));
    return FlareBand(distance) * (0.5 + 0.5 * grid);
}

void FlareVertex(inout float4 position, inout float3 normal, float2 vatUV)
{
    if (_RacVatEnabled > 0.5)
    {
        float count = max(2, _VatFrameCount);
        float elapsed = max(0, (_Time.y - _VatStartTime) * _VatFps * _VatSpeed);
        float frame = _VatLoop > 0.5 ? fmod(elapsed, count) : min(elapsed, count - 1);
        float first = floor(frame), second = _VatLoop > 0.5 ? fmod(first + 1, count) : min(first + 1, count - 1);
        float2 uv0 = float2(vatUV.x, (first * _VatRowsPerFrame + vatUV.y + 0.5) / max(1, _VatTextureHeight));
        float2 uv1 = float2(vatUV.x, (second * _VatRowsPerFrame + vatUV.y + 0.5) / max(1, _VatTextureHeight));
        position.xyz = _VatBoundsMin.xyz + lerp(tex2Dlod(_VatPositionTex, float4(uv0,0,0)).rgb, tex2Dlod(_VatPositionTex, float4(uv1,0,0)).rgb, frac(frame)) * _VatBoundsSize.xyz;
        if (_VatHasNormal > 0.5)
            normal = normalize(lerp(tex2Dlod(_VatNormalTex, float4(uv0,0,0)).xyz, tex2Dlod(_VatNormalTex, float4(uv1,0,0)).xyz, frac(frame)) * 2 - 1);
    }
}

void FlareInflate(inout float4 position, float3 normal)
{
    float progress = FlareProgress();
    if (progress <= 0 || progress >= 1 || _RacRevealInflation <= 0) return;
    float3 rootPosition = mul(_RacRevealObjectToRoot, position).xyz;
    float rootNormalLength = max(0.001, length(mul((float3x3)_RacRevealObjectToRoot, normal)));
    position.xyz += normal / rootNormalLength * _RacRevealInflation * FlareBand(FlareDistance(rootPosition, progress));
}

// Keep an uninflated, VAT-deformed coordinate for a consistent per-exhibit voxel mask.
static float3 flareUninflatedOS;
#define LIL_CUSTOM_VERTEX_OS FlareVertex(positionOS, input.normalOS, input.uv1); flareUninflatedOS = positionOS.xyz; FlareInflate(positionOS, input.normalOS);
#define LIL_CUSTOM_V2F_MEMBER(id0,id1,id2,id3,id4,id5,id6,id7) float3 flarePosition : TEXCOORD##id0;
#define LIL_CUSTOM_VERT_COPY LIL_V2F_OUT_BASE.flarePosition = mul(_RacRevealObjectToRoot, float4(flareUninflatedOS, 1)).xyz;
// Also runs in shadow/depth passes, preventing a complete shadow before the object appears.
#define BEFORE_UNPACK_V2F float flareEdge = FlareClip(input.flarePosition);
#if !defined(LIL_PASS_FORWARDADD)
    #define BEFORE_OUTPUT fd.col.rgb += _RacRevealEdgeColor.rgb * flareEdge;
#endif
#endif
