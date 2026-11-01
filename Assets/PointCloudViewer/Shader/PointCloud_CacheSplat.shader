Shader "Custom/PointCloud_CacheSplat"
{
    // 点キャッシュ方式の表示パス用シェーダ。
    // 点一覧の各点を、現在のカメラで円（Quad＋円外discard）として描く。
    // 円の大きさは直接描画（PointCloud_Circle）と同じ式で毎フレーム求め直す。
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" "RenderPipeline"="UniversalPipeline" }
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

            // 点一覧の1点（PointCloudCacheCompact.computeのCachePointと並びを一致させる）
            struct CachePoint
            {
                float3 position;    // ワールド座標
                uint   color;       // Color32そのまま
                float  worldRadius; // 1点が覆うべきワールド半径
            };

            // 描画する点一覧
            StructuredBuffer<CachePoint> _Cache;
            // ワールド半径→画面ピクセル変換係数（C#から渡す: 投影Y倍率 × 画面高さ/2）
            float _WorldToPixel;
            // 画面上の点サイズ下限（ピクセル半径）。遠くで消えないための最小値。
            float _MinPixelSize;
            // 画面上の点サイズ上限（ピクセル半径）。粗い面を近くで見た時の巨大ディスクを防ぐ。
            float _MaxPixelSize;

            // Quad四隅のオフセット係数
            static const half2 OFFS4[4] = {
                half2(-1,-1), half2(+1,-1),
                half2(-1,+1), half2(+1,+1)
            };

            static const uint MASK8 = 0xFFu;    // 下位8ビット用

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
                float2 localPos : TEXCOORD0;
            };

            // svVertexID: Quadの角番号(0〜3) / svInstanceID: 点一覧内の点番号
            Varyings vert(uint svVertexID : SV_VertexID, uint svInstanceID : SV_InstanceID)
            {
                Varyings o;

                // 点一覧から点を取得
                CachePoint cachePoint = _Cache[svInstanceID];

                // ワールド座標をクリップ空間に変換
                float4 posCSC = TransformWorldToHClip(cachePoint.position);

                // ワールド半径を画面ピクセル半径へ変換して上下限でクランプ（直接描画と同じ式）
                float clipW = max(posCSC.w, 1e-4);
                float pixelRadius = cachePoint.worldRadius * _WorldToPixel / clipW;
                pixelRadius = clamp(pixelRadius, _MinPixelSize, _MaxPixelSize);

                // Quadの角ごとのオフセットをピクセル半径→NDCで求めて中心に加える
                float2 ndcPerPx = 2.0 / _ScreenParams.xy;
                float2 ndcOff = (float2)OFFS4[svVertexID] * pixelRadius * ndcPerPx;
                o.posCS = float4(posCSC.xy + ndcOff * posCSC.w, posCSC.zw);

                // 色と円内のローカル座標
                o.col = DecodeColor(cachePoint.color);
                o.localPos = OFFS4[svVertexID];
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                // 半径1の円の外側なら破棄
                if (dot(i.localPos, i.localPos) > 1.0)
                    discard;

                // 点の色をそのまま出力
                return i.col;
            }
            ENDHLSL
        }
    }
    FallBack Off
}
