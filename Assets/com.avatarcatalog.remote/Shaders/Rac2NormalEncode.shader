Shader "Hidden/Avatar Catalog/RAC2 Normal Encode"
{
    Properties
    {
        _MainTex ("Normal", 2D) = "bump" {}
        _SourceIsNormalMap ("Source Is Imported Normal", Float) = 0
    }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float _SourceIsNormalMap;

            fixed4 frag(v2f_img input) : SV_Target
            {
                fixed4 source = tex2D(_MainTex, input.uv);
                half3 normal = _SourceIsNormalMap > 0.5
                    ? UnpackNormal(source)
                    : normalize(source.xyz * 2.0h - 1.0h);
                return fixed4(1.0h, normal.y * 0.5h + 0.5h, normal.z * 0.5h + 0.5h, normal.x * 0.5h + 0.5h);
            }
            ENDCG
        }
    }
    Fallback Off
}

