Shader "Custom/PointCloud_Quad"
{
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" "RenderPipeline"="UniversalRenderPipeline" }
        ZWrite On
        ZTest LEqual
        Blend Off
        Cull Off

        Pass
        {
            HLSLPROGRAM
            #pragma vertex   vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            StructuredBuffer<float3> _Positions;
            StructuredBuffer<uint>   _Colors;
            float _PointSize;

            // Quad Triangleの頂点 組み合わせ
            static const uint QUAD_INDEX[6] = { 0,1,2, 2,1,3 };

            // Quad四隅のオフセット係数
            static const half2 OFFS4[4] = {
                half2(-1,-1), half2(+1,-1),
                half2(-1,+1), half2(+1,+1)
            };

            // Color32をRGBAに変換
            inline half4 DecodeColor(uint c)
            {
                uint r = ( c        & 0xFFu);
                uint g = ((c >> 8 ) & 0xFFu);
                uint b = ((c >> 16) & 0xFFu);
                uint a = ((c >> 24) & 0xFFu);
                return half4(r, g, b, a) / 255;
            }

            struct Varyings {
                float4 posCS : SV_POSITION;
                half4  col   : COLOR;
            };

            // SV_VertexID :  現在処理している頂点列の中での通し番号（インデックスバッファ）
            // SV_InstanceID : 現在処理しているインスタンスが何番目か
            Varyings vert(uint svVertexID : SV_VertexID, uint svInstanceID : SV_InstanceID)
            {
                Varyings o;

                // Quadのどの位置かを「0～3」で取得
                const uint cornerID = QUAD_INDEX[svVertexID];

                // 点群バッファから指定の点のPositionを取得
                float3 posWS = _Positions[svInstanceID];

                // ワールド座標をクリップ空間に変換
                float4 pc    = TransformWorldToHClip(posWS);

                // NDC座標における1ピクセルのサイズを取得
                float2 ndcPerPx = 2.0 / _ScreenParams.xy;

                // Quadの角ごとのオフセット値を計算
                half2  ndcOff   = OFFS4[cornerID] * (half)_PointSize * (half2)ndcPerPx;

                // 中心座標にオフセットを加えクリップ空間の最終位置を計算
                o.posCS = float4(pc.xy + (float2)ndcOff * pc.w, pc.zw);

                // カラーを「0～1の小数値」で取得
                o.col   = DecodeColor(_Colors[svInstanceID]);

                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                return i.col;
            }
            ENDHLSL
        }
    }
    FallBack Off
}
