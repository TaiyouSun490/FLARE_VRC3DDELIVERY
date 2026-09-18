Shader "Avatar Catalog/RAC2 VAT Cutout"
{
    Properties
    {
        _Color ("Base Color", Color) = (1,1,1,1)
        _MainTex ("Main Texture", 2D) = "white" {}
        _BumpMap ("Normal Map", 2D) = "bump" {}
        _UseBumpMap ("Use Normal Map", Float) = 0
        _BumpScale ("Normal Strength", Float) = 1
        _Cutoff ("Alpha Cutoff", Range(0,1)) = 0.5
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull", Float) = 2
        [HideInInspector] _VatPositionTex ("VAT Position", 2D) = "black" {}
        [HideInInspector] _VatNormalTex ("VAT Normal", 2D) = "gray" {}
        [HideInInspector] _VatFrameCount ("VAT Frames", Float) = 2
        [HideInInspector] _VatRowsPerFrame ("VAT Rows", Float) = 1
        [HideInInspector] _VatTextureHeight ("VAT Height", Float) = 2
        [HideInInspector] _VatFps ("VAT FPS", Float) = 30
        [HideInInspector] _VatSpeed ("VAT Speed", Float) = 1
        [HideInInspector] _VatLoop ("VAT Loop", Float) = 1
        [HideInInspector] _VatHasNormal ("VAT Has Normal", Float) = 0
        [HideInInspector] _VatStartTime ("VAT Start", Float) = 0
        [HideInInspector] _VatBoundsMin ("VAT Bounds Min", Vector) = (0,0,0,0)
        [HideInInspector] _VatBoundsSize ("VAT Bounds Size", Vector) = (1,1,1,0)
    }

    SubShader
    {
        Tags { "Queue" = "AlphaTest" "RenderType" = "TransparentCutout" }
        LOD 250
        Cull [_Cull]

        CGPROGRAM
        #pragma target 3.5
        #pragma surface surf Lambert vertex:vert alphatest:_Cutoff addshadow

        sampler2D _MainTex;
        sampler2D _BumpMap;
        sampler2D _VatPositionTex;
        sampler2D _VatNormalTex;
        fixed4 _Color;
        float _UseBumpMap;
        float _BumpScale;
        float _VatFrameCount;
        float _VatRowsPerFrame;
        float _VatTextureHeight;
        float _VatFps;
        float _VatSpeed;
        float _VatLoop;
        float _VatHasNormal;
        float _VatStartTime;
        float4 _VatBoundsMin;
        float4 _VatBoundsSize;

        float2 VatUv(float x, float row, float frame)
        {
            return float2(x, (frame * _VatRowsPerFrame + row + 0.5) / _VatTextureHeight);
        }

        void vert(inout appdata_full v)
        {
            float count = max(2.0, _VatFrameCount);
            float elapsed = max(0.0, (_Time.y - _VatStartTime) * _VatFps * _VatSpeed);
            float frameValue = _VatLoop > 0.5 ? fmod(elapsed, count) : min(elapsed, count - 1.0);
            float frame0 = floor(frameValue);
            float frame1 = _VatLoop > 0.5 ? fmod(frame0 + 1.0, count) : min(frame0 + 1.0, count - 1.0);
            float blend = frac(frameValue);
            float2 uv0 = VatUv(v.texcoord1.x, v.texcoord1.y, frame0);
            float2 uv1 = VatUv(v.texcoord1.x, v.texcoord1.y, frame1);
            float3 p0 = tex2Dlod(_VatPositionTex, float4(uv0, 0, 0)).rgb;
            float3 p1 = tex2Dlod(_VatPositionTex, float4(uv1, 0, 0)).rgb;
            v.vertex.xyz = _VatBoundsMin.xyz + lerp(p0, p1, blend) * _VatBoundsSize.xyz;
            if (_VatHasNormal > 0.5)
            {
                float3 n0 = tex2Dlod(_VatNormalTex, float4(uv0, 0, 0)).xyz * 2.0 - 1.0;
                float3 n1 = tex2Dlod(_VatNormalTex, float4(uv1, 0, 0)).xyz * 2.0 - 1.0;
                v.normal = normalize(lerp(n0, n1, blend));
            }
        }

        struct Input
        {
            float2 uv_MainTex;
            float2 uv_BumpMap;
        };

        void surf(Input input, inout SurfaceOutput output)
        {
            fixed4 color = tex2D(_MainTex, input.uv_MainTex) * _Color;
            output.Albedo = color.rgb;
            output.Alpha = color.a;
            if (_UseBumpMap > 0.5)
            {
                float3 normal = UnpackNormal(tex2D(_BumpMap, input.uv_BumpMap));
                normal.xy *= _BumpScale;
                output.Normal = normalize(normal);
            }
        }
        ENDCG
    }

    Fallback "Legacy Shaders/Transparent/Cutout/VertexLit"
}
