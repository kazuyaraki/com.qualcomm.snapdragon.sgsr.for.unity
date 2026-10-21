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
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.RenderGraphModule.Util;
using UnityEngine.Rendering.Universal;

namespace Qualcomm.Snapdragon.SGSR
{
    internal static class SGSRUpscalerUtils
    {
        internal static void Register<TUpscaler, TOptions>(string name)
            where TUpscaler : IUpscaler
            where TOptions : UpscalerOptions
        {
#if UNITY_6000_7_OR_NEWER
            UpscalerRegistry.Register<TUpscaler, TOptions>(name, name);
#else
            UpscalerRegistry.Register<TUpscaler, TOptions>(name);
#endif
        }

#if UNITY_6000_6_OR_NEWER
        internal static TOptions GetOptions<TOptions>() where TOptions : UpscalerOptions
        {
            // URP's Upscaling instance is internal. The pipeline asset exposes the same global options.
            foreach (var options in UniversalRenderPipeline.asset.upscalerOptions)
                if (options is TOptions typedOptions)
                    return typedOptions;
            return null;
        }

        internal static Vector2 CalculateJitter(int frameIndex, float upscaleRatio, bool enabled)
        {
            if (!enabled)
                return Vector2.zero;

            int phases = CalculateJitterPhaseCount(upscaleRatio);
            return new Vector2(
                HaltonSequence.Get(frameIndex % phases + 1, 2) - 0.5f,
                HaltonSequence.Get(frameIndex % phases + 1, 3) - 0.5f);
        }
#endif

        /// <summary>
        /// Returns the number of Halton jitter phases for a given upscale ratio.
        /// Higher ratios use more phases to ensure full sub-pixel coverage
        /// proportional to the upscale area.
        /// </summary>
        internal static int CalculateJitterPhaseCount(float upscaleRatio)
        {
            return Mathf.Max(1, (int)(8.0f * upscaleRatio * upscaleRatio));
        }

        // Squared Frobenius norm threshold on the rotation-only (rows 0-2, cols 0-2) delta of the
        // VP matrix. Translation changes every frame with smooth movement; only rotation jumps on
        // a hard cut.
        // For a 60° FOV camera, a 1°/frame rotation produces delta ≈ 0.004, so the old value of
        // 0.1 (delta > 0.01) was triggering on any pan faster than ~2°/frame (~120°/s at 60 fps).
        // A hard cut (scene teleport, etc.) jumps 45°+ per frame, producing delta ≫ 4.0.
        internal const float k_CameraCutThreshold = 2.0f;

        /// <summary>
        /// Computes render scale from UpscalingIO and forwards resolution info to the debug panel.
        /// </summary>
        internal static void UpdateDebugResolutionInfo(UpscalingIO io)
        {
            float renderScale = io.postUpscaleResolution.x > 0
                ? (float)io.preUpscaleResolution.x / io.postUpscaleResolution.x
                : 1.0f;
            SGSRDebugSettings.instance?.UpdateResolutionInfo(renderScale, io.preUpscaleResolution, io.postUpscaleResolution);
        }

        /// <summary>
        /// Returns true when the change in VP matrix rotation indicates a hard camera cut.
        /// </summary>
        internal static bool IsCameraCut(Matrix4x4 currVP, Matrix4x4 prevVP)
        {
            float delta = 0f;
            for (int row = 0; row < 3; row++)
            {
                Vector4 d = currVP.GetRow(row) - prevVP.GetRow(row);
                delta += Vector4.Dot(d, d);
            }
            return delta > k_CameraCutThreshold * k_CameraCutThreshold;
        }

        /// <summary>
        /// Blits the current camera color to a new texture at output resolution.
        /// Used for the Bilinear mode in SGSRDebugIUpscaler and by TryRecordPassthroughIfDisabled.
        /// </summary>
        internal static void RecordPassthrough(
            RenderGraph      renderGraph,
            ContextContainer frameData,
            string           passName)
        {
            UpscalingIO io = frameData.Get<UpscalingIO>();
            TextureDesc outputDesc = io.cameraColor.GetDescriptor(renderGraph);
            outputDesc.width       = io.postUpscaleResolution.x;
            outputDesc.height      = io.postUpscaleResolution.y;
            outputDesc.msaaSamples = MSAASamples.None;
            TextureHandle destination = renderGraph.CreateTexture(outputDesc);
            renderGraph.AddBlitPass(io.cameraColor, destination,
                scale: UnityEngine.Vector2.one,
                offset: UnityEngine.Vector2.zero,
                filterMode: RenderGraphUtils.BlitFilterMode.ClampBilinear,
                passName: passName);
            io.cameraColor = destination;
        }

        /// <summary>
        /// Returns true if SGSR is disabled via the debug panel and the caller should skip upscaling.
        /// When disabled, records a passthrough blit at output resolution and updates io.cameraColor,
        /// so the pipeline receives a correctly-sized texture regardless.
        /// </summary>
        internal static bool TryRecordPassthroughIfDisabled(
            RenderGraph      renderGraph,
            ContextContainer frameData,
            string           passName)
        {
            if (SGSRDebugSettings.instance?.enabled ?? true)
            {
                return false;
            }

            RecordPassthrough(renderGraph, frameData, passName);
            return true;
        }
    }
}
#endif // ENABLE_UPSCALER_FRAMEWORK