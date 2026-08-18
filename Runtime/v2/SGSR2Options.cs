//============================================================================================================
//
//
//                  Copyright (c) 2026 Qualcomm Innovation Center, Inc. All rights reserved.
//                              SPDX-License-Identifier: BSD-3-Clause
//
//============================================================================================================

#if ENABLE_UPSCALER_FRAMEWORK
using UnityEngine;
using UnityEngine.Rendering;

namespace Qualcomm.Snapdragon.SGSR
{
    /// <summary>
    /// Serialized options for the SGSR v2 temporal upscaler, stored as a sub-asset of the URP pipeline asset.
    /// </summary>
    public class SGSR2Options : UpscalerOptions
    {
        /// <summary>Floor on how much the current frame contributes to the temporal blend. Higher values reduce ghosting at the cost of more temporal noise. Only active when the camera is static.</summary>
        [Tooltip("Floor on how much the current frame contributes to the temporal blend. Higher values reduce ghosting at the cost of more temporal noise. Only active when the camera is static.")]
        public float minLerpContribution = 0.3f;

        /// <summary>When true, detects hard camera cuts and resets temporal history automatically.</summary>
        [Tooltip("Detect hard camera cuts and reset temporal history automatically. Disable if your project manages resets manually or via Cinemachine.")]
        public bool detectCameraCuts = true;

        /// <summary>When true, applies per-frame sub-pixel camera jitter to accumulate sub-pixel detail across frames.</summary>
        [Tooltip("Apply per-frame sub-pixel camera jitter to accumulate sub-pixel detail across frames. Disable if you see flickering on static cameras.")]
        public bool enableJitter = true;

        /// <summary>When true, uses a faster 5-tap kernel instead of the 9-tap kernel.</summary>
        [Tooltip("Use a 5-tap kernel instead of 9-tap. Faster with minimal quality difference; recommended for most cases.")]
        public bool use5Tap = true;

        /// <summary>When true, applies extra sharpening to thin features such as fine lines and hair.</summary>
        [Tooltip("Apply extra sharpening to thin features such as fine lines and hair. Small ALU cost increase.")]
        public bool computeThinFeature = false;
    }
}
#endif // ENABLE_UPSCALER_FRAMEWORK
