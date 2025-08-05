Shader "Custom/PointCloud_Graphics"
{
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }
        Pass
        {
            ZWrite On
            ZTest LEqual
            Cull Off

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            StructuredBuffer<float3> _Positions;
            StructuredBuffer<uint> _Colors;

            struct v2f
            {
                float4 pos : SV_POSITION;
                float4 col : COLOR0;
            };

            float4 DecodeColor(uint c)
            {
                float r = (c & 0xFF) / 255.0;
                float g = ((c >> 8) & 0xFF) / 255.0;
                float b = ((c >> 16) & 0xFF) / 255.0;
                float a = ((c >> 24) & 0xFF) / 255.0;

                // sRGB -> Linear (ÉKÉìÉ}ï‚ê≥)
                r = pow(r, 2.2);
                g = pow(g, 2.2);
                b = pow(b, 2.2);

                return float4(r, g, b, a);
            }

            v2f vert(uint vid : SV_VertexID)
            {
                v2f o;
                float3 p = _Positions[vid];
                o.pos = mul(UNITY_MATRIX_VP, float4(p, 1));
                o.col = DecodeColor(_Colors[vid]);
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                return i.col;
            }
            ENDHLSL
        }
    }
}
