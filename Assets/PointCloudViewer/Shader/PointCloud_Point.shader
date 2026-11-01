Shader "Custom/PointCloud_Point"
{
    // 直接描画の1画素点版。
    // 1点を1頂点（MeshTopology.Points）で描き、Quad（4頂点・三角形2枚）より頂点処理を減らす。
    // 点の大きさは常に1画素で、点サイズの式は使わない。
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

            // マルチドロー間接描画のコマンドID取得用（コマンドは非Indexed形式）
            #define UNITY_INDIRECT_DRAW_ARGS IndirectDrawArgs
            #include "UnityIndirect.cginc"

            // ノードごとの描画データ（C#側のNodeDrawDataと並びを一致させる）
            struct NodeDrawData
            {
                float3 boundsMin;   // ノードboundsの最小（位置復元用）
                float  worldRadius; // 1点が覆うべきワールド半径（この版では使わない）
                float3 boundsMax;   // ノードboundsの最大（位置復元用）
                uint   pointStart;  // 共有点プール内の先頭位置
            };

            // 全ノード共有の点プール
            StructuredBuffer<uint3> _Points;
            // コマンドID→ノード描画データ
            StructuredBuffer<NodeDrawData> _NodeData;

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

            // svVertexID: ノード内の点番号（1点=1頂点）
            Varyings vert(uint svVertexID : SV_VertexID)
            {
                Varyings o;

                // マルチドローのコマンドIDから自ノードの描画データを取得
                InitIndirectDrawArgs(0);
                NodeDrawData node = _NodeData[GetCommandID(0)];
                uint pointIndex = GetIndirectVertexID(svVertexID);

                // 共有プールから点データを取得してワールド座標を復元し、1画素の点として投影する
                uint3 pointData = _Points[node.pointStart + pointIndex];
                float3 posWS = DecodePos(pointData.x, pointData.y, node.boundsMin, node.boundsMax);
                o.posCS = TransformWorldToHClip(posWS);

                // 点データからカラーを復元
                o.col = DecodeColor(pointData.z);
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                // 点の色をそのまま出力
                return i.col;
            }
            ENDHLSL
        }
    }
    FallBack Off
}
