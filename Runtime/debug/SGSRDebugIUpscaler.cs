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
static class RegisterSGSRDebug
{
    static RegisterSGSRDebug() =>
        UpscalerRegistry.Register<Qualcomm.Snapdragon.SGSR.SGSRDebugIUpscaler, Qualcomm.Snapdragon.SGSR.SGSRDebugOptions>(
            Qualcomm.Snapdragon.SGSR.SGSRDebugIUpscaler.UPSCALER_NAME);

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void InitRuntime() =>
        UpscalerRegistry.Register<Qualcomm.Snapdragon.SGSR.SGSRDebugIUpscaler, Qualcomm.Snapdragon.SGSR.SGSRDebugOptions>(
            Qualcomm.Snapdragon.SGSR.SGSRDebugIUpscaler.UPSCALER_NAME);
}

namespace Qualcomm.Snapdragon.SGSR
{
    /// <summary>
    /// Debug upscaler that wraps all three SGSR variants (Spatial, 2-Pass FS, 3-Pass CS) and
    /// Bilinear passthrough in a single renderer feature. Select "SGSR Debug Upscaler" in the
    /// URP pipeline asset, then switch algorithms at runtime via the Rendering Debugger.
    /// </summary>
    public class SGSRDebugIUpscaler : AbstractUpscaler, IDisposable
    {
        public static readonly string UPSCALER_NAME = "SGSR Debug Upscaler";

        private SGSRDebugOptions m_options;
        private bool m_initialized;
        private SGSRDebugUpscalerMode m_activeMode;
        private Vector2 m_jitterOffset;
        private float m_upscaleRatio = 1.0f;

        // ---- Spatial (v1) ----
        private Material m_spatialMaterial;
        private SGSRRenderPass m_spatialPass;

        // ---- 2-Pass FS ----
        private Material m_twoPassConvertMaterial;
        private Material m_twoPassUpscaleMaterial;
        private SGSR2RenderPass m_twoPassPass;
        private SGSR2HistoryManager m_twoPassHistory;

        // ---- 3-Pass CS ----
        private SGSR2CSRenderPass m_threePassPass;
        private SGSR2CSHistoryManager m_threePassHistory;
        private Dictionary<EntityId, CSCameraState> m_cameraStates =
            new Dictionary<EntityId, CSCameraState>();

        private class CSCameraState
        {
            public Matrix4x4 prevViewProj = Matrix4x4.zero;
            public bool needsReset = true;
        }

        /// <summary>
        /// Initializes the debug upscaler with the given options asset.
        /// </summary>
        public SGSRDebugIUpscaler(SGSRDebugOptions options)
        {
            m_options = options;
        }

        public override string GetName() => UPSCALER_NAME;

        // Always true so URP enables the TAA/upscale path regardless of active variant.
        public override bool IsTemporalUpscaler() => true;

        public override void CalculateJitter(int frameIndex, out Vector2 jitter, out bool allowScaling)
        {
            if (m_activeMode == SGSRDebugUpscalerMode.Spatial || m_activeMode == SGSRDebugUpscalerMode.Bilinear)
            {
                jitter = m_jitterOffset = Vector2.zero;
                allowScaling = false;
                return;
            }

            bool enableJitter = m_activeMode == SGSRDebugUpscalerMode.TwoPassFS
                ? (m_options.twoPass?.enableJitter ?? true)
                : (m_options.cs?.enableJitter ?? true);

            if (!enableJitter)
            {
                jitter = m_jitterOffset = Vector2.zero;
                allowScaling = false;
                return;
            }

            int phases = SGSRUpscalerUtils.CalculateJitterPhaseCount(m_upscaleRatio);
            m_jitterOffset = new Vector2(
                HaltonSequence.Get(frameIndex % phases + 1, 2) - 0.5f,
                HaltonSequence.Get(frameIndex % phases + 1, 3) - 0.5f
            );
            jitter = m_jitterOffset;
            allowScaling = false;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            if (!EnsureInitialized())
            {
                return;
            }

            if (SGSRUpscalerUtils.TryRecordPassthroughIfDisabled(renderGraph, frameData, "SGSR Debug Passthrough (Disabled)"))
            {
                return;
            }

            SGSRDebugUpscalerMode targetMode = SGSRDebugSettings.instance?.upscalerOverride ?? m_options.defaultUpscaler;

            if (targetMode != m_activeMode)
            {
                OnModeSwitch(targetMode);
            }

            UpscalingIO io = frameData.Get<UpscalingIO>();
            float renderScale = io.postUpscaleResolution.x > 0
                ? (float)io.preUpscaleResolution.x / io.postUpscaleResolution.x
                : 1.0f;
            SGSRDebugSettings.instance?.UpdateResolutionInfo(renderScale, io.preUpscaleResolution, io.postUpscaleResolution);
            if (io.postUpscaleResolution.x > 0)
            {
                m_upscaleRatio = (float)io.postUpscaleResolution.x / Mathf.Max(1, io.preUpscaleResolution.x);
            }

            switch (m_activeMode)
            {
                case SGSRDebugUpscalerMode.Spatial:
                    RecordSpatial(renderGraph, frameData);
                    break;
                case SGSRDebugUpscalerMode.TwoPassFS:
                    RecordTwoPass(renderGraph, frameData);
                    break;
                case SGSRDebugUpscalerMode.ThreePassCS:
                    RecordThreePass(renderGraph, frameData);
                    break;
                case SGSRDebugUpscalerMode.Bilinear:
                    SGSRUpscalerUtils.RecordPassthrough(renderGraph, frameData, "SGSR Debug Bilinear Passthrough");
                    break;
            }
        }

        public void Dispose()
        {
            DisposeSpatial();
            DisposeTwoPass();
            DisposeThreePass();
            m_initialized = false;

            SGSRDebugSettings.UnregisterAsDebugUpscaler();
            SGSRDebugSettings.UnregisterDebugPanel();
        }

        // ---- initialization ----

        private bool EnsureInitialized()
        {
            if (m_initialized)
            {
                return true;
            }

            m_activeMode = m_options.defaultUpscaler;

            bool initOk;
            switch (m_activeMode)
            {
                case SGSRDebugUpscalerMode.Spatial:
                    initOk = EnsureSpatialInitialized();
                    break;
                case SGSRDebugUpscalerMode.TwoPassFS:
                    initOk = EnsureTwoPassInitialized();
                    break;
                case SGSRDebugUpscalerMode.ThreePassCS:
                    initOk = EnsureThreePassInitialized();
                    break;
                case SGSRDebugUpscalerMode.Bilinear:
                    initOk = true;
                    break;
                default:
                    initOk = false;
                    break;
            }

            if (!initOk)
            {
                return false;
            }

            m_initialized = true;
            SGSRDebugSettings.RegisterDebugPanel();
            SGSRDebugSettings.RegisterAsDebugUpscaler(m_options.defaultUpscaler);
            return true;
        }

        // ---- mode switching ----

        private void OnModeSwitch(SGSRDebugUpscalerMode newMode)
        {
            bool initOk;
            switch (newMode)
            {
                case SGSRDebugUpscalerMode.Spatial:
                    initOk = EnsureSpatialInitialized();
                    break;
                case SGSRDebugUpscalerMode.TwoPassFS:
                    initOk = EnsureTwoPassInitialized();
                    if (initOk)
                    {
                        // Dispose and recreate history to avoid temporal artifacts from the previous mode.
                        m_twoPassHistory?.Dispose();
                        m_twoPassHistory = new SGSR2HistoryManager();
                    }
                    break;
                case SGSRDebugUpscalerMode.ThreePassCS:
                    initOk = EnsureThreePassInitialized();
                    if (initOk)
                    {
                        m_threePassHistory?.Dispose();
                        m_threePassHistory = new SGSR2CSHistoryManager();
                        // Clear camera states so each camera resets on its first frame in the new mode.
                        m_cameraStates.Clear();
                    }
                    break;
                case SGSRDebugUpscalerMode.Bilinear:
                    initOk = true;
                    break;
                default:
                    initOk = false;
                    break;
            }

            if (initOk)
            {
                m_activeMode = newMode;
            }
        }

        // ---- resources helper ----

        private SGSRRuntimeResources GetResources()
        {
            var resources = GraphicsSettings.GetRenderPipelineSettings<SGSRRuntimeResources>();
            if (resources == null)
            {
                Debug.LogError("[SGSR Debug] SGSRRuntimeResources not found in GraphicsSettings.");
            }
            return resources;
        }

        // ---- spatial ----

        private bool EnsureSpatialInitialized()
        {
            if (m_spatialPass != null)
            {
                return true;
            }

            if (m_options.spatial == null)
            {
                Debug.LogError("[SGSR Debug] SGSRDebugOptions.spatial is not initialized.");
                return false;
            }

            var resources = GetResources();
            if (resources == null)
            {
                return false;
            }

            if (resources.spatialUpscaleShader == null)
            {
                Debug.LogError("[SGSR Debug] SGSRRuntimeResources.spatialUpscaleShader is not assigned.");
                return false;
            }

            m_spatialMaterial = CoreUtils.CreateEngineMaterial(resources.spatialUpscaleShader);
            m_spatialPass = new SGSRRenderPass(m_spatialMaterial);
            return true;
        }

        private void DisposeSpatial()
        {
            CoreUtils.Destroy(m_spatialMaterial);
            m_spatialMaterial = null;
            m_spatialPass = null;
        }

        private void RecordSpatial(RenderGraph renderGraph, ContextContainer frameData)
        {
            if (!EnsureSpatialInitialized())
            {
                return;
            }

            m_spatialPass.Setup(m_options.spatial.quality, m_options.spatial.useHalfPrecision);
            m_spatialPass.RecordRenderGraph(renderGraph, frameData);
        }

        // ---- two-pass FS ----

        private bool EnsureTwoPassInitialized()
        {
            if (m_twoPassPass != null)
            {
                return true;
            }

            if (m_options.twoPass == null)
            {
                Debug.LogError("[SGSR Debug] SGSRDebugOptions.twoPass is not initialized.");
                return false;
            }

            var resources = GetResources();
            if (resources == null)
            {
                return false;
            }

            if (resources.convertShader == null)
            {
                Debug.LogError("[SGSR Debug] SGSRRuntimeResources.convertShader is not assigned.");
                return false;
            }

            if (resources.upscaleShader == null)
            {
                Debug.LogError("[SGSR Debug] SGSRRuntimeResources.upscaleShader is not assigned.");
                return false;
            }

            m_twoPassConvertMaterial = CoreUtils.CreateEngineMaterial(resources.convertShader);
            m_twoPassUpscaleMaterial = CoreUtils.CreateEngineMaterial(resources.upscaleShader);
            m_twoPassHistory = new SGSR2HistoryManager();
            m_twoPassPass = new SGSR2RenderPass(m_twoPassConvertMaterial, m_twoPassUpscaleMaterial);
            return true;
        }

        private void DisposeTwoPass()
        {
            CoreUtils.Destroy(m_twoPassConvertMaterial);
            m_twoPassConvertMaterial = null;
            CoreUtils.Destroy(m_twoPassUpscaleMaterial);
            m_twoPassUpscaleMaterial = null;
            m_twoPassPass = null;
            m_twoPassHistory?.Dispose();
            m_twoPassHistory = null;
        }

        private void RecordTwoPass(RenderGraph renderGraph, ContextContainer frameData)
        {
            if (!EnsureTwoPassInitialized())
            {
                return;
            }

            SGSRDebugSettings debugSettings = SGSRDebugSettings.instance;
            if (debugSettings != null && debugSettings.resetHistoryRequested)
            {
                UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
                m_twoPassHistory.MarkReset(cameraData.camera);
                debugSettings.ConsumeResetHistoryRequest();
            }

            m_twoPassPass.Setup(frameData, m_twoPassHistory, m_jitterOffset, m_options.twoPass);
            m_twoPassPass.RecordRenderGraph(renderGraph, frameData);
        }

        // ---- three-pass CS ----

        private bool EnsureThreePassInitialized()
        {
            if (m_threePassPass != null)
            {
                return true;
            }

            if (m_options.cs == null)
            {
                Debug.LogError("[SGSR Debug] SGSRDebugOptions.cs is not initialized.");
                return false;
            }

            var resources = GetResources();
            if (resources == null)
            {
                return false;
            }

            if (resources.convertCS == null)
            {
                Debug.LogError("[SGSR Debug] SGSRRuntimeResources.convertCS is not assigned.");
                return false;
            }

            if (resources.activateCS == null)
            {
                Debug.LogError("[SGSR Debug] SGSRRuntimeResources.activateCS is not assigned.");
                return false;
            }

            if (resources.upscaleCS == null)
            {
                Debug.LogError("[SGSR Debug] SGSRRuntimeResources.upscaleCS is not assigned.");
                return false;
            }

            if (m_options.cs?.enableSharpening == true && resources.sharpenCS == null)
            {
                Debug.LogError("[SGSR Debug] SGSRRuntimeResources.sharpenCS is not assigned but cs.enableSharpening is true.");
                return false;
            }

            m_threePassHistory = new SGSR2CSHistoryManager();
            m_threePassPass = new SGSR2CSRenderPass(
                resources.convertCS,
                resources.activateCS,
                resources.upscaleCS,
                resources.sharpenCS);
            return true;
        }

        private void DisposeThreePass()
        {
            m_threePassPass = null;
            m_threePassHistory?.Dispose();
            m_threePassHistory = null;
            m_cameraStates.Clear();
        }

        private void RecordThreePass(RenderGraph renderGraph, ContextContainer frameData)
        {
            if (!EnsureThreePassInitialized())
            {
                return;
            }

            UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
            Camera camera = cameraData.camera;
            EntityId cameraId = camera.GetEntityId();

            if (!m_cameraStates.TryGetValue(cameraId, out CSCameraState camState))
            {
                camState = new CSCameraState();
                m_cameraStates[cameraId] = camState;
            }

            UpscalingIO io = frameData.Get<UpscalingIO>();

            Matrix4x4 currVP = GL.GetGPUProjectionMatrix(camera.nonJitteredProjectionMatrix, true)
                               * camera.worldToCameraMatrix;

            if (io.resetHistory)
            {
                camState.needsReset = true;
            }
            else if (m_options.cs.detectCameraCuts && !camState.needsReset && !camState.prevViewProj.Equals(Matrix4x4.zero)
                  && SGSRUpscalerUtils.IsCameraCut(currVP, camState.prevViewProj))
            {
                camState.needsReset = true;
            }

            float validReset = camState.needsReset ? 1.0f : 0.0f;

            SGSR2CSHistoryState historyState = m_threePassHistory.GetOrCreate(camera, io.postUpscaleResolution);

            if (historyState.needsReset)
            {
                camState.needsReset = true;
                validReset = 1.0f;
            }

            m_threePassPass.Setup(
                frameData,
                camState.prevViewProj,
                m_jitterOffset,
                m_options.cs.enableJitter ? SGSRUpscalerUtils.CalculateJitterPhaseCount(m_upscaleRatio) : 8,
                validReset,
                m_options.cs.enablePixelLock,
                m_options.cs.use5Tap,
                m_options.cs.enableSharpening,
                m_options.cs.sharpness,
                historyState);

            camState.prevViewProj = currVP;
            camState.needsReset = false;

            m_threePassPass.RecordRenderGraph(renderGraph, frameData);
            m_threePassHistory.Swap(camera);
        }
    }
}
#endif // ENABLE_UPSCALER_FRAMEWORK
