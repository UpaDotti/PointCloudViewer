Shader "Custom/PointCloud_Circle"
{
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

            // マルチドロー間接描画のコマンドID取得用（コマンドはIndexed形式）
            #define UNITY_INDIRECT_DRAW_ARGS IndirectDrawIndexedArgs
            #include "UnityIndirect.cginc"

            // ノードごとの描画データ（C#側のNodeDrawDataと並びを一致させる）
            struct NodeDrawData
            {
                float3 boundsMin;   // ノードboundsの最小（位置復元用）
                float  worldRadius; // 1点が覆うべきワールド半径
                float3 boundsMax;   // ノードboundsの最大（位置復元用）
                uint   pointStart;  // 共有点プール内の先頭位置
            };

            // 全ノード共有の点プール
            StructuredBuffer<uint3> _Points;
            // コマンドID→ノード描画データ
            StructuredBuffer<NodeDrawData> _NodeData;
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
                float2 localPos : TEXCOORD0;
            };

            // SV_VertexID :  現在処理している頂点列の中での通し番号（インデックスバッファ）
            // SV_InstanceID : 現在処理しているインスタンスが何番目か
            Varyings vert(uint svVertexID : SV_VertexID, uint svInstanceID : SV_InstanceID)
            {
                Varyings o;

                // マルチドローのコマンドIDから自ノードの描画データを取得
                InitIndirectDrawArgs(0);
                NodeDrawData node = _NodeData[GetCommandID(0)];
                uint instanceID = GetIndirectInstanceID(svInstanceID);

                // Quadのどの位置かを「0〜3」で取得
                const uint cornerID = svVertexID;

                // 共有プールから自ノードの点データを取得
                uint3 pointData = _Points[node.pointStart + instanceID];

                // 点データからワールド座標を復元
                float3 posWS = DecodePos(pointData.x, pointData.y, node.boundsMin, node.boundsMax);

                // ワールド座標をクリップ空間に変換
                float4 posCSC    = TransformWorldToHClip(posWS);

                // クリップ空間のw（パースでは視点からの奥行き）。0割れ防止でクランプ。
                float clipW = max(posCSC.w, 1e-4);
                // ワールド半径を画面ピクセル半径へ変換（近いほど大きく/遠いほど小さく。正射では clipW=1）
                float pixelRadius = node.worldRadius * _WorldToPixel / clipW;

                // 下限と上限でクランプ（遠くで消えない / 粗い面の近接で巨大化しない）
                pixelRadius = clamp(pixelRadius, _MinPixelSize, _MaxPixelSize);

                // NDC座標における1ピクセルのサイズを取得
                float2 ndcPerPx = 2.0 / _ScreenParams.xy;

                // Quadの角ごとのオフセット値を計算（ピクセル半径 → NDC）
                float2 ndcOff = (float2)OFFS4[cornerID] * pixelRadius * ndcPerPx;

                // 中心座標にオフセットを加えクリップ空間の最終位置を計算
                o.posCS = float4(posCSC.xy + ndcOff * posCSC.w, posCSC.zw);

                // 点データからカラーを復元
                o.col   = DecodeColor(pointData.z);

                // -1〜+1 のローカル座標
                o.localPos = OFFS4[cornerID];

                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                 // (-1,-1)〜(+1,+1)
                float2 p = i.localPos;

                // 原点からの距離の二乗
                float r2 = dot(p, p);

                // 半径1の円の外側なら破棄
                if (r2 > 1.0)
                    discard;

                // 点の色をそのまま出力
                return i.col;
            }
            ENDHLSL
        }
    }
    FallBack Off
}
