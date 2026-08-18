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
        UpscalerRegistry.Register<Qualcomm.Snapdragon.SGSR.SGSR2IUpscaler, Qualcomm.Snapdragon.SGSR.SGSR2Options>(
            Qualcomm.Snapdragon.SGSR.SGSR2IUpscaler.UPSCALER_NAME);

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void InitRuntime() =>
        UpscalerRegistry.Register<Qualcomm.Snapdragon.SGSR.SGSR2IUpscaler, Qualcomm.Snapdragon.SGSR.SGSR2Options>(
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

        public override string GetName() => UPSCALER_NAME;

        public override bool IsTemporalUpscaler() => true;

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

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
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
            CoreUtils.Destroy(m_convertMaterial);
            m_convertMaterial = null;
            CoreUtils.Destroy(m_upscaleMaterial);
            m_upscaleMaterial = null;
            m_renderPass = null;

            m_historyManager?.Dispose();
            m_historyManager = null;

            SGSRDebugSettings.UnregisterDebugPanel();
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
