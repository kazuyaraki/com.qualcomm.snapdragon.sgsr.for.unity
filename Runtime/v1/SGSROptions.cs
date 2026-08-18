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
    public enum SGSRQuality
    {
        /// <summary>Simplified Lanczos2 filter using green channel as luminance. No edge-direction awareness. Fastest; suited for performance-constrained devices. Edge threshold: 8/255.</summary>
        Mobile,
        /// <summary>Full edge-direction detection with dual-pass weighted filtering (edge + smooth blended 60/40). Best output quality. Edge threshold: 4/255.</summary>
        HighQuality,
        /// <summary>Direction-aware filtering like High Quality, but clamps luminance correction to ±23/255 to prevent over-sharpening in high-contrast areas. Recommended for VR headsets.</summary>
        VR
    }

    /// <summary>
    /// Serialized options for the SGSR v1 upscaler, stored as a sub-asset of the URP pipeline asset.
    /// </summary>
    public class SGSROptions : UpscalerOptions
    {
        /// <summary>Selects the SGSR upscaling algorithm variant.</summary>
        [Tooltip("Selects the SGSR upscaling algorithm variant.\n\n" +
                 "• High Quality — Full edge-direction detection with dual-pass weighted filtering (edge + smooth blended 60/40). Best output quality. Edge threshold: 4/255.\n\n" +
                 "• Mobile — Simplified Lanczos2 filter using green channel as luminance. No edge-direction awareness. Fastest; suited for performance-constrained devices. Edge threshold: 8/255.\n\n" +
                 "• VR — Direction-aware filtering like High Quality, but clamps luminance correction to ±23/255 to prevent over-sharpening in high-contrast areas. Recommended for VR headsets.\n\n" +
                 "Render scale is controlled separately via the Render Scale slider on the URP pipeline asset.")]
        public SGSRQuality quality = SGSRQuality.HighQuality;

        /// <summary>When true, uses 16-bit (half) precision floats in the upscale shader.</summary>
        [Tooltip("Use 16-bit (half) precision floats in the upscale shader (min16float / mediump).\n\n" +
                 "Recommended on mobile and Adreno GPUs where 16-bit arithmetic is natively faster. " +
                 "Disable on desktop GPUs where full 32-bit precision is preferred or if you observe banding artifacts.")]
        public bool useHalfPrecision = true;
    }
}
#endif // ENABLE_UPSCALER_FRAMEWORK
