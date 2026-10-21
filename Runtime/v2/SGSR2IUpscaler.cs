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
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering.RenderGraphModule;

#if UNITY_EDITOR
using UnityEditor;
#endif // UNITY_EDITOR

#if UNITY_EDITOR
[InitializeOnLoad]
#endif // UNITY_EDITOR
static class RegisterSGSR2
{
    static RegisterSGSR2() =>
        Qualcomm.Snapdragon.SGSR.SGSRUpscalerUtils.Register<Qualcomm.Snapdragon.SGSR.SGSR2IUpscaler, Qualcomm.Snapdragon.SGSR.SGSR2Options>(
            Qualcomm.Snapdragon.SGSR.SGSR2IUpscaler.UPSCALER_NAME);

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void InitRuntime() =>
        Qualcomm.Snapdragon.SGSR.SGSRUpscalerUtils.Register<Qualcomm.Snapdragon.SGSR.SGSR2IUpscaler, Qualcomm.Snapdragon.SGSR.SGSR2Options>(
            Qualcomm.Snapdragon.SGSR.SGSR2IUpscaler.UPSCALER_NAME);
}

namespace Qualcomm.Snapdragon.SGSR
{
    /// <summary>
    /// SGSR v2 implementation of Unity's AbstractUpscaler (IUpscaler framework).
    /// Two-pass temporal upscaler (Convert + Upscale) — requires depth texture, maintains per-camera history.
    /// </summary>
    public class SGSR2IUpscaler : AbstractUpscaler, IDisposable
    {
        public static readonly string UPSCALER_NAME = "SGSR 2.0 Temporal Upscaler (2-Pass FS)";

        private Material m_convertMaterial;
        private Material m_upscaleMaterial;
        private SGSR2RenderPass m_renderPass;
        private SGSR2HistoryManager m_historyManager;
        private SGSR2Options m_options;
        private Vector2 m_jitterOffset;
        private float   m_upscaleRatio = 1.0f;

        /// <summary>
        /// Initializes the upscaler with the given options asset.
        /// </summary>
        public SGSR2IUpscaler(SGSR2Options options)
        {
            m_options = options;
        }

#if UNITY_6000_6_OR_NEWER
        // Unity 6.6 creates a shared descriptor and owns the lifetime of camera contexts.
        public SGSR2IUpscaler() { }

        public override IUpscalerContext CreateContext(UpscalerOptions options, Vector2Int displayResolution)
        {
            return new SGSRUpscalerContext<SGSR2IUpscaler>(
                new SGSR2IUpscaler((SGSR2Options)options), displayResolution);
        }
#endif

#if UNITY_6000_7_OR_NEWER
        // Keep the previous serialized name as the ID when upgrading existing pipeline assets.
        public override string upscalerId => UPSCALER_NAME;
#endif
#if UNITY_6000_5_OR_NEWER
        public override string name => UPSCALER_NAME;
        public override bool isTemporal => true;
        public override bool supportsSharpening => false;
#else
        public override string GetName() => UPSCALER_NAME;

        public override bool IsTemporalUpscaler() => true;
#endif

#if UNITY_6000_6_OR_NEWER
        public override void CalculateJitter(int frameIndex, float upscaleRatio, out Vector2 jitter, out bool allowScaling)
        {
            // Jitter is queried before a camera context is acquired. URP currently uses global options.
            var options = SGSRUpscalerUtils.GetOptions<SGSR2Options>();
            bool enabled = options?.enableJitter ?? true;
            jitter = SGSRUpscalerUtils.CalculateJitter(frameIndex, upscaleRatio, enabled);
            allowScaling = false;
        }
#else
        public override void CalculateJitter(int frameIndex, out Vector2 jitter, out bool allowScaling)
        {
            if (m_options.enableJitter)
            {
                int phases = SGSRUpscalerUtils.CalculateJitterPhaseCount(m_upscaleRatio);
                m_jitterOffset = new Vector2(
                    HaltonSequence.Get(frameIndex % phases + 1, 2) - 0.5f,
                    HaltonSequence.Get(frameIndex % phases + 1, 3) - 0.5f
                );
                jitter = m_jitterOffset;
            }
            else
            {
                jitter = m_jitterOffset = Vector2.zero;
            }
            allowScaling = false;
        }

#endif

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
#if UNITY_6000_6_OR_NEWER
            UpscalingIO io = frameData.Get<UpscalingIO>();
            var context = (SGSRUpscalerContext<SGSR2IUpscaler>)io.context;
            context.upscaler.m_options = (SGSR2Options)io.options;
            context.upscaler.m_jitterOffset = io.subpixelJitter;
            context.upscaler.RecordCameraRenderGraph(renderGraph, frameData);
        }

        private void RecordCameraRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
#endif
            if (!EnsureInitialized())
            {
                return;
            }

            if (SGSRUpscalerUtils.TryRecordPassthroughIfDisabled(renderGraph, frameData, "SGSR2 Passthrough (Disabled)"))
            {
                return;
            }

            // Act on one-shot debug reset request before Setup reads history state.
            SGSRDebugSettings debugSettings = SGSRDebugSettings.instance;
            if (debugSettings != null && debugSettings.resetHistoryRequested)
            {
                UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
                m_historyManager.MarkReset(cameraData.camera);
                debugSettings.ConsumeResetHistoryRequest();
            }

            UpscalingIO io = frameData.Get<UpscalingIO>();
            SGSRUpscalerUtils.UpdateDebugResolutionInfo(io);
            if (io.postUpscaleResolution.x > 0)
            {
                m_upscaleRatio = (float)io.postUpscaleResolution.x / Mathf.Max(1, io.preUpscaleResolution.x);
            }

            m_renderPass.Setup(frameData, m_historyManager, m_jitterOffset, m_options);
            m_renderPass.RecordRenderGraph(renderGraph, frameData);
        }

        public void Dispose()
        {
#if UNITY_6000_6_OR_NEWER
            if (m_renderPass != null)
                SGSRDebugSettings.UnregisterDebugPanel();
#endif
            CoreUtils.Destroy(m_convertMaterial);
            m_convertMaterial = null;
            CoreUtils.Destroy(m_upscaleMaterial);
            m_upscaleMaterial = null;
            m_renderPass = null;

            m_historyManager?.Dispose();
            m_historyManager = null;

#if !UNITY_6000_6_OR_NEWER
            SGSRDebugSettings.UnregisterDebugPanel();
#endif
        }

        // ---- private helpers ----

        private bool EnsureInitialized()
        {
            if (m_renderPass != null)
            {
                return true;
            }

            var resources = GraphicsSettings.GetRenderPipelineSettings<SGSRRuntimeResources>();
            if (resources == null)
            {
                Debug.LogError("[SGSR2] SGSRRuntimeResources not found in GraphicsSettings.");
                return false;
            }

            if (resources.convertShader == null)
            {
                Debug.LogError("[SGSR2] SGSRRuntimeResources.convertShader is not assigned.");
                return false;
            }

            if (resources.upscaleShader == null)
            {
                Debug.LogError("[SGSR2] SGSRRuntimeResources.upscaleShader is not assigned.");
                return false;
            }

            m_convertMaterial = CoreUtils.CreateEngineMaterial(resources.convertShader);
            m_upscaleMaterial = CoreUtils.CreateEngineMaterial(resources.upscaleShader);
            m_historyManager = new SGSR2HistoryManager();
            m_renderPass = new SGSR2RenderPass(m_convertMaterial, m_upscaleMaterial);
            SGSRDebugSettings.RegisterDebugPanel();
            return true;
        }
    }
}
#endif // ENABLE_UPSCALER_FRAMEWORK
