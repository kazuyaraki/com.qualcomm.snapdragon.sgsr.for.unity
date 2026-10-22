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
    /// SGSR v2 two-pass (fragment shader) render graph pass.
    /// </summary>
    internal class SGSR2RenderPass : ScriptableRenderPass
    {
        // Pass data for the Convert unsafe pass.
        private class ConvertPassData
        {
            public TextureHandle inputDepth;
            public TextureHandle inputVelocity;       // may be invalid when not using motion vectors
            public TextureHandle motionDepthClipBuffer;
            public Material      convertMaterial;
        }

        // Pass data for the Upscale unsafe pass.
        private class UpscalePassData
        {
            public TextureHandle inputColor;
            public TextureHandle motionDepthClipBuffer;
            public TextureHandle prevHistory;
            public TextureHandle historyOutput;
            public TextureHandle sceneColorOutput;
            public Material      upscaleMaterial;
        }

        // Shader property IDs
        private static readonly int k_InputDepth        = Shader.PropertyToID("_SGSRInputDepth");
        private static readonly int k_InputVelocity     = Shader.PropertyToID("_SGSRInputVelocity");
        private static readonly int k_PrevHistory       = Shader.PropertyToID("_SGSRPrevHistory");
        private static readonly int k_MotionDepthClip   = Shader.PropertyToID("_SGSRMotionDepthClipBuffer");
        private static readonly int k_InputColor        = Shader.PropertyToID("_SGSRInputColor");
        private static readonly int k_RenderSize        = Shader.PropertyToID("InputInfo_ViewportSize");
        private static readonly int k_OutputSize        = Shader.PropertyToID("OutputInfo_ViewportSize");
        private static readonly int k_RenderSizeRcp     = Shader.PropertyToID("InputInfo_ViewportSizeInverse");
        private static readonly int k_JitterOffset      = Shader.PropertyToID("InputJitter");
        private static readonly int k_Biasmax           = Shader.PropertyToID("Biasmax_viewportXScale");
        private static readonly int k_Scalefactor       = Shader.PropertyToID("Scalefactor");
        private static readonly int k_CameraFovAngleHor = Shader.PropertyToID("AngleVertical");
        private static readonly int k_MinLerpContrib    = Shader.PropertyToID("MinLerpContribution");
        private static readonly int k_Reset             = Shader.PropertyToID("ValidReset");
        private static readonly int k_BSameCamera       = Shader.PropertyToID("bSameCamera");
        private static readonly int k_ClipToPrevClip    = Shader.PropertyToID("ReClipToPrevClip");

        private const string k_ConvertSampler = "SGSR2 Convert";
        private const string k_UpscaleSampler = "SGSR2 Upscale";

        private static readonly ProfilingSampler k_ConvertGpuScope = new ProfilingSampler("SGSR2 Convert.GPU");
        private static readonly ProfilingSampler k_UpscaleGpuScope = new ProfilingSampler("SGSR2 Upscale.GPU");

        static SGSR2RenderPass()
        {
            k_ConvertGpuScope.enableRecording = true;
            k_UpscaleGpuScope.enableRecording = true;
        }

        private readonly Material m_convertMaterial;
        private readonly Material m_upscaleMaterial;

        // Cached per-frame setup data (written by Setup, read by RecordRenderGraph)
        private Vector2Int          m_renderSize;
        private Vector2Int          m_outputSize;
        private Matrix4x4           m_clipToPrevClip;
        private float               m_cameraFovAngleHor;
        private float               m_reset;
        private uint                m_bSameCamera;
        private Vector2             m_jitterOffset;
        private SGSR2HistoryManager m_historyManager;
        private Camera              m_camera;
        private SGSR2Options        m_options;

        /// <summary>
        /// Initializes the render pass with the Convert and Upscale materials.
        /// </summary>
        public SGSR2RenderPass(Material convertMaterial, Material upscaleMaterial)
        {
            m_convertMaterial = convertMaterial;
            m_upscaleMaterial = upscaleMaterial;
            renderPassEvent = RenderPassEvent.AfterRenderingPostProcessing;
        }

        /// <summary>
        /// Called each frame before RecordRenderGraph. Computes per-frame uniforms.
        /// </summary>
        public void Setup(
            ContextContainer frameData,
            SGSR2HistoryManager historyManager,
            Vector2 jitterOffset,
            SGSR2Options options)
        {
            m_options = options;

            UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
            Camera camera = cameraData.camera;
            m_camera = camera;
            m_historyManager = historyManager;

            UpscalingIO io = frameData.Get<UpscalingIO>();
            m_outputSize = io.postUpscaleResolution;

            // Use the authoritative render resolution from the framework rather than
            // recomputing from renderScale, which may differ due to URP's snap threshold.
            m_renderSize = io.preUpscaleResolution;

            // Reprojection matrix: prev_view_proj * inv(curr_view_proj)
            Matrix4x4 currVP = GL.GetGPUProjectionMatrix(camera.nonJitteredProjectionMatrix, true)
                               * camera.worldToCameraMatrix;
            SGSR2HistoryState state = historyManager.GetOrCreate(camera, m_outputSize);
            Matrix4x4 prevVP = state.prevViewProj.Equals(Matrix4x4.zero)
                ? currVP
                : state.prevViewProj;
            m_clipToPrevClip = prevVP * currVP.inverse;

            // Reset if: framework signals it (scene load, etc.), history was just allocated,
            // or camera cut detected via VP matrix delta.
            if (io.resetHistory)
            {
                state.needsReset = true;
            }
            else if (m_options.detectCameraCuts && !state.needsReset && !state.prevViewProj.Equals(Matrix4x4.zero)
                  && SGSRUpscalerUtils.IsCameraCut(currVP, prevVP))
            {
                state.needsReset = true;
            }

            // _BSameCamera: 1 only when the camera VP didn't change from last frame (fully static).
            // Compare prevVP (before overwrite) to currVP; use a near-zero squared-Frobenius threshold
            // to handle floating-point identity rather than exact equality.
            bool cameraStatic = !state.needsReset && !state.prevViewProj.Equals(Matrix4x4.zero);
            if (cameraStatic)
            {
                // Jitter shifts the rendered content sub-pixel each frame. The shader's
                // forceReset path fires on out-of-AABB history (which jitter causes every frame),
                // discarding all history and producing a visibly jittered output. Treat any
                // active jitter as camera motion so bSameCamera stays 0.
                if (jitterOffset.sqrMagnitude > 1e-8f)
                {
                    cameraStatic = false;
                }
                else
                {
                    float vpDeltaSq = 0f;
                    for (int r = 0; r < 4; r++)
                    {
                        Vector4 d = currVP.GetRow(r) - prevVP.GetRow(r);
                        vpDeltaSq += Vector4.Dot(d, d);
                    }
                    cameraStatic = vpDeltaSq < 1e-6f;
                }
            }

            state.prevViewProj = currVP;

            m_bSameCamera = cameraStatic ? 1u : 0u;
            m_reset = state.needsReset ? 1.0f : 0.0f;

            // FOV: tan(hFOV/2)
            float vFovRad = camera.fieldOfView * Mathf.Deg2Rad;
            float aspect = (float)m_renderSize.x / Mathf.Max(m_renderSize.y, 1);
            m_cameraFovAngleHor = Mathf.Tan(vFovRad * 0.5f) * aspect;

            m_jitterOffset = jitterOffset;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            SGSRGpuTimings.sgsr2ConvertMs = k_ConvertGpuScope.gpuElapsedTime;
            SGSRGpuTimings.sgsr2UpscaleMs = k_UpscaleGpuScope.gpuElapsedTime;
            SGSRGpuTimings.sgsr2Ms        = SGSRGpuTimings.sgsr2ConvertMs + SGSRGpuTimings.sgsr2UpscaleMs;

            UpscalingIO io = frameData.Get<UpscalingIO>();
            UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();

            TextureHandle inputDepth = resourceData.cameraDepth;

            // MotionDepthClipBuffer: RGBA16F at render resolution
            TextureDesc mdcDesc = new TextureDesc(m_renderSize.x, m_renderSize.y)
            {
                colorFormat = GraphicsFormat.R16G16B16A16_SFloat,
                filterMode  = FilterMode.Bilinear,
                wrapMode    = TextureWrapMode.Clamp,
                msaaSamples = MSAASamples.None,
                name        = "SGSR2_MotionDepthClipBuffer",
            };
            TextureHandle motionDepthClipBuffer = renderGraph.CreateTexture(mdcDesc);

            // Set per-frame uniforms on both materials
            SetMaterialUniforms(m_convertMaterial);
            SetMaterialUniforms(m_upscaleMaterial);

            // ---- Convert pass ----

            renderGraph.BeginProfilingSampler(k_ConvertGpuScope);
            using (IUnsafeRenderGraphBuilder builder = renderGraph.AddUnsafePass<ConvertPassData>(
                k_ConvertSampler, out ConvertPassData convertData))
            {
                convertData.inputDepth            = inputDepth;
                convertData.inputVelocity         = resourceData.motionVectorColor;
                convertData.motionDepthClipBuffer = motionDepthClipBuffer;
                convertData.convertMaterial       = m_convertMaterial;
                builder.UseTexture(inputDepth);
                if (resourceData.motionVectorColor.IsValid())
                {
                    builder.UseTexture(resourceData.motionVectorColor);
                }
                builder.UseTexture(motionDepthClipBuffer, AccessFlags.Write);
                builder.AllowGlobalStateModification(true);

                builder.SetRenderFunc((ConvertPassData data, UnsafeGraphContext ctx) =>
                {
                    CommandBuffer cmd = CommandBufferHelpers.GetNativeCommandBuffer(ctx.cmd);

                    cmd.SetGlobalTexture(k_InputVelocity,
                        data.inputVelocity.IsValid() ? (RenderTargetIdentifier)data.inputVelocity : Texture2D.blackTexture);
                    cmd.SetGlobalTexture(k_InputDepth, data.inputDepth);

                    cmd.SetRenderTarget(data.motionDepthClipBuffer);
                    Blitter.BlitTexture(cmd, data.inputDepth, new Vector4(1, 1, 0, 0), data.convertMaterial, 0);
                });
            }
            renderGraph.EndProfilingSampler(k_ConvertGpuScope);

            // ---- Upscale pass ----
            if (m_options.use5Tap)
            {
                m_upscaleMaterial.EnableKeyword("SGSR2_USE_5_TAP");
            }
            else
            {
                m_upscaleMaterial.DisableKeyword("SGSR2_USE_5_TAP");
            }
            // SGSR2_LANCZOS_OPT is a future optimization path; always disabled until exposed as an option.
            m_upscaleMaterial.DisableKeyword("SGSR2_LANCZOS_OPT");
            if (m_options.computeThinFeature)
            {
                m_upscaleMaterial.EnableKeyword("SGSR2_COMPUTE_THIN_FEATURE");
            }
            else
            {
                m_upscaleMaterial.DisableKeyword("SGSR2_COMPUTE_THIN_FEATURE");
            }

            // Import persistent history RTHandles into the render graph.
            // prevHistory: clearOnFirstUse is intentionally false. Unsafe passes do not guarantee
            // when the render graph injects resource clears, so a clearOnFirstUse on a read-only
            // texture can misfire and clear the wrong render target. The shader _Reset uniform
            // (= 1.0 on reset frames) forces alpha = 1.0, which discards history entirely and
            // uses only the current frame — so no explicit GPU clear is needed.
            // historyOutput: never discard — its contents must survive to the next frame as prevHistory.
            SGSR2HistoryState historyState = m_historyManager.GetOrCreate(m_camera, m_outputSize);
            var historyImportParams = new ImportResourceParams
            {
                clearOnFirstUse  = false,
                discardOnLastUse = false,
            };
            TextureHandle prevHistoryHandle   = renderGraph.ImportTexture(historyState.prevHistory,  historyImportParams);
            TextureHandle historyOutputHandle = renderGraph.ImportTexture(historyState.historyOutput, historyImportParams);

            // Output texture at display resolution (scene color).
            TextureDesc sourceDesc = io.cameraColor.GetDescriptor(renderGraph);
            TextureDesc outputDesc = sourceDesc;
            outputDesc.width  = m_outputSize.x;
            outputDesc.height = m_outputSize.y;
            outputDesc.msaaSamples = MSAASamples.None;
            outputDesc.clearBuffer = false;
            TextureHandle sceneColorOutput = renderGraph.CreateTexture(outputDesc);

            // Use AddUnsafePass so the SetRenderFunc closure can capture 'this' for Swap().
            // This matches the pattern used by DLSS and FSR2 reference implementations.
            renderGraph.BeginProfilingSampler(k_UpscaleGpuScope);
            using (IUnsafeRenderGraphBuilder builder = renderGraph.AddUnsafePass<UpscalePassData>(
                k_UpscaleSampler, out UpscalePassData passData))
            {
                passData.inputColor          = io.cameraColor;
                passData.motionDepthClipBuffer = motionDepthClipBuffer;
                passData.prevHistory         = prevHistoryHandle;
                passData.historyOutput       = historyOutputHandle;
                passData.sceneColorOutput    = sceneColorOutput;
                passData.upscaleMaterial     = m_upscaleMaterial;
                builder.UseTexture(io.cameraColor);
                builder.UseTexture(motionDepthClipBuffer);
                builder.UseTexture(prevHistoryHandle);
                builder.UseTexture(historyOutputHandle, AccessFlags.Write);
                builder.UseTexture(sceneColorOutput,    AccessFlags.Write);
                builder.AllowGlobalStateModification(true);

                builder.SetRenderFunc((UpscalePassData data, UnsafeGraphContext ctx) =>
                {
                    CommandBuffer cmd = CommandBufferHelpers.GetNativeCommandBuffer(ctx.cmd);

                    cmd.SetGlobalTexture(k_InputColor,      data.inputColor);
                    cmd.SetGlobalTexture(k_MotionDepthClip, data.motionDepthClipBuffer);
                    cmd.SetGlobalTexture(k_PrevHistory,     data.prevHistory);

                    // MRT: SV_Target0 = sceneColorOutput, SV_Target1 = historyOutput
                    RenderTargetIdentifier[] mrts =
                    {
                        data.sceneColorOutput,
                        data.historyOutput,
                    };
                    cmd.SetRenderTarget(mrts, data.sceneColorOutput);
                    Blitter.BlitTexture(cmd, data.inputColor, new Vector4(1, 1, 0, 0), data.upscaleMaterial, 0);

                    // SetRenderFunc executes at render graph execution time, not recording time.
                    // Swapping here ensures historyOutput is ready as prevHistory on the next frame,
                    // but only after the GPU write to historyOutput has been issued to the command buffer.
                    m_historyManager.Swap(m_camera);
                });
            }
            renderGraph.EndProfilingSampler(k_UpscaleGpuScope);

            io.cameraColor = sceneColorOutput;
        }

        // ---- private helpers ----

        private void SetMaterialUniforms(Material mat)
        {
            float rW = (float)m_renderSize.x;
            float rH = (float)m_renderSize.y;
            float oW = (float)m_outputSize.x;
            float oH = (float)m_outputSize.y;

            mat.SetVector(k_RenderSize,    new Vector4(rW, rH, 0, 0));
            mat.SetVector(k_OutputSize,    new Vector4(oW, oH, 0, 0));
            mat.SetVector(k_RenderSizeRcp, new Vector4(1f / rW, 1f / rH, 0, 0));
            mat.SetVector(k_JitterOffset,  new Vector4(m_jitterOffset.x, m_jitterOffset.y, 0, 0));
            mat.SetFloat(k_Biasmax, oW / rW);
            mat.SetFloat(k_Scalefactor, Mathf.Min(20f, Mathf.Pow((oW / rW) * (oH / rH), 3f)));
            mat.SetFloat(k_CameraFovAngleHor, m_cameraFovAngleHor);
            mat.SetFloat(k_MinLerpContrib, m_bSameCamera == 1u ? m_options.minLerpContribution : 0f);
            mat.SetFloat(k_Reset,          m_reset);
            mat.SetInteger(k_BSameCamera,  (int)m_bSameCamera);

            mat.SetMatrix(k_ClipToPrevClip, m_clipToPrevClip);
        }
    }
}
#endif // ENABLE_UPSCALER_FRAMEWORK