//============================================================================================================
//
//
//                  Copyright (c) 2026 Qualcomm Innovation Center, Inc. All rights reserved.
//                              SPDX-License-Identifier: BSD-3-Clause
//
//============================================================================================================

#if ENABLE_UPSCALER_FRAMEWORK
using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

#if UNITY_EDITOR
using UnityEditor;
#endif // UNITY_EDITOR

#if UNITY_EDITOR
[InitializeOnLoad]
#endif // UNITY_EDITOR
static class RegisterSGSR
{
    static RegisterSGSR() =>
        UpscalerRegistry.Register<Qualcomm.Snapdragon.SGSR.SGSRIUpscaler, Qualcomm.Snapdragon.SGSR.SGSROptions>(
            Qualcomm.Snapdragon.SGSR.SGSRIUpscaler.UPSCALER_NAME);

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void InitRuntime() =>
        UpscalerRegistry.Register<Qualcomm.Snapdragon.SGSR.SGSRIUpscaler, Qualcomm.Snapdragon.SGSR.SGSROptions>(
            Qualcomm.Snapdragon.SGSR.SGSRIUpscaler.UPSCALER_NAME);
}

namespace Qualcomm.Snapdragon.SGSR
{
    /// <summary>
    /// SGSR v1 implementation of Unity's AbstractUpscaler (IUpscaler framework).
    /// Single-pass spatial upscaler — no inter-frame state, no depth requirement.
    /// </summary>
    public class SGSRIUpscaler : AbstractUpscaler, IDisposable
    {
        public static readonly string UPSCALER_NAME = "SGSR 1.0 Spatial Upscaler";

        private Material m_material;
        private SGSRRenderPass m_renderPass;
        private SGSROptions m_options;

        /// <summary>
        /// Initializes the upscaler with the given options asset.
        /// </summary>
        public SGSRIUpscaler(SGSROptions options)
        {
            m_options = options;
        }

        public override string GetName() => UPSCALER_NAME;

        // Return true so URP forces TAA on, which is required for UpscalerPostProcessPass to run.
        // The TAA pass itself is bypassed when activeUpscaler != null.
        public override bool IsTemporalUpscaler() => true;

#if SGSR_APPLIED_PATCH
        // SGSR v1 is spatial — motion vectors and depth copy are not needed.
        public override bool RequiresMotionVectors() => false;
#endif // SGSR_APPLIED_PATCH

        // SGSR v1 is spatial — no jitter needed.
        public override void CalculateJitter(int frameIndex, out Vector2 jitter, out bool allowScaling)
        {
            jitter = Vector2.zero;
            allowScaling = false;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            if (!EnsureInitialized())
            {
                return;
            }

            if (SGSRUpscalerUtils.TryRecordPassthroughIfDisabled(renderGraph, frameData, "SGSR Passthrough (Disabled)"))
            {
                return;
            }

            SGSRQuality quality = m_options?.quality ?? SGSRQuality.HighQuality;
            bool useHalfPrecision = m_options?.useHalfPrecision ?? true;

            UpscalingIO io = frameData.Get<UpscalingIO>();
            SGSRUpscalerUtils.UpdateDebugResolutionInfo(io);

            m_renderPass.Setup(quality, useHalfPrecision);
            m_renderPass.RecordRenderGraph(renderGraph, frameData);
        }

        public void Dispose()
        {
            CoreUtils.Destroy(m_material);
            m_material = null;
            m_renderPass = null;

            SGSRDebugSettings.UnregisterDebugPanel();
        }

        private bool EnsureInitialized()
        {
            if (m_renderPass != null)
            {
                return true;
            }

            var resources = GraphicsSettings.GetRenderPipelineSettings<SGSRRuntimeResources>();
            if (resources == null)
            {
                Debug.LogError("[SGSR] SGSRRuntimeResources not found in GraphicsSettings.");
                return false;
            }

            if (resources.spatialUpscaleShader == null)
            {
                Debug.LogError("[SGSR] SGSRRuntimeResources.spatialUpscaleShader is not assigned.");
                return false;
            }

            m_material = CoreUtils.CreateEngineMaterial(resources.spatialUpscaleShader);
            m_renderPass = new SGSRRenderPass(m_material);
            SGSRDebugSettings.RegisterDebugPanel();
            return true;
        }
    }
}
#endif // ENABLE_UPSCALER_FRAMEWORK
