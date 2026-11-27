// Copyright 2026 The Open Brush Authors
// SPDX-License-Identifier: Apache-2.0

Shader "Hidden/Open Brush/Room Capture Test Readback"
{
    SubShader
    {
        Pass
        {
            Cull Off ZWrite Off ZTest Always

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex vert_img
            #pragma fragment ReadDirection
            #include "UnityCG.cginc"

            samplerCUBE _TestRoomCube;
            float3 _TestRoomDirection;

            float4 ReadDirection(v2f_img input) : SV_Target
            {
                return texCUBElod(_TestRoomCube, float4(_TestRoomDirection, 0));
            }
            ENDHLSL
        }
    }
}
