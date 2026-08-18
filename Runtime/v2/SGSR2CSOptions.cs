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
    /// Serialized options for the SGSR v2 three-pass compute shader upscaler.
    /// </summary>
    public class SGSR2CSOptions : UpscalerOptions
    {
        /// <summary>When true, enables the pixel-lock variant for thin feature preservation.</summary>
        [Tooltip("Enable pixel-lock for thin feature preservation.")]
        public bool enablePixelLock = false;

        /// <summary>When true, runs the optional RCAS sharpen pass after Upscale.</summary>
        [Tooltip("Enable the optional RCAS sharpen pass after Upscale.")]
        public bool enableSharpening = false;

        /// <summary>RCAS sharpness strength [1.0, 1.5]. Only used when enableSharpening is true.</summary>
        [Tooltip("RCAS sharpness strength [1.0, 1.5]. Only used when enableSharpening is true.")]
        [Range(1.0f, 1.5f)]
        public float sharpness = 1.12f;

        /// <summary>When true, uses the faster 5-tap Lanczos kernel instead of the 9-tap kernel.</summary>
        [Tooltip("Use 5-tap Lanczos kernel. When false, uses 9-tap (higher quality, more cost).")]
        public bool use5Tap = false;

        /// <summary>
        /// When true, detects hard camera cuts by comparing the rotation part of the view-projection
        /// matrix between frames and forces a history reset.
        /// </summary>
        [Tooltip("When enabled, detects hard camera cuts by comparing the rotation part of the view-projection " +
                 "matrix between frames and forces a history reset.\n\n" +
                 "Disable if you use Cinemachine (which sets resetHistory automatically on cuts) or if you " +
                 "manage camera cuts manually via UniversalAdditionalCameraData.resetHistory.")]
        public bool detectCameraCuts = true;

        /// <summary>When true, applies per-frame sub-pixel camera jitter to accumulate sub-pixel detail across frames.</summary>
        [Tooltip("Apply per-frame sub-pixel camera jitter to accumulate sub-pixel detail across frames. Disable if you see flickering on static cameras.")]
        public bool enableJitter = true;
    }
}
#endif // ENABLE_UPSCALER_FRAMEWORK
