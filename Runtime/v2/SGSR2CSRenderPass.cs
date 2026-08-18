//============================================================================================================
//
//
//                  Copyright (c) 2026 Qualcomm Innovation Center, Inc. All rights reserved.
//                              SPDX-License-Identifier: BSD-3-Clause
//
//============================================================================================================

#if ENABLE_UPSCALER_FRAMEWORK
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering.RenderGraphModule;

namespace Qualcomm.Snapdragon.SGSR
{
    /// <summary>
    /// SGSR v2 three-pass compute shader render graph pass.
    ///
    /// Convert + Activate + Upscale + optional Sharpen passes with history ping-pong.
    ///   When enableSharpening is true, Upscale writes HistoryOutput only (SHARPENING_ON keyword set)
    ///   and the Sharpen pass reads HistoryOutput and writes SceneColorOutput.
    ///   When enableSharpening is false, Upscale writes both HistoryOutput and SceneColorOutput.
    ///   io.cameraColor is set to SceneColorOutput in both cases.
    ///   ValidReset = 1.0 on the first frame (or after a camera cut); 0.0 thereafter.
    /// </summary>
    internal class SGSR2CSRenderPass : ScriptableRenderPass
    {
        // ---- Pass data structs ----

        private class ConvertPassData
        {
            public TextureHandle inputColor;
            public TextureHandle inputDepth;
            public TextureHandle inputVelocity;
            public TextureHandle yCoCgLuma;
            public TextureHandle dilatedMotionDepthLuma;
            public ComputeShader convertCS;
            public int           convertKernel;
            public Vector2Int    renderSize;
            public Vector2Int    outputSize;
            public Matrix4x4     reClipToPrevClip;
            public Vector4       deviceToViewDepth;
            public Vector2       inputJitter;
            public float         jitterSeqLength;
            public float         validReset;
        }

        private class ActivatePassData
        {
            public TextureHandle dilatedMotionDepthLuma;
            public TextureHandle reactiveMask;
            public TextureHandle newLocks;
            public ComputeShader activateCS;
            public int           activateKernel;
            public Vector2Int    renderSize;
            public Vector2Int    outputSize;
            public Vector4       deviceToViewDepth;
            public Vector2       inputJitter;
            public float         jitterSeqLength;
            public float         validReset;
            public bool          enablePixelLock;
        }

        private class UpscalePassData
        {
            public TextureHandle yCoCgLuma;
            public TextureHandle dilatedMotionDepthLuma;
            public TextureHandle reactiveMask;
            public TextureHandle prevHistoryOutput;
            public TextureHandle historyOutput;
            public TextureHandle sceneColorOutput;
            public TextureHandle newLocks;
            public ComputeShader upscaleCS;
            public int           upscaleKernel;
            public Vector2Int    renderSize;
            public Vector2Int    outputSize;
            public Vector2       inputJitter;
            public float         jitterSeqLength;
            public float         validReset;
            public bool          enablePixelLock;
            public float         sharpness;
        }

        private class DebugBlitPassData
        {
            public TextureHandle source;
            public TextureHandle dest;
        }

        private class SharpenPassData
        {
            public TextureHandle  input;
            public TextureHandle  upscaledOutput;
            public ComputeShader  sharpenCS;
            public int            sharpenKernel;
            public Vector2Int     outputSize;
            public float          sharpness;
        }

        // ---- Compute shader kernel indices ----

        private readonly ComputeShader m_convertCS;
        private readonly int           m_convertKernel;

        private readonly ComputeShader m_activateCS;
        private readonly int           m_activateKernel;

        private readonly ComputeShader m_upscaleCS;
        private readonly int           m_upscaleKernel;

        // Sharpen pass (optional; null when enableSharpening is false).
        private readonly ComputeShader m_sharpenCS;
        private readonly int           m_sharpenKernel;

        // ---- Shader property IDs ----

        // Textures
        private static readonly int k_InputColor               = Shader.PropertyToID("InputColor");
        private static readonly int k_InputDepth               = Shader.PropertyToID("InputDepth");
        private static readonly int k_InputVelocity            = Shader.PropertyToID("InputVelocity");
        private static readonly int k_YCoCgLuma                = Shader.PropertyToID("YCoCgLuma");
        private static readonly int k_DilatedMotionDepthLuma   = Shader.PropertyToID("DilatedMotionDepthLuma");
        private static readonly int k_ReactiveMask             = Shader.PropertyToID("ReactiveMask");
        private static readonly int k_NewLocks                 = Shader.PropertyToID("NewLocks");
        private static readonly int k_PrevHistoryOutput        = Shader.PropertyToID("PrevHistoryOutput");
        private static readonly int k_HistoryOutput            = Shader.PropertyToID("HistoryOutput");
        private static readonly int k_SceneColorOutput         = Shader.PropertyToID("SceneColorOutput");

        // Sharpen pass texture names.
        private static readonly int k_Input                    = Shader.PropertyToID("Input");
        private static readonly int k_UpscaledOutput           = Shader.PropertyToID("UpscaledOutput");

        // Uniforms (SGSR2CSParams cbuffer)
        private static readonly int k_InputInfo_ViewportSize          = Shader.PropertyToID("InputInfo_ViewportSize");
        private static readonly int k_InputInfo_ViewportSizeInverse   = Shader.PropertyToID("InputInfo_ViewportSizeInverse");
        private static readonly int k_HistoryInfo_ViewportSize        = Shader.PropertyToID("HistoryInfo_ViewportSize");
        private static readonly int k_HistoryInfo_ViewportSizeInverse = Shader.PropertyToID("HistoryInfo_ViewportSizeInverse");
        private static readonly int k_DepthInfo_ViewportSize          = Shader.PropertyToID("DepthInfo_ViewportSize");
        private static readonly int k_DepthInfo_ViewportSizeInverse   = Shader.PropertyToID("DepthInfo_ViewportSizeInverse");
        private static readonly int k_OutputInfo_ViewportSize         = Shader.PropertyToID("OutputInfo_ViewportSize");
        private static readonly int k_OutputInfo_ViewportSizeInverse  = Shader.PropertyToID("OutputInfo_ViewportSizeInverse");
        private static readonly int k_InputJitter                     = Shader.PropertyToID("InputJitter");
        private static readonly int k_DeviceToViewDepth               = Shader.PropertyToID("DeviceToViewDepth");
        private static readonly int k_ReClipToPrevClip                = Shader.PropertyToID("ReClipToPrevClip");
        private static readonly int k_ValidReset                      = Shader.PropertyToID("ValidReset");
        private static readonly int k_JitterSeqLength                 = Shader.PropertyToID("JitterSeqLength");
        private static readonly int k_Scalefactor                     = Shader.PropertyToID("Scalefactor");
        private static readonly int k_Sharpness                       = Shader.PropertyToID("Sharpness");

        // ---- Profiling samplers ----

        // Pass names used by AddUnsafePass — kept as strings so CPU ProfilerRecorder in perf automation
        // continues to record them by name without colliding with the GPU scope samplers below.
        private const string k_ConvertSampler   = "SGSR2CS Convert";
        private const string k_ActivateSampler  = "SGSR2CS Activate";
        private const string k_UpscaleSampler   = "SGSR2CS Upscale";
        private const string k_SharpenSampler   = "SGSR2CS Sharpen";
        private const string k_DebugBlitSampler = "SGSR2CS DebugBlit";

        // GPU timing scopes — distinct names avoid colliding with the pass names above so that
        // CPU ProfilerRecorder doesn't double-count. enableRecording is set only on these
        // (not on any outer wrapper) to avoid monopolizing the GPU timestamp budget on the device.
        private static readonly ProfilingSampler k_ConvertGpuScope  = new ProfilingSampler("SGSR2CS Convert.GPU");
        private static readonly ProfilingSampler k_ActivateGpuScope = new ProfilingSampler("SGSR2CS Activate.GPU");
        private static readonly ProfilingSampler k_UpscaleGpuScope  = new ProfilingSampler("SGSR2CS Upscale.GPU");
        private static readonly ProfilingSampler k_SharpenGpuScope  = new ProfilingSampler("SGSR2CS Sharpen.GPU");

        static SGSR2CSRenderPass()
        {
            k_ConvertGpuScope.enableRecording  = true;
            k_ActivateGpuScope.enableRecording = true;
            k_UpscaleGpuScope.enableRecording  = true;
            k_SharpenGpuScope.enableRecording  = true;
        }

        // ---- Per-frame cached data (written by Setup, read by RecordRenderGraph) ----

        private Vector2Int   m_renderSize;
        private Vector2Int   m_outputSize;
        private Matrix4x4    m_reClipToPrevClip;
        private Vector4      m_deviceToViewDepth;
        private Vector2      m_inputJitter;
        private float        m_jitterSeqLength;
        private float        m_validReset;
        private bool         m_enablePixelLock;
        private bool         m_use5Tap;
        private bool         m_enableSharpening;
        private float        m_sharpness;
        private Camera       m_camera;

        // ---- History state (injected each frame by SGSR2CSIUpscaler) ----

        private SGSR2CSHistoryState m_historyState;

        // ---- Constructor ----

        /// <summary>
        /// Initializes the render pass with the three (or four) compute shader assets.
        /// </summary>
        public SGSR2CSRenderPass(
            ComputeShader convertCS,
            ComputeShader activateCS,
            ComputeShader upscaleCS,
            ComputeShader sharpenCS)
        {
            m_convertCS      = convertCS;
            m_convertKernel  = convertCS.FindKernel("MainCS");
            m_activateCS     = activateCS;
            m_activateKernel = activateCS.FindKernel("MainCS");
            m_upscaleCS      = upscaleCS;
            m_upscaleKernel  = upscaleCS.FindKernel("MainCS");
            m_sharpenCS      = sharpenCS;
            m_sharpenKernel  = sharpenCS != null ? sharpenCS.FindKernel("MainCS") : -1;
            renderPassEvent  = RenderPassEvent.AfterRenderingPostProcessing;
        }

        // ---- Setup (called each frame before RecordRenderGraph) ----

        /// <summary>
        /// Computes per-frame uniforms and injects the history state.
        /// Must be called before RecordRenderGraph.
        /// </summary>
        public void Setup(
            ContextContainer    frameData,
            Matrix4x4           prevViewProj,
            Vector2             inputJitter,
            int                 jitterPhaseCount,
            float               validReset,
            bool                enablePixelLock,
            bool                use5Tap,
            bool                enableSharpening,
            float               sharpness,
            SGSR2CSHistoryState historyState)
        {
            UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
            m_camera = cameraData.camera;

            UpscalingIO io = frameData.Get<UpscalingIO>();
            m_renderSize = io.preUpscaleResolution;
            m_outputSize = io.postUpscaleResolution;

            // Reprojection matrix: prev_view_proj * inv(curr_view_proj)
            Matrix4x4 currVP = GL.GetGPUProjectionMatrix(m_camera.nonJitteredProjectionMatrix, true)
                               * m_camera.worldToCameraMatrix;
            Matrix4x4 prevVP = prevViewProj.Equals(Matrix4x4.zero) ? currVP : prevViewProj;
            m_reClipToPrevClip = prevVP * currVP.inverse;

            // DeviceToViewDepth: [A, B, tanHalfFovX, tanHalfFovY]
            Matrix4x4 proj = GL.GetGPUProjectionMatrix(m_camera.nonJitteredProjectionMatrix, true);
            m_deviceToViewDepth = new Vector4(
                proj[2, 2],
                proj[2, 3],
                1.0f / proj[0, 0],
                1.0f / proj[1, 1]);

            m_inputJitter      = inputJitter;
            m_jitterSeqLength  = (float)jitterPhaseCount;
            m_validReset       = validReset;
            m_enablePixelLock  = enablePixelLock;
            m_use5Tap          = use5Tap;
            m_enableSharpening = enableSharpening && m_sharpenCS != null;
            m_sharpness        = sharpness;
            m_historyState     = historyState;
        }

        // ---- RecordRenderGraph ----

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            SGSRGpuTimings.sgsr2CsConvertMs  = k_ConvertGpuScope.gpuElapsedTime;
            SGSRGpuTimings.sgsr2CsActivateMs = k_ActivateGpuScope.gpuElapsedTime;
            SGSRGpuTimings.sgsr2CsUpscaleMs  = k_UpscaleGpuScope.gpuElapsedTime;
            SGSRGpuTimings.sgsr2CsSharpenMs  = k_SharpenGpuScope.gpuElapsedTime;
            SGSRGpuTimings.sgsr2CsMs         = SGSRGpuTimings.sgsr2CsConvertMs
                                             + SGSRGpuTimings.sgsr2CsActivateMs
                                             + SGSRGpuTimings.sgsr2CsUpscaleMs
                                             + SGSRGpuTimings.sgsr2CsSharpenMs;

            UpscalingIO io                     = frameData.Get<UpscalingIO>();
            UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();

            // ---- Create transient intermediate textures ----

            TextureHandle yCoCgLuma = renderGraph.CreateTexture(new TextureDesc(m_renderSize.x, m_renderSize.y)
            {
                colorFormat       = GraphicsFormat.R16G16B16A16_SFloat,
                filterMode        = FilterMode.Bilinear,
                wrapMode          = TextureWrapMode.Clamp,
                msaaSamples       = MSAASamples.None,
                enableRandomWrite = true,
                name              = "SGSR2CS_YCoCgLuma",
            });

            TextureHandle dilatedMotionDepthLuma = renderGraph.CreateTexture(new TextureDesc(m_renderSize.x, m_renderSize.y)
            {
                colorFormat       = GraphicsFormat.R16G16B16A16_SFloat,
                filterMode        = FilterMode.Bilinear,
                wrapMode          = TextureWrapMode.Clamp,
                msaaSamples       = MSAASamples.None,
                enableRandomWrite = true,
                name              = "SGSR2CS_DilatedMotionDepthLuma",
            });

            TextureHandle reactiveMask = renderGraph.CreateTexture(new TextureDesc(m_renderSize.x, m_renderSize.y)
            {
                colorFormat       = SystemInfo.IsFormatSupported(GraphicsFormat.R16_SFloat, GraphicsFormatUsage.LoadStore)
                    ? GraphicsFormat.R16_SFloat
                    : GraphicsFormat.R32_SFloat,
                filterMode        = FilterMode.Bilinear,
                wrapMode          = TextureWrapMode.Clamp,
                msaaSamples       = MSAASamples.None,
                enableRandomWrite = true,
                name              = "SGSR2CS_ReactiveMask",
            });

            // ---- Import persistent history textures ----

            var resetImportParams = new ImportResourceParams
            {
                clearOnFirstUse  = m_historyState.needsReset,
                clearColor       = Color.black,
                discardOnLastUse = false,
            };
            TextureHandle prevHistoryOutput = renderGraph.ImportTexture(m_historyState.prevHistoryOutput, resetImportParams);
            TextureHandle historyOutput     = renderGraph.ImportTexture(m_historyState.historyOutput,     resetImportParams);
            TextureHandle newLocks          = renderGraph.ImportTexture(m_historyState.newLocks,          resetImportParams);

            SetKeyword(m_activateCS, "PIXEL_LOCK_ON", m_enablePixelLock);
            SetKeyword(m_upscaleCS,  "PIXEL_LOCK_ON", m_enablePixelLock);

            // When SHARPENING_ON is set, Upscale skips writing SceneColorOutput; the Sharpen pass writes it.
            SetKeyword(m_upscaleCS, "SHARPENING_ON", m_enableSharpening);

            // FIVE_TAP_ON: 5 samples (better performance); off = 9 samples (better quality).
            SetKeyword(m_upscaleCS, "FIVE_TAP_ON", m_use5Tap);

            // SceneColorOutput: display-res transient UAV.
            // When sharpening is disabled: Upscale writes the final color here.
            // When sharpening is enabled:  Upscale skips this write (DO_SHARPENING==1);
            //   the Sharpen pass writes the final color to UpscaledOutput instead.
            //   We still create and bind SceneColorOutput so the Upscale shader compiles cleanly.
            TextureHandle sceneColorOutput = renderGraph.CreateTexture(new TextureDesc(m_outputSize.x, m_outputSize.y)
            {
                colorFormat       = GraphicsFormat.R16G16B16A16_SFloat,
                filterMode        = FilterMode.Bilinear,
                wrapMode          = TextureWrapMode.Clamp,
                msaaSamples       = MSAASamples.None,
                enableRandomWrite = true,
                name              = "SGSR2CS_SceneColorOutput",
            });

            // UpscaledOutput: the Sharpen pass writes the final sharpened color here.
            // Only created when sharpening is enabled.
            TextureHandle upscaledOutput = m_enableSharpening
                ? renderGraph.CreateTexture(new TextureDesc(m_outputSize.x, m_outputSize.y)
                {
                    colorFormat       = GraphicsFormat.R16G16B16A16_SFloat,
                    filterMode        = FilterMode.Bilinear,
                    wrapMode          = TextureWrapMode.Clamp,
                    msaaSamples       = MSAASamples.None,
                    enableRandomWrite = true,
                    name              = "SGSR2CS_UpscaledOutput",
                })
                : TextureHandle.nullHandle;

            // ---- Convert pass ----

            renderGraph.BeginProfilingSampler(k_ConvertGpuScope);
            using (IUnsafeRenderGraphBuilder builder = renderGraph.AddUnsafePass<ConvertPassData>(
                k_ConvertSampler, out ConvertPassData convertData))
            {
                convertData.inputColor             = io.cameraColor;
                convertData.inputDepth             = resourceData.cameraDepth;
                convertData.inputVelocity          = resourceData.motionVectorColor;
                convertData.yCoCgLuma              = yCoCgLuma;
                convertData.dilatedMotionDepthLuma = dilatedMotionDepthLuma;
                convertData.convertCS              = m_convertCS;
                convertData.convertKernel          = m_convertKernel;
                convertData.renderSize             = m_renderSize;
                convertData.outputSize             = m_outputSize;
                convertData.reClipToPrevClip       = m_reClipToPrevClip;
                convertData.deviceToViewDepth      = m_deviceToViewDepth;
                convertData.inputJitter            = m_inputJitter;
                convertData.jitterSeqLength        = m_jitterSeqLength;
                convertData.validReset             = m_validReset;

                builder.UseTexture(io.cameraColor);
                builder.UseTexture(resourceData.cameraDepth);
                if (resourceData.motionVectorColor.IsValid())
                {
                    builder.UseTexture(resourceData.motionVectorColor);
                }
                builder.UseTexture(yCoCgLuma,              AccessFlags.Write);
                builder.UseTexture(dilatedMotionDepthLuma, AccessFlags.Write);
                builder.AllowGlobalStateModification(true);

                builder.SetRenderFunc((ConvertPassData data, UnsafeGraphContext ctx) =>
                {
                    CommandBuffer cmd    = CommandBufferHelpers.GetNativeCommandBuffer(ctx.cmd);
                    ComputeShader cs     = data.convertCS;
                    int           kernel = data.convertKernel;

                    cmd.SetComputeTextureParam(cs, kernel, k_InputColor,             data.inputColor);
                    cmd.SetComputeTextureParam(cs, kernel, k_InputDepth,             data.inputDepth);
                    cmd.SetComputeTextureParam(cs, kernel, k_InputVelocity,
                        data.inputVelocity.IsValid() ? (RenderTargetIdentifier)data.inputVelocity : Texture2D.blackTexture);
                    cmd.SetComputeTextureParam(cs, kernel, k_YCoCgLuma,              data.yCoCgLuma);
                    cmd.SetComputeTextureParam(cs, kernel, k_DilatedMotionDepthLuma, data.dilatedMotionDepthLuma);

                    BindCommonUniforms(cmd, cs, kernel, data.renderSize, data.outputSize,
                        data.inputJitter, data.jitterSeqLength, data.deviceToViewDepth, data.reClipToPrevClip, data.validReset);

                    int groupsX = (data.renderSize.x + 15) / 16;
                    int groupsY = (data.renderSize.y +  7) /  8;
                    cmd.DispatchCompute(cs, kernel, groupsX, groupsY, 1);
                });
            }
            renderGraph.EndProfilingSampler(k_ConvertGpuScope);

            // ---- Activate pass ----

            renderGraph.BeginProfilingSampler(k_ActivateGpuScope);
            using (IUnsafeRenderGraphBuilder builder = renderGraph.AddUnsafePass<ActivatePassData>(
                k_ActivateSampler, out ActivatePassData activateData))
            {
                activateData.dilatedMotionDepthLuma = dilatedMotionDepthLuma;
                activateData.reactiveMask           = reactiveMask;
                activateData.newLocks               = newLocks;
                activateData.activateCS             = m_activateCS;
                activateData.activateKernel         = m_activateKernel;
                activateData.renderSize             = m_renderSize;
                activateData.outputSize             = m_outputSize;
                activateData.deviceToViewDepth      = m_deviceToViewDepth;
                activateData.inputJitter            = m_inputJitter;
                activateData.jitterSeqLength        = m_jitterSeqLength;
                activateData.validReset             = m_validReset;
                activateData.enablePixelLock        = m_enablePixelLock;

                builder.UseTexture(dilatedMotionDepthLuma);
                builder.UseTexture(reactiveMask, AccessFlags.Write);
                // newLocks is not declared: imported persistent resources trigger an unwanted
                // Clear (color) each frame when declared in AddUnsafePass builders.
                builder.AllowGlobalStateModification(true);

                builder.SetRenderFunc((ActivatePassData data, UnsafeGraphContext ctx) =>
                {
                    CommandBuffer cmd    = CommandBufferHelpers.GetNativeCommandBuffer(ctx.cmd);
                    ComputeShader cs     = data.activateCS;
                    int           kernel = data.activateKernel;

                    cmd.SetComputeTextureParam(cs, kernel, k_DilatedMotionDepthLuma, data.dilatedMotionDepthLuma);
                    cmd.SetComputeTextureParam(cs, kernel, k_ReactiveMask,           data.reactiveMask);
                    if (data.enablePixelLock)
                    {
                        cmd.SetComputeTextureParam(cs, kernel, k_NewLocks, data.newLocks);
                    }

                    BindCommonUniforms(cmd, cs, kernel, data.renderSize, data.outputSize,
                        data.inputJitter, data.jitterSeqLength, data.deviceToViewDepth, Matrix4x4.identity, data.validReset);

                    int groupsX = (data.renderSize.x + 15) / 16;
                    int groupsY = (data.renderSize.y +  7) /  8;
                    cmd.DispatchCompute(cs, kernel, groupsX, groupsY, 1);
                });
            }
            renderGraph.EndProfilingSampler(k_ActivateGpuScope);

            // ---- Upscale pass ----

            renderGraph.BeginProfilingSampler(k_UpscaleGpuScope);
            using (IUnsafeRenderGraphBuilder builder = renderGraph.AddUnsafePass<UpscalePassData>(
                k_UpscaleSampler, out UpscalePassData upscaleData))
            {
                upscaleData.yCoCgLuma              = yCoCgLuma;
                upscaleData.dilatedMotionDepthLuma = dilatedMotionDepthLuma;
                upscaleData.reactiveMask           = reactiveMask;
                upscaleData.prevHistoryOutput      = prevHistoryOutput;
                upscaleData.historyOutput          = historyOutput;
                upscaleData.sceneColorOutput       = sceneColorOutput;
                upscaleData.newLocks               = newLocks;
                upscaleData.upscaleCS              = m_upscaleCS;
                upscaleData.upscaleKernel          = m_upscaleKernel;
                upscaleData.renderSize             = m_renderSize;
                upscaleData.outputSize             = m_outputSize;
                upscaleData.inputJitter            = m_inputJitter;
                upscaleData.jitterSeqLength        = m_jitterSeqLength;
                upscaleData.validReset             = m_validReset;
                upscaleData.enablePixelLock        = m_enablePixelLock;
                upscaleData.sharpness              = m_sharpness;

                builder.UseTexture(yCoCgLuma);
                builder.UseTexture(dilatedMotionDepthLuma);
                builder.UseTexture(reactiveMask);
                // prevHistoryOutput, historyOutput, and newLocks are not declared: imported
                // persistent resources trigger an unwanted Clear (color) each frame when declared
                // in AddUnsafePass builders. Pass ordering is enforced via reactiveMask (written
                // by Activate, read here). AllowGlobalStateModification prevents pass culling.
                builder.UseTexture(sceneColorOutput,  AccessFlags.Write);
                builder.AllowGlobalStateModification(true);

                builder.SetRenderFunc((UpscalePassData data, UnsafeGraphContext ctx) =>
                {
                    CommandBuffer cmd    = CommandBufferHelpers.GetNativeCommandBuffer(ctx.cmd);
                    ComputeShader cs     = data.upscaleCS;
                    int           kernel = data.upscaleKernel;

                    cmd.SetComputeTextureParam(cs, kernel, k_YCoCgLuma,              data.yCoCgLuma);
                    cmd.SetComputeTextureParam(cs, kernel, k_DilatedMotionDepthLuma, data.dilatedMotionDepthLuma);
                    cmd.SetComputeTextureParam(cs, kernel, k_ReactiveMask,           data.reactiveMask);
                    cmd.SetComputeTextureParam(cs, kernel, k_PrevHistoryOutput,      data.prevHistoryOutput);
                    cmd.SetComputeTextureParam(cs, kernel, k_HistoryOutput,          data.historyOutput);
                    cmd.SetComputeTextureParam(cs, kernel, k_SceneColorOutput,       data.sceneColorOutput);
                    if (data.enablePixelLock)
                    {
                        cmd.SetComputeTextureParam(cs, kernel, k_NewLocks, data.newLocks);
                    }

                    BindCommonUniforms(cmd, cs, kernel, data.renderSize, data.outputSize,
                        data.inputJitter, data.jitterSeqLength, Vector4.zero, Matrix4x4.identity, data.validReset, data.sharpness);

                    int groupsX = (data.outputSize.x + 15) / 16;
                    int groupsY = (data.outputSize.y +  7) /  8;
                    cmd.DispatchCompute(cs, kernel, groupsX, groupsY, 1);
                });
            }
            renderGraph.EndProfilingSampler(k_UpscaleGpuScope);

            // Wire the upscaled output as the new camera color.
            // When sharpening is enabled, the Sharpen pass will overwrite this below.
            io.cameraColor = sceneColorOutput;

            // ---- Sharpen pass (optional) ----

            if (m_enableSharpening)
            {
                renderGraph.BeginProfilingSampler(k_SharpenGpuScope);
                using (IUnsafeRenderGraphBuilder builder = renderGraph.AddUnsafePass<SharpenPassData>(
                    k_SharpenSampler, out SharpenPassData sharpenData))
                {
                    sharpenData.input          = historyOutput;
                    sharpenData.upscaledOutput = upscaledOutput;
                    sharpenData.sharpenCS      = m_sharpenCS;
                    sharpenData.sharpenKernel  = m_sharpenKernel;
                    sharpenData.outputSize     = m_outputSize;
                    sharpenData.sharpness      = m_sharpness;

                    // historyOutput is not declared: imported persistent resources trigger an
                    // unwanted Clear (color) each frame when declared in AddUnsafePass builders.
                    builder.UseTexture(upscaledOutput, AccessFlags.Write);
                    builder.AllowGlobalStateModification(true);

                    builder.SetRenderFunc((SharpenPassData data, UnsafeGraphContext ctx) =>
                    {
                        CommandBuffer cmd    = CommandBufferHelpers.GetNativeCommandBuffer(ctx.cmd);
                        ComputeShader cs     = data.sharpenCS;
                        int           kernel = data.sharpenKernel;

                        cmd.SetComputeTextureParam(cs, kernel, k_Input,          data.input);
                        cmd.SetComputeTextureParam(cs, kernel, k_UpscaledOutput, data.upscaledOutput);
                        cmd.SetComputeFloatParam(cs,  k_Sharpness,               data.sharpness);
                        cmd.SetComputeVectorParam(cs, k_OutputInfo_ViewportSize,
                            new Vector4(data.outputSize.x, data.outputSize.y, 0, 0));
                        cmd.SetComputeVectorParam(cs, k_OutputInfo_ViewportSizeInverse,
                            new Vector4(1f / data.outputSize.x, 1f / data.outputSize.y, 0, 0));

                        int groupsX = (data.outputSize.x + 15) / 16;
                        int groupsY = (data.outputSize.y +  7) /  8;
                        cmd.DispatchCompute(cs, kernel, groupsX, groupsY, 1);
                    });
                }
                renderGraph.EndProfilingSampler(k_SharpenGpuScope);

                io.cameraColor = upscaledOutput;
            }

            // ---- Debug blit (Rendering Debugger > SGSR > CS Debug Output) ----
            // Blits a selected intermediate to cameraColor so it is visible in the Game view.
            // Off = no blit; the normal upscaled output is used.

            SGSRDebugSettings.SGSR2CSDebugOutput debugOutput =
                SGSRDebugSettings.instance?.csDebugOutput ?? SGSRDebugSettings.SGSR2CSDebugOutput.Off;

            if (debugOutput != SGSRDebugSettings.SGSR2CSDebugOutput.Off)
            {
                TextureHandle debugSource;
                switch (debugOutput)
                {
                    case SGSRDebugSettings.SGSR2CSDebugOutput.YCoCgLuma:
                        debugSource = yCoCgLuma;
                        break;
                    case SGSRDebugSettings.SGSR2CSDebugOutput.DilatedMotionDepthLuma:
                        debugSource = dilatedMotionDepthLuma;
                        break;
                    case SGSRDebugSettings.SGSR2CSDebugOutput.ReactiveMask:
                        debugSource = reactiveMask;
                        break;
                    case SGSRDebugSettings.SGSR2CSDebugOutput.NewLocks:
                        debugSource = newLocks;
                        break;
                    case SGSRDebugSettings.SGSR2CSDebugOutput.HistoryOutput:
                        debugSource = historyOutput;
                        break;
                    default:
                        debugSource = sceneColorOutput;
                        break;
                }

                using (IUnsafeRenderGraphBuilder builder = renderGraph.AddUnsafePass<DebugBlitPassData>(
                    k_DebugBlitSampler, out DebugBlitPassData blitData))
                {
                    blitData.source = debugSource;
                    blitData.dest   = io.cameraColor;

                    builder.UseTexture(debugSource);
                    builder.UseTexture(io.cameraColor, AccessFlags.Write);

                    builder.SetRenderFunc((DebugBlitPassData data, UnsafeGraphContext ctx) =>
                    {
                        CommandBuffer cmd = CommandBufferHelpers.GetNativeCommandBuffer(ctx.cmd);
                        cmd.SetRenderTarget(data.dest);
                        Blitter.BlitTexture(cmd, data.source, new Vector4(1, 1, 0, 0), 0, false);
                    });
                }
            }
        }

        // ---- Helpers ----

        private static void SetKeyword(ComputeShader cs, string keyword, bool enable)
        {
            if (enable)
            {
                cs.EnableKeyword(keyword);
            }
            else
            {
                cs.DisableKeyword(keyword);
            }
        }

        // ---- Uniform binding helper ----

        private static void BindCommonUniforms(
            CommandBuffer cmd,
            ComputeShader cs,
            int           kernel,
            Vector2Int    renderSize,
            Vector2Int    outputSize,
            Vector2       inputJitter,
            float         jitterSeqLength,
            Vector4       deviceToViewDepth,
            Matrix4x4     reClipToPrevClip,
            float         validReset,
            float         sharpness = 1.12f)
        {
            float rW = (float)renderSize.x;
            float rH = (float)renderSize.y;
            float oW = (float)outputSize.x;
            float oH = (float)outputSize.y;

            cmd.SetComputeVectorParam(cs, k_InputInfo_ViewportSize,          new Vector4(rW, rH, 0, 0));
            cmd.SetComputeVectorParam(cs, k_InputInfo_ViewportSizeInverse,   new Vector4(1f / rW, 1f / rH, 0, 0));
            cmd.SetComputeVectorParam(cs, k_HistoryInfo_ViewportSize,        new Vector4(oW, oH, 0, 0));
            cmd.SetComputeVectorParam(cs, k_HistoryInfo_ViewportSizeInverse, new Vector4(1f / oW, 1f / oH, 0, 0));
            cmd.SetComputeVectorParam(cs, k_DepthInfo_ViewportSize,          new Vector4(rW, rH, 0, 0));
            cmd.SetComputeVectorParam(cs, k_DepthInfo_ViewportSizeInverse,   new Vector4(1f / rW, 1f / rH, 0, 0));
            cmd.SetComputeVectorParam(cs, k_OutputInfo_ViewportSize,         new Vector4(oW, oH, 0, 0));
            cmd.SetComputeVectorParam(cs, k_OutputInfo_ViewportSizeInverse,  new Vector4(1f / oW, 1f / oH, 0, 0));
            cmd.SetComputeVectorParam(cs, k_InputJitter,                     new Vector4(inputJitter.x, inputJitter.y, 0, 0));
            cmd.SetComputeVectorParam(cs, k_DeviceToViewDepth,               deviceToViewDepth);
            cmd.SetComputeMatrixParam(cs, k_ReClipToPrevClip,                reClipToPrevClip);
            cmd.SetComputeFloatParam(cs,  k_ValidReset,                      validReset);
            cmd.SetComputeFloatParam(cs,  k_JitterSeqLength,                 jitterSeqLength);
            cmd.SetComputeFloatParam(cs,  k_Scalefactor,                     Mathf.Min(20f, Mathf.Pow((oW / rW) * (oH / rH), 3f)));
            cmd.SetComputeFloatParam(cs,  k_Sharpness,                       sharpness);
        }
    }
}
#endif // ENABLE_UPSCALER_FRAMEWORK
