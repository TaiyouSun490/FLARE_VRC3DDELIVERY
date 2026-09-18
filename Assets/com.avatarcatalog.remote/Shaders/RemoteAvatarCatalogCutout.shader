Shader "Avatar Catalog/RAC1 Opaque Cutout"
{
    Properties
    {
        _Color ("Base Color", Color) = (1,1,1,1)
        _MainTex ("RAC1 Atlas", 2D) = "white" {}
        _Cutoff ("Alpha Cutoff", Range(0,1)) = 0.5
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull", Float) = 2
    }

    SubShader
    {
        Tags
        {
            "Queue" = "AlphaTest"
            "RenderType" = "TransparentCutout"
        }
        LOD 200
        Cull [_Cull]

        CGPROGRAM
        #pragma surface surf Lambert alphatest:_Cutoff addshadow
        #pragma target 3.0

        sampler2D _MainTex;
        fixed4 _Color;

        struct Input
        {
            float2 uv_MainTex;
        };

        void surf(Input input, inout SurfaceOutput output)
        {
            fixed4 sampledColor = tex2D(_MainTex, input.uv_MainTex) * _Color;
            output.Albedo = sampledColor.rgb;
            output.Alpha = sampledColor.a;
        }
        ENDCG
    }

    Fallback "Legacy Shaders/Transparent/Cutout/VertexLit"
}
