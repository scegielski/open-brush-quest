// Copyright 2026 The Open Brush Authors
// SPDX-License-Identifier: Apache-2.0

Shader "Custom/TransitionPreviewLine"
{
    Properties
    {
        _Color ("Color", Color) = (0.16, 0.72, 0.86, 1)
    }

    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "Queue" = "Overlay" "RenderType" = "Transparent" }
        Blend One OneMinusSrcAlpha
        Cull Off
        ZWrite Off
        ZTest Always

        Pass
        {
            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
            half4 _Color;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float dashCoordinate : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.dashCoordinate = input.uv.x;
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float dashDistance = abs(frac(input.dashCoordinate + 0.5) - 0.5);
                float edge = clamp(fwidth(input.dashCoordinate), 0.001, 0.15);
                half alpha = (1.0 - smoothstep(0.30 - edge, 0.30 + edge, dashDistance)) * _Color.a;
                return half4(_Color.rgb * alpha, alpha);
            }
            ENDHLSL
        }
    }
}
