Shader "Custom/PointCloud_GatherComposite"
{
    // 集め直し方式（compute版）の合成パス用シェーダ（全画面1枚）。
    // computeが書いた結果（色＋半径, 深度）をカメラの色と深度へ書き写す。円に覆われない画素は書かない。
    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" }
        ZWrite On
        ZTest LEqual
        Blend Off
        Cull Off

        Pass
        {
            HLSLPROGRAM
            #pragma vertex   vert
            #pragma fragment frag
            #pragma target 4.5
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            // computeの結果（x=色＋半径を詰めた値。0は円に覆われない / y=深度のビット列）
            Texture2D<uint2> _GatherResult;

            static const uint MASK8 = 0xFFu;

            struct Varyings
            {
                float4 posCS : SV_POSITION;
            };

            struct FragOut
            {
                half4 color : SV_Target;
                float depth : SV_Depth;
            };

            // 画面全体を覆う三角形1枚（頂点番号から位置を作る）
            Varyings vert(uint svVertexID : SV_VertexID)
            {
                Varyings o;
                float2 uv = float2((svVertexID << 1) & 2, svVertexID & 2);
                o.posCS = float4(uv * 2.0 - 1.0, UNITY_NEAR_CLIP_VALUE, 1.0);
                return o;
            }

            FragOut frag(Varyings i)
            {
                uint2 result = _GatherResult.Load(int3(int2(i.posCS.xy), 0));

                // 円に覆われない画素は書かない
                if (result.x == 0u) discard;

                FragOut o;
                o.color = half4(result.x & MASK8, (result.x >> 8) & MASK8, (result.x >> 16) & MASK8, 255) / 255.0h;
                o.depth = asfloat(result.y);
                return o;
            }
            ENDHLSL
        }
    }
    FallBack Off
}
