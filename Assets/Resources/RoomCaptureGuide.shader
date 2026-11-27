// Copyright 2026 The Open Brush Authors
// SPDX-License-Identifier: Apache-2.0

Shader "Hidden/Open Brush/Room Capture Guide"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "Queue" = "Transparent" "RenderType" = "Transparent" }

        Blend One OneMinusSrcAlpha
        Cull Off
        ZWrite Off
        ZTest LEqual

        Pass
        {
            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 markerUV : TEXCOORD0;
                half4 state : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 markerUV : TEXCOORD0;
                half covered : TEXCOORD1;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.markerUV = input.markerUV;
                output.covered = input.state.r;
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float distance = length(input.markerUV);
                float edge = clamp(fwidth(distance), 0.01, 0.15);
                // Amber rings retain a dark edge on bright walls. Covered dots take less space.
                float outlineRadius = lerp(0.96, 0.58, input.covered);
                float inkRadius = lerp(0.81, 0.42, input.covered);
                half mask = 1.0 - smoothstep(outlineRadius - edge, outlineRadius + edge, distance);
                half ink = 1.0 - smoothstep(inkRadius - edge, inkRadius + edge, distance);
                if (input.covered < 0.5)
                {
                    mask *= smoothstep(0.43 - edge, 0.43 + edge, distance);
                    ink *= smoothstep(0.59 - edge, 0.59 + edge, distance);
                }
                half3 tint = lerp(half3(1.0, 0.66, 0.17), half3(0.26, 0.94, 0.96), input.covered);
                half alpha = mask * lerp(0.95, 0.78, input.covered);
                half3 color = lerp(half3(0.025, 0.035, 0.045), tint, ink);
                return half4(color * alpha, alpha);
            }
            ENDHLSL
        }
    }
}
