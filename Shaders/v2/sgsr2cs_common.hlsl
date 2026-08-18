//============================================================================================================
//
//
//                  Copyright (c) 2026 Qualcomm Innovation Center, Inc. All rights reserved.
//                              SPDX-License-Identifier: BSD-3-Clause
//
//============================================================================================================
//
// SGSR v2 — 3-Pass Compute Shader variant — shared utilities
//
// Shared utilities for the SGSR v2 3-pass compute shader variant.
//
// Contains the shared uniform buffer (SGSR2CSParams), UV helpers, and all
// color-space / math helpers used across the Convert, Activate, Upscale,
// and Sharpen passes.
//
// Unity URP-specific: the uniform layout and depth conventions match Unity's
// reversed-Z pipeline. Not portable to other engines without modification.
//
// Conventions:
//   - Unity URP reversed-Z: 1 = near, 0 = far.
//   - All color-space helpers operate on half3 for 16-bit ALU throughput on Adreno.
//     UV coordinates, viewport sizes, and matrix math remain float.
//
//============================================================================================================

#ifndef SGSR2CS_COMMON_HLSL
#define SGSR2CS_COMMON_HLSL

// ---------------------------------------------------------------------------
// Constants
// ---------------------------------------------------------------------------

#define FLT_FP16_MIN  (6.10e-05f)        // smallest positive normal half
#define FLT_FP16_MAX  (65504.0f)         // largest finite half
#define FLT_EPSILON   (1.192092896e-07f) // single-precision machine epsilon
#define FLT_MAX       (3.402823466e+38f) // largest finite float
#define LOG2E         (1.442695f)        // log2(e) = 1/ln(2)

// ---------------------------------------------------------------------------
// Uniform buffer — shared across all 3-pass CS compute shaders.
// Declared here so each .compute file only needs to #include this header.
// ---------------------------------------------------------------------------

cbuffer SGSR2CSParams
{
    float2 InputInfo_ViewportSize;          // render resolution (width, height)
    float2 InputInfo_ViewportSizeInverse;   // 1 / render resolution
    float2 HistoryInfo_ViewportSize;        // display resolution (width, height)
    float2 HistoryInfo_ViewportSizeInverse; // 1 / display resolution
    float2 DepthInfo_ViewportSize;          // depth buffer resolution (usually == render res)
    float2 DepthInfo_ViewportSizeInverse;   // 1 / depth buffer resolution
    float2 OutputInfo_ViewportSize;         // output resolution (== display resolution)
    float2 OutputInfo_ViewportSizeInverse;  // 1 / output resolution
    float2 InputJitter;                     // current frame jitter in render-res pixels
    float4 DeviceToViewDepth;               // [A, B, tanHalfFovX, tanHalfFovY]
    float4x4 ReClipToPrevClip;              // prev_view_proj * inv(curr_view_proj)
    float  ValidReset;                      // 1.0 on reset frame, 0.0 otherwise
    float  JitterSeqLength;                 // length of jitter sequence (8 for Halton-8)
    float  Scalefactor;                     // history AABB box scale (pre-computed on CPU)
    float  Sharpness;                       // RCAS sharpness [1.0, 1.5] (sharpen pass only)
};


//Functions related to color space conversion
half3 Tonemap(half3 fRgb)
{
    return fRgb / (max(max((half)FLT_FP16_MIN, fRgb.r), max(fRgb.g, fRgb.b)) + 1.0h).xxx;
}

half3 InverseTonemap(half3 fRgb)
{
    fRgb = clamp(fRgb, (half)FLT_FP16_MIN, 0.999h);
    return fRgb / max((half)FLT_FP16_MIN/*fp16 inf fix*/, 1.0h - max(fRgb.r, max(fRgb.g, fRgb.b))).xxx;
}

half3 RGBToYCoCg(half3 RGB)
{
    half Y  = dot(RGB, half3( 0.25h,  0.5h,  0.25h));
    half Co = dot(RGB, half3( 0.5h,   0.0h, -0.5h));
    half Cg = dot(RGB, half3(-0.25h,  0.5h, -0.25h));
    return half3(Y, Co, Cg);
}

half3 YCoCgToRGB(half3 YCoCg)
{
    half R = dot(YCoCg, half3(1.0h,  1.0h, -1.0h));
    half G = dot(YCoCg, half3(1.0h,  0.0h,  1.0h));
    half B = dot(YCoCg, half3(1.0h, -1.0h, -1.0h));
    return half3(R, G, B);
}

//Functions related to coordinate calculation
int2 ClampLoad(int2 iPxSample, int2 iPxOffset, int2 iTextureSize)
{
    int2 result = iPxSample + iPxOffset;
    result.x = clamp(result.x, 0, iTextureSize.x - 1);
    result.y = clamp(result.y, 0, iTextureSize.y - 1);
    return result;
}

bool IsUvInside(float2 fUv)
{
    return (fUv.x >= 0.0f && fUv.x <= 1.0f) && (fUv.y >= 0.0f && fUv.y <= 1.0f);
}

#endif // SGSR2CS_COMMON_HLSL
