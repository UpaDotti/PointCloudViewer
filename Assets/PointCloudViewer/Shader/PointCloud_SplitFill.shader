Shader "Custom/PointCloud_SplitFill"
{
    // 点キャッシュ方式の整列パス用シェーダ。
    // 受け持ちの点を1画素の点として描き、深度テストで1画素1点（最も手前）に絞る。
    // 出力はワールド座標＋ワールド半径と色で、後段のcomputeが点一覧へ詰め直す。
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
            // スライス分割: 各ノードで _SliceOffset 番目から _SliceStride 間隔で点を間引いて描く
            int _SliceOffset;
            int _SliceStride;

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

            struct Varyings
            {
                float4 posCS       : SV_POSITION;
                float3 posWS       : TEXCOORD0;
                nointerpolation float worldRadius : TEXCOORD1;
                nointerpolation uint  color       : TEXCOORD2;
            };

            // 整列パスの出力（0=ワールド座標と半径。半径>0が「点あり」の印 / 1=Color32そのまま）
            struct FragOut
            {
                float4 positionRadius : SV_Target0;
                uint   color          : SV_Target1;
            };

            // svVertexID: このスライスでのノード内の点番号（1点=1頂点）
            Varyings vert(uint svVertexID : SV_VertexID)
            {
                Varyings o;

                // 今発行中のノードの描画データを取得
                NodeDrawData node = _NodeData[_CommandId];

                // スライス分割を考慮したノード内の点番号（offset番目からstride間隔）
                uint localIndex = (uint)_SliceOffset + svVertexID * (uint)_SliceStride;

                // 共有プールから点データを取得してワールド座標を復元
                uint3 pointData = _Points[node.pointStart + localIndex];
                float3 posWS = DecodePos(pointData.x, pointData.y, node.boundsMin, node.boundsMax);

                // 1画素の点として投影する
                o.posCS = TransformWorldToHClip(posWS);
                o.posWS = posWS;
                o.worldRadius = node.worldRadius;
                o.color = pointData.z;
                return o;
            }

            FragOut frag(Varyings i)
            {
                // 点のワールド座標・半径・色をそのまま書き出す
                FragOut o;
                o.positionRadius = float4(i.posWS, i.worldRadius);
                o.color = i.color;
                return o;
            }
            ENDHLSL
        }
    }
    FallBack Off
}
