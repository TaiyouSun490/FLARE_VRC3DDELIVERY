Shader "Avatar Catalog/RAC2 Particle Additive"
{
    Properties
    {
        _MainTex ("Particle Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)
        _Frame ("Flipbook Frame", Float) = 0
        _Columns ("Flipbook Columns", Float) = 1
        _Rows ("Flipbook Rows", Float) = 1
        _Billboard ("Billboard", Float) = 1
        _Spin ("Billboard Rotation (radians)", Float) = 0
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" }
        Blend SrcAlpha One
        ZWrite Off
        Cull Off
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _MainTex_ST;
            float4 _Color;
            float _Frame;
            float _Columns;
            float _Rows;
            float _Billboard;
            float _Spin;

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct v2f { float4 position : SV_POSITION; float2 uv : TEXCOORD0; };

            v2f vert(appdata input)
            {
                v2f output;
                float4 world;
                if (_Billboard > 0.5)
                {
                    float3 center = mul(unity_ObjectToWorld, float4(0,0,0,1)).xyz;
                    float3 right = normalize(float3(UNITY_MATRIX_I_V._m00, UNITY_MATRIX_I_V._m10, UNITY_MATRIX_I_V._m20));
                    float3 up = normalize(float3(UNITY_MATRIX_I_V._m01, UNITY_MATRIX_I_V._m11, UNITY_MATRIX_I_V._m21));
                    float sx = length(unity_ObjectToWorld._m00_m10_m20);
                    float sy = length(unity_ObjectToWorld._m01_m11_m21);
                    float sine, cosine;
                    sincos(_Spin, sine, cosine);
                    float2 rotated = float2(cosine * input.vertex.x - sine * input.vertex.y,
                                            sine * input.vertex.x + cosine * input.vertex.y);
                    world = float4(center + right * rotated.x * sx + up * rotated.y * sy, 1);
                    output.position = mul(UNITY_MATRIX_VP, world);
                }
                else output.position = UnityObjectToClipPos(input.vertex);

                float columns = max(1, floor(_Columns + 0.5));
                float rows = max(1, floor(_Rows + 0.5));
                float frame = clamp(floor(_Frame + 0.5), 0, columns * rows - 1);
                float2 tile = float2(fmod(frame, columns), rows - 1 - floor(frame / columns));
                output.uv = (TRANSFORM_TEX(input.uv, _MainTex) + tile) / float2(columns, rows);
                return output;
            }

            fixed4 frag(v2f input) : SV_Target
            {
                fixed4 color = tex2D(_MainTex, input.uv) * _Color;
                clip(color.a - 0.003);
                return color;
            }
            ENDCG
        }
    }
    Fallback Off
}
