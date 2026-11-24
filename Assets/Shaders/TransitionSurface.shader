// Copyright 2026 The Open Brush Authors
// SPDX-License-Identifier: Apache-2.0

Shader "Brush/TransitionSurface"
{
    Properties
    {
        _MainTex ("Concrete diffuse", 2D) = "white" {}
        [Normal] _BumpMap ("Concrete normal", 2D) = "bump" {}
        _BumpScale ("Concrete normal strength", Range(0, 16)) = 12
        _RoughnessMap ("Concrete roughness", 2D) = "white" {}
    }

    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "RenderType" = "Opaque" }
        Cull Off

        Pass
        {
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing
            #pragma multi_compile_fog
            #pragma multi_compile _ _ADDITIONAL_LIGHTS

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);
            TEXTURE2D(_BumpMap);
            SAMPLER(sampler_BumpMap);
            TEXTURE2D(_RoughnessMap);
            SAMPLER(sampler_RoughnessMap);
            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                half _BumpScale;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                half3 normalOS : NORMAL;
                half4 tangentOS : TANGENT;
                half4 color : COLOR;
                float2 uv : TEXCOORD0;
                float3 surface : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                half3 normalWS : TEXCOORD1;
                half fog : TEXCOORD2;
                half4 tangentWS : TEXCOORD3;
                float3 positionWS : TEXCOORD4;
                half deck : TEXCOORD5;
                half4 color : COLOR;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                VertexPositionInputs position = GetVertexPositionInputs(input.positionOS.xyz);
                output.positionCS = position.positionCS;
                output.positionWS = position.positionWS;
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                output.tangentWS = half4(TransformObjectToWorldDir(input.tangentOS.xyz),
                    input.tangentOS.w * GetOddNegativeScale());
                output.fog = ComputeFogFactor(output.positionCS.z);
                output.color = input.color;
                output.uv = input.uv;
                output.deck = input.surface.x;
                return output;
            }

            half Joint(float coordinate, float halfWidth)
            {
                float distance = abs(frac(coordinate + 0.5) - 0.5);
                float footprint = fwidth(coordinate);
                float antialias = max(footprint * 0.5, 0.001);
                float joint = 1.0 - smoothstep(halfWidth - antialias, halfWidth + antialias, distance);
                return lerp(joint, 2.0 * halfWidth, saturate(footprint - 0.5));
            }

            void AccumulateLight(Light light, half3 normal, half3 view, half glossPower, half specularStrength,
                inout half3 diffuse, inout half3 specular)
            {
                half3 radiance = light.color * light.distanceAttenuation;
                half incidence = dot(normal, light.direction);
                diffuse += radiance * saturate((incidence + 0.15h) / 1.15h);
                half3 halfDirection = SafeNormalize(light.direction + view);
                half gloss = pow(saturate(dot(normal, halfDirection)), glossPower);
                specular += radiance * gloss * specularStrength * saturate(incidence);
            }

            half4 Frag(Varyings input, FRONT_FACE_TYPE facing : FRONT_FACE_SEMANTIC) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float2 concreteUV = TRANSFORM_TEX(input.uv, _MainTex);
                // UVs describe a 3 m repeat. Use whole multiples so grout and coping joints close with the loop.
                float2 tile = input.uv * 60.0;
                half grout = max(Joint(tile.x, 0.032), Joint(tile.y, 0.032));
                half copingJoint = Joint(input.uv.x * 6.0, 0.004);
                bool deck = input.deck > 0.5;
                bool coping = !deck && input.uv.y < 0.0;
                bool tileBand = !deck && !coping && tile.y < 3.0;
                // A shared grout mask controls both the raised glaze and the direct specular response.
                float2 tileOffset = frac(tile + 0.5) - 0.5;
                float2 tileDistance = abs(tileOffset);
                float2 bevel = smoothstep(0.032, 0.058, tileDistance) *
                    (1.0 - smoothstep(0.058, 0.13, tileDistance));
                half tileGlaze = 1.0h - grout;
                half bevelVisibility = saturate(1.0 - 3.0 * max(fwidth(tile.x), fwidth(tile.y)));
                half3 baseNormal = NormalizeNormalPerPixel(input.normalWS);
                half3 tangent = SafeNormalize(input.tangentWS.xyz -
                    baseNormal * dot(input.tangentWS.xyz, baseNormal));
                half3 bitangent = cross(baseNormal, tangent) * input.tangentWS.w;
                half2 slope = UnpackNormalScale(
                    SAMPLE_TEXTURE2D(_BumpMap, sampler_BumpMap, concreteUV), _BumpScale).xy;
                slope *= coping ? 0.55h : 1.0h;
                if (tileBand)
                    slope = -sign(tileOffset) * bevel * (0.52h * tileGlaze * bevelVisibility);
                // Bring out shallow pits after mip filtering while limiting the steepest slopes to 45 degrees.
                slope *= rsqrt(1.0h + 2.0h * dot(slope, slope));
                half perpendicular = sqrt(1.0h - dot(slope, slope));
                half3 normal = NormalizeNormalPerPixel(
                    tangent * slope.x + bitangent * slope.y + baseNormal * perpendicular) *
                    IS_FRONT_VFACE(facing, 1.0h, -1.0h);
                half roughness = SAMPLE_TEXTURE2D(_RoughnessMap, sampler_RoughnessMap, concreteUV).r;
                half glossPower = tileBand ? 50.0h : lerp(32.0h, 4.0h, roughness);
                half specularStrength = tileBand ? 0.85h * tileGlaze : 0.15h * (1.0h - roughness);
                half3 view = GetWorldSpaceNormalizeViewDir(input.positionWS);
                half3 diffuse = 0.0h;
                half3 specular = 0.0h;
                AccumulateLight(GetMainLight(), normal, view, glossPower, specularStrength, diffuse, specular);
                #if defined(_ADDITIONAL_LIGHTS)
                    uint lightCount = GetAdditionalLightsCount();
                    LIGHT_LOOP_BEGIN(lightCount)
                        AccumulateLight(GetAdditionalLight(lightIndex, input.positionWS),
                            normal, view, glossPower, specularStrength, diffuse, specular);
                    LIGHT_LOOP_END
                #endif

                // Compress bright direct light smoothly to preserve contrast in the normal map.
                half peak = Max3(diffuse.r, diffuse.g, diffuse.b);
                half3 lighting = 0.33h + 0.10h * saturate(SampleSH(normal)) + diffuse * (0.85h / (0.75h + peak));
                // The app's default mid-grey preserves the concrete's photographed tone.
                half3 tint = lerp(0.25h, 1.75h, input.color.rgb);
                half3 concrete = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, concreteUV).rgb;
                half3 color = tint * concrete;
                if (tileBand)
                    color = lerp(half3(0.62h, 0.78h, 0.88h), half3(0.43h, 0.49h, 0.52h), grout);
                if (coping)
                    color = lerp(concrete * 0.68h, half3(0.60h, 0.58h, 0.53h), copingJoint * 0.72h);
                if (deck)
                    color = tint * (concrete * 0.87h + 0.10h);
                return half4(MixFog(color * lighting + specular, input.fog), 1.0h);
            }
            ENDHLSL
        }
    }
}
