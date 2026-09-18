Shader "Hidden/Avatar Catalog/RAC1 Booth Floor Guide"
{
    Properties
    {
        _Color ("Floor Fill", Color) = (0.04, 0.55, 1.0, 0.10)
        _GridColor ("Grid", Color) = (0.10, 0.75, 1.0, 0.42)
        _BorderColor ("Border", Color) = (0.15, 0.95, 1.0, 0.85)
        _FrontColor ("Customer Side", Color) = (1.0, 0.65, 0.08, 0.95)
        _GridDivisions ("Grid Divisions", Float) = 6
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Transparent+50"
            "RenderType" = "Transparent"
            "IgnoreProjector" = "True"
            "PreviewType" = "Plane"
        }

        Pass
        {
            Cull Off
            ZWrite Off
            ZTest LEqual
            Blend SrcAlpha OneMinusSrcAlpha
            Offset -1, -1

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float4 position : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            fixed4 _Color;
            fixed4 _GridColor;
            fixed4 _BorderColor;
            fixed4 _FrontColor;
            float _GridDivisions;

            v2f vert(appdata input)
            {
                v2f output;
                output.position = UnityObjectToClipPos(input.vertex);
                output.uv = input.uv;
                return output;
            }

            fixed4 frag(v2f input) : SV_Target
            {
                float2 uv = saturate(input.uv);
                float divisions = max(_GridDivisions, 1.0);

                float2 gridCoordinate = uv * divisions;
                float2 gridWidth = max(fwidth(gridCoordinate), 0.0001);
                float2 gridDistance = abs(frac(gridCoordinate - 0.5) - 0.5) / gridWidth;
                float grid = 1.0 - saturate(min(gridDistance.x, gridDistance.y));

                float edgeDistance = min(min(uv.x, 1.0 - uv.x), min(uv.y, 1.0 - uv.y));
                float edgeWidth = max(fwidth(edgeDistance) * 1.5, 0.0005);
                float border = 1.0 - smoothstep(0.0, edgeWidth, edgeDistance);

                float axisDistance = min(abs(uv.x - 0.5), abs(uv.y - 0.5));
                float axisWidth = max(fwidth(axisDistance) * 1.25, 0.0005);
                float axis = 1.0 - smoothstep(0.0, axisWidth, axisDistance);

                float front = border * smoothstep(0.94, 0.995, uv.y);
                fixed4 color = lerp(_Color, _GridColor, saturate(grid + axis * 0.45));
                color = lerp(color, _BorderColor, border);
                color = lerp(color, _FrontColor, front);
                return color;
            }
            ENDCG
        }
    }

    Fallback Off
}
