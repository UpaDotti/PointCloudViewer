Shader "Custom/PointCloud_Mesh"
{
    Properties
    {
        _PointSize ("Point Size", Float) = 5.0
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue"="Geometry" }
        Pass
        {
            Name "PointPass"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 position : POSITION;
                float4 color : COLOR;
            };

            struct Varyings
            {
                float4 position : SV_POSITION;
                float4 color : COLOR;
                float pointSize : PSIZE;
            };

            float _PointSize;

            Varyings vert(Attributes input)
            {
                Varyings output;
                output.position = TransformObjectToHClip(input.position.xyz);
                output.color = input.color;
                output.pointSize = _PointSize;
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                return input.color;
            }

            ENDHLSL
        }
    }

    FallBack "Hidden/Universal Render Pipeline/FallbackError"
}
