Shader "Custom/PointCloud_GatherFill"
{
    // 集め直し方式の点描きパス用シェーダ。
    // 全点を1画素の点（1点=1頂点）として作業テクスチャへ描き、深度テストで1画素1点（最も手前）に絞る。
    // 出力は「色＋画面半径」を詰めた値と深度で、後段の集め直しパスが円として描き直す。
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
            #pragma target 4.5
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

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
            // ノード描画データ（_CommandIdで引く）
            StructuredBuffer<NodeDrawData> _NodeData;
            // 今発行中のノードのインデックス（CommandBufferがノード毎にSetGlobalIntで渡す）
            int _CommandId;
            // ワールド半径→画面ピクセル変換係数（C#から渡す: 投影Y倍率 × 画面高さ/2）
            float _WorldToPixel;
            // 画面上の点サイズの下限と上限（ピクセル半径。直接描画と同じ値）
            float _MinPixelSize;
            float _MaxPixelSize;

            static const uint MASK16 = 0xFFFFu;  // 下位16ビット用
            // 画面半径を8ビットへ詰める時の倍率（1/16ピクセル刻み。最大約15.9ピクセル）
            static const float RADIUS_SCALE = 16.0;

            // u16量子化座標をmin/maxでワールド座標(float)へ復元
            inline float3 DecodePos(uint uxy, uint uz16, float3 minV, float3 maxV)
            {
                uint ux = (uxy     & MASK16);
                uint uy = (uxy >> 16);
                uint uz = (uz16    & MASK16);

                float3 t = float3((float)ux, (float)uy, (float)uz) / 65535.0;
                return lerp(minV, maxV, t);
            }

            struct Varyings
            {
                float4 posCS : SV_POSITION;
                nointerpolation uint packed : TEXCOORD0;
            };

            // 0=色(RGB 24ビット)と画面半径(上位8ビット)を詰めた値。0は「点なし」 / 1=深度
            struct FragOut
            {
                uint  packed : SV_Target0;
                float depth  : SV_Target1;
            };

            // svVertexID: ノード内の点番号（1点=1頂点）
            Varyings vert(uint svVertexID : SV_VertexID)
            {
                Varyings o;

                // 今発行中のノードの描画データと点データを取得してワールド座標を復元
                NodeDrawData node = _NodeData[_CommandId];
                uint3 pointData = _Points[node.pointStart + svVertexID];
                float3 posWS = DecodePos(pointData.x, pointData.y, node.boundsMin, node.boundsMax);

                // 1画素の点として投影する
                o.posCS = TransformWorldToHClip(posWS);

                // 画面半径を直接描画と同じ式で求め、8ビットへ詰める（0は点なしの印なので最小1）
                float clipW = max(o.posCS.w, 1e-4);
                float pixelRadius = clamp(node.worldRadius * _WorldToPixel / clipW, _MinPixelSize, _MaxPixelSize);
                uint radiusCode = (uint)clamp(round(pixelRadius * RADIUS_SCALE), 1.0, 255.0);
                o.packed = (pointData.z & 0x00FFFFFFu) | (radiusCode << 24);
                return o;
            }

            FragOut frag(Varyings i)
            {
                FragOut o;
                o.packed = i.packed;
                o.depth = i.posCS.z;
                return o;
            }
            ENDHLSL
        }
    }
    FallBack Off
}
