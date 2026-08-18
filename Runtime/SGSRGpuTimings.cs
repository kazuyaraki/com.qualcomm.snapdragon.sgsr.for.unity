//============================================================================================================
//
//
//                  Copyright (c) 2026 Qualcomm Innovation Center, Inc. All rights reserved.
//                              SPDX-License-Identifier: BSD-3-Clause
//
//============================================================================================================

namespace Qualcomm.Snapdragon.SGSR
{
    /// <summary>
    /// Per-frame GPU elapsed times (ms) for active SGSR passes, readable by external tools.
    /// Values are written by render passes each frame and reflect the previous frame's GPU work.
    /// Returns 0 when the pass is not active or GPU timing is unavailable on the platform.
    /// </summary>
    public static class SGSRGpuTimings
    {
        /// <summary>GPU elapsed time in milliseconds for the SGSR 1.0 spatial upscale pass.</summary>
        public static float sgsr1Ms;

        /// <summary>GPU elapsed time in milliseconds for the SGSR 2.0 two-pass Convert pass.</summary>
        public static float sgsr2ConvertMs;
        /// <summary>GPU elapsed time in milliseconds for the SGSR 2.0 two-pass Upscale pass.</summary>
        public static float sgsr2UpscaleMs;
        /// <summary>Total GPU elapsed time in milliseconds for all SGSR 2.0 two-pass passes (Convert + Upscale).</summary>
        public static float sgsr2Ms;

        /// <summary>GPU elapsed time in milliseconds for the SGSR 2.0 three-pass Convert pass.</summary>
        public static float sgsr2CsConvertMs;
        /// <summary>GPU elapsed time in milliseconds for the SGSR 2.0 three-pass Activate pass.</summary>
        public static float sgsr2CsActivateMs;
        /// <summary>GPU elapsed time in milliseconds for the SGSR 2.0 three-pass Upscale pass.</summary>
        public static float sgsr2CsUpscaleMs;
        /// <summary>GPU elapsed time in milliseconds for the SGSR 2.0 three-pass optional Sharpen pass.</summary>
        public static float sgsr2CsSharpenMs;
        /// <summary>Total GPU elapsed time in milliseconds for all SGSR 2.0 three-pass passes (Convert + Activate + Upscale + Sharpen).</summary>
        public static float sgsr2CsMs;
    }
}