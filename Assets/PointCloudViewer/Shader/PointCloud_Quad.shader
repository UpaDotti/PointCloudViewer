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

            StructuredBuffer<uint3> _Points;
            float3 _ChunkMin;
            float3 _ChunkMax;
            float _PointSize;

            // Quad Triangleの頂点 組み合わせ
            static const uint QUAD_INDEX[6] = { 0,1,2, 2,1,3 };

            // Quad四隅のオフセット係数
            static const half2 OFFS4[4] = {
                half2(-1,-1), half2(+1,-1),
                half2(-1,+1), half2(+1,+1)
            };
            
            static const uint MASK8  = 0xFFu;    // 下位8ビット用
            static const uint MASK16 = 0xFFFFu;  // 下位16ビット用

            // u16量子化座標をmin/maxでワールド座標(float)へ復元
            inline float3 DecodePos(uint uxy, uint uz16, float3 minV, float3 maxV)
            {
                uint ux = (uxy     & MASK16);
                uint uy = (uxy >> 16);
                uint uz = (uz16    & MASK16);

                float3 t = float3((float)ux, (float)uy, (float)uz) / 65535.0;
                return lerp(minV, maxV, t);
            }

            // Color32をRGBAに変換
            inline half4 DecodeColor(uint c)
            {
                uint r = ( c        & MASK8);
                uint g = ((c >> 8 ) & MASK8);
                uint b = ((c >> 16) & MASK8);
                uint a = ((c >> 24) & MASK8);
                return half4(r, g, b, a) / 255.0h;
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

                // 点群バッファから指定の点データを取得
                uint3 pointData = _Points[svInstanceID];

                // 点データからワールド座標を復元
                float3 posWS = DecodePos(pointData.x, pointData.y, _ChunkMin, _ChunkMax);

                // ワールド座標をクリップ空間に変換
                float4 posCSC    = TransformWorldToHClip(posWS);

                // NDC座標における1ピクセルのサイズを取得
                float2 ndcPerPx = 2.0 / _ScreenParams.xy;

                // Quadの角ごとのオフセット値を計算
                half2  ndcOff   = OFFS4[cornerID] * (half)_PointSize * (half2)ndcPerPx;

                // 中心座標にオフセットを加えクリップ空間の最終位置を計算
                o.posCS = float4(posCSC.xy + (float2)ndcOff * posCSC.w, posCSC.zw);

                // 点データからカラーを復元
                o.col   = DecodeColor(pointData.z);

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
