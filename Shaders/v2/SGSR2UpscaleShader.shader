//============================================================================================================
//
//
//                  Copyright (c) 2026, Qualcomm Innovation Center, Inc. All rights reserved.
//                              SPDX-License-Identifier: BSD-3-Clause
//
//============================================================================================================

Shader "Hidden/SGSR2/Upscale"
{
    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }
        ZWrite Off ZTest Always Blend Off Cull Off

        Pass
        {
            Name "SGSR2 Upscale"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragUpscale
            #pragma target 4.5

            #pragma multi_compile_local _ SGSR2_USE_5_TAP
            #pragma multi_compile_local _ SGSR2_LANCZOS_OPT
            #pragma multi_compile_local _ SGSR2_COMPUTE_THIN_FEATURE

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

            #if defined(SGSR2_USE_5_TAP)
                #define SAMPLE_NUMBER 1
            #else
                #define SAMPLE_NUMBER 0
            #endif
            #if defined(SGSR2_LANCZOS_OPT)
                #define LANCZOS_OPT 1
            #else
                #define LANCZOS_OPT 0
            #endif
            #if defined(SGSR2_COMPUTE_THIN_FEATURE)
                #define THIN_FEATURE 1
            #else
                #define THIN_FEATURE 0
            #endif

            #include "sgsr2_upscale.hlsl"

            ENDHLSL
        }
    }
}
