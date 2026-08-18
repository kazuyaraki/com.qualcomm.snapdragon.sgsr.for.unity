//============================================================================================================
//
//
//                  Copyright (c) 2026 Qualcomm Innovation Center, Inc. All rights reserved.
//                              SPDX-License-Identifier: BSD-3-Clause
//
//============================================================================================================

#if ENABLE_UPSCALER_FRAMEWORK
using System;
using System.Collections.Generic;
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
static class RegisterSGSR2CS
{
    static RegisterSGSR2CS() =>
        UpscalerRegistry.Register<Qualcomm.Snapdragon.SGSR.SGSR2CSIUpscaler, Qualcomm.Snapdragon.SGSR.SGSR2CSOptions>(
            Qualcomm.Snapdragon.SGSR.SGSR2CSIUpscaler.UPSCALER_NAME);

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void InitRuntime() =>
        UpscalerRegistry.Register<Qualcomm.Snapdragon.SGSR.SGSR2CSIUpscaler, Qualcomm.Snapdragon.SGSR.SGSR2CSOptions>(
            Qualcomm.Snapdragon.SGSR.SGSR2CSIUpscaler.UPSCALER_NAME);
}

namespace Qualcomm.Snapdragon.SGSR
{
    /// <summary>
    /// SGSR v2 three-pass compute shader implementation of IUpscaler.
    ///
    /// Dispatches Convert → Activate → Upscale → (optional Sharpen) with history ping-pong.
    /// When enableSharpening is true, Upscale writes HistoryOutput only and the Sharpen pass
    /// reads it and writes SceneColorOutput. When false, Upscale writes both.
    /// io.cameraColor is set to SceneColorOutput in both cases.
    /// </summary>
    public class SGSR2CSIUpscaler : AbstractUpscaler, IDisposable
    {
        public static readonly string UPSCALER_NAME = "SGSR 2.0 Temporal Upscaler (3-Pass CS)";

        private SGSR2CSRenderPass    m_renderPass;
        private SGSR2CSHistoryManager m_historyManager;
        private SGSR2CSOptions        m_options;
        private Vector2               m_jitterOffset;
        private float                 m_upscaleRatio = 1.0f;

        // Per-camera state: previous view-projection matrix and reset flag.
        private Dictionary<EntityId, CameraState> m_cameraStates =
            new Dictionary<EntityId, CameraState>();

        private class CameraState
        {
            public Matrix4x4 prevViewProj = Matrix4x4.zero;
            public bool      needsReset   = true;
        }

        /// <summary>
        /// Initializes the upscaler with the given options asset.
        /// </summary>
        public SGSR2CSIUpscaler(SGSR2CSOptions options)
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

            if (SGSRUpscalerUtils.TryRecordPassthroughIfDisabled(renderGraph, frameData, "SGSR2CS Passthrough (Disabled)"))
            {
                return;
            }

            UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
            Camera   camera   = cameraData.camera;
            EntityId cameraId = camera.GetEntityId();

            if (!m_cameraStates.TryGetValue(cameraId, out CameraState camState))
            {
                camState = new CameraState();
                m_cameraStates[cameraId] = camState;
            }

            UpscalingIO io = frameData.Get<UpscalingIO>();
            SGSRUpscalerUtils.UpdateDebugResolutionInfo(io);
            if (io.postUpscaleResolution.x > 0)
            {
                m_upscaleRatio = (float)io.postUpscaleResolution.x / Mathf.Max(1, io.preUpscaleResolution.x);
            }

            // Detect camera cut via VP matrix delta.
            Matrix4x4 currVP = GL.GetGPUProjectionMatrix(camera.nonJitteredProjectionMatrix, true)
                               * camera.worldToCameraMatrix;

            if (io.resetHistory)
            {
                camState.needsReset = true;
            }
            else if (m_options.detectCameraCuts && !camState.needsReset && !camState.prevViewProj.Equals(Matrix4x4.zero)
                  && SGSRUpscalerUtils.IsCameraCut(currVP, camState.prevViewProj))
            {
                camState.needsReset = true;
            }

            float validReset = camState.needsReset ? 1.0f : 0.0f;

            // Get or allocate persistent history buffers for this camera.
            SGSR2CSHistoryState historyState = m_historyManager.GetOrCreate(camera, io.postUpscaleResolution);

            // If the history manager just allocated (needsReset was set), propagate to camState.
            if (historyState.needsReset)
            {
                camState.needsReset = true;
                validReset          = 1.0f;
            }

            m_renderPass.Setup(
                frameData,
                camState.prevViewProj,
                m_jitterOffset,
                m_options.enableJitter ? SGSRUpscalerUtils.CalculateJitterPhaseCount(m_upscaleRatio) : 8,
                validReset,
                m_options.enablePixelLock,
                m_options.use5Tap,
                m_options.enableSharpening,
                m_options.sharpness,
                historyState);

            camState.prevViewProj = currVP;
            camState.needsReset   = false;

            m_renderPass.RecordRenderGraph(renderGraph, frameData);

            // Swap ping-pong buffers after the pass is recorded.
            m_historyManager.Swap(camera);
        }

        public void Dispose()
        {
            m_renderPass = null;
            m_historyManager?.Dispose();
            m_historyManager = null;
            m_cameraStates.Clear();
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
                Debug.LogError("[SGSR2CS] SGSRRuntimeResources not found in GraphicsSettings.");
                return false;
            }

            if (resources.convertCS == null)
            {
                Debug.LogError("[SGSR2CS] SGSRRuntimeResources.convertCS is not assigned.");
                return false;
            }

            if (resources.activateCS == null)
            {
                Debug.LogError("[SGSR2CS] SGSRRuntimeResources.activateCS is not assigned.");
                return false;
            }

            if (resources.upscaleCS == null)
            {
                Debug.LogError("[SGSR2CS] SGSRRuntimeResources.upscaleCS is not assigned.");
                return false;
            }

            if (m_options.enableSharpening && resources.sharpenCS == null)
            {
                Debug.LogError("[SGSR2CS] SGSRRuntimeResources.sharpenCS is not assigned but enableSharpening is true.");
                return false;
            }

            m_historyManager = new SGSR2CSHistoryManager();
            m_renderPass     = new SGSR2CSRenderPass(
                resources.convertCS,
                resources.activateCS,
                resources.upscaleCS,
                resources.sharpenCS);
            SGSRDebugSettings.RegisterDebugPanel();
            return true;
        }
    }
}
#endif // ENABLE_UPSCALER_FRAMEWORK
