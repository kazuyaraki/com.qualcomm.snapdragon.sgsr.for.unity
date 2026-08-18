//============================================================================================================
//
//
//                  Copyright (c) 2026, Qualcomm Innovation Center, Inc. All rights reserved.
//                              SPDX-License-Identifier: BSD-3-Clause
//
//============================================================================================================

#define FLT_EPSILON (1.192092896e-07)

// ---------------------------------------------------------------------------
// Textures
// ---------------------------------------------------------------------------

TEXTURE2D(_SGSRInputDepth);
SAMPLER(sampler_SGSRInputDepth);

TEXTURE2D(_SGSRInputVelocity);
SAMPLER(sampler_SGSRInputVelocity);

// ---------------------------------------------------------------------------
// Constant buffer
// ---------------------------------------------------------------------------

CBUFFER_START(SGSR2Params)
    float2 InputInfo_ViewportSize;
    float2 InputInfo_ViewportSizeInverse;
    float2 InputJitter;
    float  AngleVertical;
    float4x4 ReClipToPrevClip;
CBUFFER_END

// ---------------------------------------------------------------------------
// Fragment entry point
// ---------------------------------------------------------------------------

half4 FragConvert(Varyings input) : SV_Target
{
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
    float2 ViewportUV = input.texcoord;

    uint2 InputPos = (uint2)(ViewportUV * InputInfo_ViewportSize);

    float2 gatherCoord = ViewportUV - float2(0.5f, 0.5f) * InputInfo_ViewportSizeInverse;

    half4 topleft     = GATHER_RED_TEXTURE2D(_SGSRInputDepth, sampler_SGSRInputDepth, gatherCoord);
    half4 topRight    = GATHER_RED_TEXTURE2D(_SGSRInputDepth, sampler_SGSRInputDepth, gatherCoord + float2(InputInfo_ViewportSizeInverse.x * 2.0f, 0.0f));
    half4 bottomLeft  = GATHER_RED_TEXTURE2D(_SGSRInputDepth, sampler_SGSRInputDepth, gatherCoord + float2(0.0f, InputInfo_ViewportSizeInverse.y * 2.0f));
    half4 bottomRight = GATHER_RED_TEXTURE2D(_SGSRInputDepth, sampler_SGSRInputDepth, gatherCoord + float2(InputInfo_ViewportSizeInverse.x * 2.0f, InputInfo_ViewportSizeInverse.y * 2.0f));

    half maxC        = max(max(max(topleft.y, topRight.x), bottomLeft.z), bottomRight.w);
    half topleft4    = max(max(max(topleft.y, topleft.x), topleft.z), topleft.w);
    half topLeftMax9 = max(bottomLeft.w, max(max(maxC, topleft4), topRight.w));

    half depthclip = 0.0;
    if (maxC > 1.0e-05)
    {
        half topRight4    = max(max(max(topRight.y,    topRight.x),    topRight.z),    topRight.w);
        half bottomLeft4  = max(max(max(bottomLeft.y,  bottomLeft.x),  bottomLeft.z),  bottomLeft.w);
        half bottomRight4 = max(max(max(bottomRight.y, bottomRight.x), bottomRight.z), bottomRight.w);

        half  Wdepth = 0.0;
        float Ksep = 1.37e-05f;
        float Kfov = AngleVertical; /** (InputInfo_ViewportSize.x / InputInfo_ViewportSize.y)*/
        float diagonal_length = length(InputInfo_ViewportSize);
        float Ksep_Kfov_diagonal = Ksep * Kfov * diagonal_length;

        half Depthsep = Ksep_Kfov_diagonal * maxC;
        Wdepth += saturate(Depthsep / (abs(maxC - topleft4)    + FLT_EPSILON));
        Wdepth += saturate(Depthsep / (abs(maxC - topRight4)   + FLT_EPSILON));
        Wdepth += saturate(Depthsep / (abs(maxC - bottomLeft4) + FLT_EPSILON));
        Wdepth += saturate(Depthsep / (abs(maxC - bottomRight4)+ FLT_EPSILON));

        depthclip = saturate(1.0 - Wdepth * 0.25);
    }

    float4 EncodedVelocity = LOAD_TEXTURE2D(_SGSRInputVelocity, int2(InputPos));

    float2 motion;
    if (any(EncodedVelocity.xy))
    {
        motion = float2(EncodedVelocity.x, -EncodedVelocity.y) * 2.0f;
    }
    else
    {
        float2 ScreenPos = float2(2 * ViewportUV.x - 1, 1 - 2 * ViewportUV.y);
        float3 Position  = float3(ScreenPos, topLeftMax9);
        float4 CurClip   = float4(Position, 1);
        float4 PreClip   = mul(CurClip, ReClipToPrevClip);
        float2 PreScreen = PreClip.xy / PreClip.w;
        motion = Position.xy - PreScreen;
    }

    return half4((half2)motion, (half)depthclip, topleft.x);
}
