//============================================================================================================
//
//
//                  Copyright (c) 2026 Qualcomm Innovation Center, Inc. All rights reserved.
//                              SPDX-License-Identifier: BSD-3-Clause
//
//============================================================================================================

Shader "Hidden/SGSR/Upscale"
{
    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }
        ZWrite Off ZTest Always Blend Off Cull Off

        Pass
        {
            Name "SGSR Upscale"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment SGSRFrag
            #pragma target 5.0

            #pragma multi_compile _ SGSR_USE_HALF_PRECISION
            #pragma multi_compile SGSR_QUALITY_MOBILE SGSR_QUALITY_HIGH SGSR_QUALITY_VR

            #define SGSR_GPU_CONFIG 1

            #if defined(SGSR_QUALITY_MOBILE)
                #define SGSR_TARGET SGSR_TARGET_MOBILE
            #elif defined(SGSR_QUALITY_HIGH)
                #define SGSR_TARGET SGSR_TARGET_HIGH_QUALITY
            #elif defined(SGSR_QUALITY_VR)
                #define SGSR_TARGET SGSR_TARGET_VR
            #else
                #error "Unknown SGSR quality definition"
            #endif

            // Edge detection threshold per quality mode.
            // Mobile uses a looser threshold (8/255); High Quality and VR use a tighter one (4/255).
            #define SGSR_EDGE_THRESHOLD_MOBILE     (8.0/255.0)
            #define SGSR_EDGE_THRESHOLD_HQ_VR      (4.0/255.0)
            // Suggested range [1.0, 2.0]
            #define SGSR_EDGE_SHARPNESS (2.0)

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

            // Declare input texture explicitly so AddBlitPass can bind it via MaterialPropertyBlock.
            Texture2D _SGSRInputTexture;
            #define InputTexture _SGSRInputTexture

            #include "sgsr.hlsl"

            float4 sgsr_Const1;

            half4 SGSRFrag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float2 uv = UnityStereoTransformScreenSpaceTex(input.texcoord);
                half4 outColor = half4(0, 0, 0, 0);
                SgsrYuvH(outColor, uv, sgsr_Const1);
                return outColor;
            }
            ENDHLSL
        }
    }
}
