Shader "Lever/Glass"
{
    Properties
    {
        _BaseColor ("Glass Color", Color) = (0.35, 0.72, 0.88, 0.12)
        _RimColor ("Rim Color", Color) = (0.55, 0.9, 1, 0.32)
        _RimPower ("Rim Power", Range(1, 8)) = 4
    }
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" "RenderPipeline"="UniversalPipeline" }
        Pass
        {
            Name "Glass"
            Tags { "LightMode"="SRPDefaultUnlit" }
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Back
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; };
            struct Varyings { float4 positionCS : SV_POSITION; float3 normalWS : TEXCOORD0; float3 viewWS : TEXCOORD1; };
            CBUFFER_START(UnityPerMaterial)
                half4 _BaseColor;
                half4 _RimColor;
                float _RimPower;
            CBUFFER_END
            Varyings Vert(Attributes input)
            {
                Varyings output;
                VertexPositionInputs p = GetVertexPositionInputs(input.positionOS.xyz);
                output.positionCS = p.positionCS;
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                output.viewWS = GetWorldSpaceViewDir(p.positionWS);
                return output;
            }
            half4 Frag(Varyings input) : SV_Target
            {
                half rim = pow(1.0h - saturate(dot(normalize(input.normalWS), normalize(input.viewWS))), _RimPower);
                half4 color = _BaseColor;
                color.rgb = lerp(color.rgb, _RimColor.rgb, rim);
                color.a = saturate(color.a + rim * _RimColor.a);
                return color;
            }
            ENDHLSL
        }
    }
}
