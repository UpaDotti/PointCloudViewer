Shader "Custom/PointCloudSplitComposite"
{
    // スライスバッファ1枚を画面へ深度付きで合成するフルスクリーンパス。
    // 各スライスを順に重ね、SV_Depth＋ZTestで最も手前の点が勝つ（closest wins）。
    // Stage2a: 再投影なし（同じUVでサンプル）。Stage2bで再投影UVに差し替える。

    // MaterialPropertyBlockでのSetTextureはPropertiesブロックに宣言が無いと効かないため、ここで宣言する
    Properties
    {
        _SliceColor ("Slice Color", 2D) = "black" {}
        _SliceDepth ("Slice Depth", 2D) = "white" {}
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalRenderPipeline" }
        ZWrite On
        ZTest LEqual
        Cull Off
        Blend Off

        Pass
        {
            HLSLPROGRAM
            #pragma vertex   vert
            #pragma fragment frag
            #pragma target 4.5
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            // 合成するスライスの色と深度（C#からスライス別MPBで差し替える）
            Texture2D _SliceColor;
            Texture2D _SliceDepth;
            // sampler_PointClamp はURP(Core.hlsl)が定義済み。自前SAMPLER宣言は再定義エラーになるので使わない。

            // 再投影行列: スライス撮影VPの逆（slice→world）と現在VP（world→現在）
            float4x4 _SliceInvVP;
            float4x4 _CurVP;
            // disocclusion棄却のしきい値（往復UV誤差がこれを超えたら捨てる）
            float _WarpTolerance;

            struct Varyings
            {
                float4 posCS : SV_POSITION;
                float2 uv    : TEXCOORD0;
            };

            // SV_VertexIDからフルスクリーン三角形を生成（URP共通関数）
            Varyings vert(uint vertexID : SV_VertexID)
            {
                Varyings o;
                o.posCS = GetFullScreenTriangleVertexPosition(vertexID);
                o.uv    = GetFullScreenTriangleTexCoord(vertexID);
                return o;
            }

            struct FragOut
            {
                half4 color : SV_Target;
                float depth : SV_Depth;
            };

            // world座標→現在ビューのUVとデバイス深度（ComputeWorldSpacePositionの厳密な逆変換）
            void WorldToCurrent(float3 world, out float2 uv, out float deviceDepth)
            {
                float4 clip = mul(_CurVP, float4(world, 1.0));
                float3 ndc = clip.xyz / clip.w;
                deviceDepth = ndc.z;            // 深度はY反転の影響を受けない
                float2 ndcXY = ndc.xy;
            #if UNITY_UV_STARTS_AT_TOP
                ndcXY.y = -ndcXY.y;             // ComputeClipSpacePositionと対にする
            #endif
                uv = ndcXY * 0.5 + 0.5;
            }

            FragOut frag(Varyings i)
            {
                FragOut o;

                // 出力画素（現在ビュー）に対応するスライスUVを反復で求める（逆方向ガザー）
                float2 uvCur = i.uv;
                float2 uvSlice = uvCur;     // 初期推定（小さな動きなら現在UVでよい）
                float2 uvCurGuess = uvCur;
                float deviceDepthCur = 0.0;

                [unroll]
                for (int iter = 0; iter < 3; iter++)
                {
                    // スライス側の実深度からワールド座標を復元し、現在ビューへ投影
                    float dSlice = _SliceDepth.Sample(sampler_PointClamp, uvSlice).r;
                    float3 world = ComputeWorldSpacePosition(uvSlice, dSlice, _SliceInvVP);
                    WorldToCurrent(world, uvCurGuess, deviceDepthCur);

                    // 推定が現在UVに一致するようスライスUVを補正（撮影=現在なら誤差0で恒等）
                    uvSlice += (uvCur - uvCurGuess);
                }

                // スライス範囲外、または往復が合わない（disocclusion）画素は捨てる
                if (any(uvSlice < 0.0) || any(uvSlice > 1.0)) discard;
                if (distance(uvCurGuess, uvCur) > _WarpTolerance) discard;

                // 収束したスライスUVの色（α=点の有無）
                half4 c = _SliceColor.Sample(sampler_PointClamp, uvSlice);
                if (c.a <= 0.0h)
                    discard;

                o.color = c;
                // 再投影後の現在ビューのデバイス深度（ZTestで他スライスと最前面を競う）
                o.depth = deviceDepthCur;
                return o;
            }
            ENDHLSL
        }
    }
    FallBack Off
}
