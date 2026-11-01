Shader "Custom/PointCloud_GatherResolve"
{
    // 集め直し方式の表示パス用シェーダ（全画面1枚）。
    // 各画素で周り半径GATHER_RADIUSの1画素点を調べ、自分を覆う円のうち最も手前の点の色と深度を書く。
    // どの円にも覆われない画素は書かない（シーンの色と深度を残す）。
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

            // 点描きパスの出力（色＋画面半径を詰めた値 / 深度）
            Texture2D<uint>  _GatherPacked;
            Texture2D<float> _GatherDepth;

            // 調べる近傍の半径（ピクセル）。これより大きい円はこの半径で切れる（試作の制限）
            #define GATHER_RADIUS 4
            // 画面半径を8ビットから戻す時の倍率（点描きパスと一致させる）
            static const float RADIUS_SCALE = 16.0;
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
                int2 pixel = int2(i.posCS.xy);
                int2 size;
                _GatherPacked.GetDimensions(size.x, size.y);

                // 自分を覆う円のうち最も手前の点を探す
                uint bestPacked = 0u;
                #if UNITY_REVERSED_Z
                float bestDepth = 0.0;
                #else
                float bestDepth = 1.0;
                #endif

                [unroll]
                for (int dy = -GATHER_RADIUS; dy <= GATHER_RADIUS; dy++)
                {
                    [unroll]
                    for (int dx = -GATHER_RADIUS; dx <= GATHER_RADIUS; dx++)
                    {
                        // 半径の外は調べない
                        int distanceSquared = dx * dx + dy * dy;
                        if (distanceSquared > GATHER_RADIUS * GATHER_RADIUS) continue;

                        int2 neighbor = pixel + int2(dx, dy);
                        if (any(neighbor < 0) || any(neighbor >= size)) continue;

                        // 点が無い画素、または円がここまで届かない点は飛ばす
                        uint packed = _GatherPacked.Load(int3(neighbor, 0));
                        if (packed == 0u) continue;
                        float radius = (float)(packed >> 24) / RADIUS_SCALE;
                        if ((float)distanceSquared > radius * radius) continue;

                        // より手前なら採用する
                        float depth = _GatherDepth.Load(int3(neighbor, 0));
                        #if UNITY_REVERSED_Z
                        bool nearer = depth > bestDepth;
                        #else
                        bool nearer = depth < bestDepth;
                        #endif
                        if (nearer)
                        {
                            bestDepth = depth;
                            bestPacked = packed;
                        }
                    }
                }

                // どの円にも覆われない画素は書かない
                if (bestPacked == 0u) discard;

                FragOut o;
                o.color = half4(bestPacked & MASK8, (bestPacked >> 8) & MASK8, (bestPacked >> 16) & MASK8, 255) / 255.0h;
                o.depth = bestDepth;
                return o;
            }
            ENDHLSL
        }
    }
    FallBack Off
}
