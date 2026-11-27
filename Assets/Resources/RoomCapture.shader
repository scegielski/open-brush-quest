// Copyright 2026 The Open Brush Authors
// SPDX-License-Identifier: Apache-2.0

Shader "Hidden/Open Brush/Room Capture"
{
    SubShader
    {
        Cull Off ZWrite Off ZTest Always

        HLSLINCLUDE
        #include "UnityCG.cginc"

        sampler2D _RoomCaptureImage;
        samplerCUBE _RoomCaptureRadiance;
        samplerCUBE _RoomCaptureFallback;
        float4 _RoomCaptureFallbackDecode;
        float _RoomCaptureHasFallback;
        float4 _RoomCaptureIntrinsics; // Focal length and principal point in image UV units.
        float4x4 _RoomCaptureWorldToCamera;
        float3 _RoomCaptureFaceForward;
        float3 _RoomCaptureFaceRight;
        float3 _RoomCaptureFaceDown;

        struct Varyings
        {
            float4 position : SV_POSITION;
            float2 uv : TEXCOORD0;
        };

        Varyings Vert(uint id : SV_VertexID)
        {
            Varyings output;
            output.uv = float2((id << 1) & 2, id & 2);
            output.position = float4(output.uv * 2 - 1, 0, 1);
            return output;
        }

        float3 Direction(float2 uv)
        {
            // Cubemap image rows run down from the top of a face.
            float2 face = uv * 2 - 1;
            #if UNITY_UV_STARTS_AT_TOP
            face.y = -face.y;
            #endif
            return normalize(_RoomCaptureFaceForward + face.x * _RoomCaptureFaceRight +
                face.y * _RoomCaptureFaceDown);
        }

        float4 Paint(Varyings input) : SV_Target
        {
            float3 ray = mul(_RoomCaptureWorldToCamera, float4(Direction(input.uv), 0)).xyz;
            clip(ray.z - 0.0001);
            float2 uv = _RoomCaptureIntrinsics.xy * ray.xy / ray.z + _RoomCaptureIntrinsics.zw;
            float2 edges = min(uv, 1 - uv);
            float edge = min(edges.x, edges.y);
            clip(edge);
            float alpha = smoothstep(0.015, 0.13, edge);
            float3 cameraColor = GammaToLinearSpace(tex2D(_RoomCaptureImage, uv).rgb);
            return float4(cameraColor * alpha, alpha);
        }

        float4 Convolve(Varyings input) : SV_Target
        {
            float3 normal = Direction(input.uv);
            float3 tangent = normalize(cross(abs(normal.y) < 0.999 ? float3(0, 1, 0) : float3(1, 0, 0), normal));
            float3 bitangent = cross(normal, tangent);
            float4 irradiance = 0;
            // Uniform disk samples give cosine-weighted directions on the hemisphere.
            const int samples = 32;
            UNITY_UNROLL
            for (int i = 0; i < samples; i++)
            {
                float r2 = (i + 0.5) / samples;
                float angle = i * 2.39996323;
                float sine, cosine;
                sincos(angle, sine, cosine);
                float3 ray = normal * sqrt(1 - r2) + sqrt(r2) * (tangent * cosine + bitangent * sine);
                irradiance += texCUBElod(_RoomCaptureRadiance, float4(ray, 2.5));
            }
            return irradiance / samples;
        }
        float4 ComposeReflection(Varyings input) : SV_Target
        {
            float3 ray = Direction(input.uv);
            float4 room = texCUBElod(_RoomCaptureRadiance, float4(ray, 0));
            if (_RoomCaptureHasFallback > 0.5)
            {
                float4 fallback = texCUBElod(_RoomCaptureFallback, float4(ray, 0));
                float3 radiance = DecodeHDR(fallback, _RoomCaptureFallbackDecode);
                #ifdef UNITY_COLORSPACE_GAMMA
                radiance = GammaToLinearSpace(radiance);
                #endif
                room.rgb += radiance * (1 - saturate(room.a));
            }
            #ifdef UNITY_COLORSPACE_GAMMA
            room.rgb = LinearToGammaSpace(room.rgb);
            #endif
            return float4(room.rgb, 1);
        }

        float4 SampleAmbientAxes(Varyings input) : SV_Target
        {
            const float3 axes[6] = {
                float3(1, 0, 0), float3(-1, 0, 0), float3(0, 1, 0),
                float3(0, -1, 0), float3(0, 0, 1), float3(0, 0, -1)
            };
            return texCUBElod(_RoomCaptureRadiance, float4(axes[min((int)(input.uv.x * 6), 5)], 0));
        }
        ENDHLSL

        Pass
        {
            Name "Paint"
            Blend One OneMinusSrcAlpha

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Vert
            #pragma fragment Paint
            ENDHLSL
        }

        Pass
        {
            Name "Diffuse"
            Blend Off

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Vert
            #pragma fragment Convolve
            ENDHLSL
        }

        Pass
        {
            Name "Scene Reflection"

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Vert
            #pragma fragment ComposeReflection
            ENDHLSL
        }

        Pass
        {
            Name "Ambient Samples"

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Vert
            #pragma fragment SampleAmbientAxes
            ENDHLSL
        }
    }
}
