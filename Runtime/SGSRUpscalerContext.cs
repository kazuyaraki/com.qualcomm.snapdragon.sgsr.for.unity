// SPDX-License-Identifier: BSD-3-Clause

#if ENABLE_UPSCALER_FRAMEWORK && UNITY_6000_6_OR_NEWER
using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace Qualcomm.Snapdragon.SGSR
{
    // Reuse the existing renderer and history managers per camera. Unity controls cleanup,
    // including resolution changes and cameras that stop rendering.
    internal sealed class SGSRUpscalerContext<TUpscaler> : IUpscalerContext
        where TUpscaler : class, IDisposable
    {
        internal TUpscaler upscaler { get; private set; }
        public Vector2Int createdForDisplayResolution { get; }
        public int lastUsedFrame { get; set; }

        internal SGSRUpscalerContext(TUpscaler upscaler, Vector2Int displayResolution)
        {
            this.upscaler = upscaler;
            createdForDisplayResolution = displayResolution;
        }

        // SGSR options are read each frame and do not change the allocation layout.
        public bool IsValidForOptions(UpscalerOptions options) => upscaler != null;

        public void Cleanup(CommandBuffer cmd)
        {
            upscaler?.Dispose();
            upscaler = null;
        }
    }
}
#endif
