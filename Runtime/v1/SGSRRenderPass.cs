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
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering.RenderGraphModule;

namespace Qualcomm.Snapdragon.SGSR
{
    /// <summary>
    /// Render Graph pass that executes the SGSR upscale shader.
    /// </summary>
    public class SGSRRenderPass : ScriptableRenderPass
    {
        private Material m_material;
        private SGSRQuality m_quality;
        private bool m_useHalfPrecision;
        private static readonly int k_SGSRConst1 = Shader.PropertyToID("sgsr_Const1");
        private static readonly int k_SGSRInputTexture = Shader.PropertyToID("_SGSRInputTexture");

        private static readonly ProfilingSampler k_UpscaleSampler = new ProfilingSampler("SGSR1");

        static SGSRRenderPass()
        {
            k_UpscaleSampler.enableRecording = true;
        }

        private class BlitPassData
        {
            public TextureHandle source;
            public TextureHandle destination;
            public Material      material;
        }

        public SGSRRenderPass(Material material)
        {
            m_material = material;
            renderPassEvent = RenderPassEvent.AfterRenderingPostProcessing;
        }

        /// <summary>
        /// Configures the upscale quality and precision for the next RecordRenderGraph call.
        /// </summary>
        public void Setup(SGSRQuality quality, bool useHalfPrecision)
        {
            m_quality = quality;
            m_useHalfPrecision = useHalfPrecision;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            SGSRGpuTimings.sgsr1Ms = k_UpscaleSampler.gpuElapsedTime;

            UpscalingIO io = frameData.Get<UpscalingIO>();

            TextureHandle source = io.cameraColor;

            // Create full-resolution destination texture
            TextureDesc destDesc = source.GetDescriptor(renderGraph);
            destDesc.width = io.postUpscaleResolution.x;
            destDesc.height = io.postUpscaleResolution.y;
            destDesc.msaaSamples = MSAASamples.None;
            destDesc.clearBuffer = false;
            TextureHandle destination = renderGraph.CreateTexture(destDesc);

            Vector4 con1 = new Vector4(
                1f / io.preUpscaleResolution.x, 1f / io.preUpscaleResolution.y,
                io.preUpscaleResolution.x, io.preUpscaleResolution.y);
            m_material.SetVector(k_SGSRConst1, con1);

            // Set quality keyword — clear all before enabling one
            m_material.DisableKeyword("SGSR_QUALITY_MOBILE");
            m_material.DisableKeyword("SGSR_QUALITY_HIGH");
            m_material.DisableKeyword("SGSR_QUALITY_VR");
            switch (m_quality)
            {
                case SGSRQuality.Mobile:
                    m_material.EnableKeyword("SGSR_QUALITY_MOBILE");
                    break;
                case SGSRQuality.VR:
                    m_material.EnableKeyword("SGSR_QUALITY_VR");
                    break;
                default:
                    m_material.EnableKeyword("SGSR_QUALITY_HIGH");
                    break;
            }

            // Half precision toggle
            if (m_useHalfPrecision)
            {
                m_material.EnableKeyword("SGSR_USE_HALF_PRECISION");
            }
            else
            {
                m_material.DisableKeyword("SGSR_USE_HALF_PRECISION");
            }

            // Execute blit: source (low-res) -> destination (full-res) via SGSR shader.
            renderGraph.BeginProfilingSampler(k_UpscaleSampler);
            using (IUnsafeRenderGraphBuilder builder = renderGraph.AddUnsafePass<BlitPassData>(
                "SGSR Upscale", out BlitPassData passData))
            {
                passData.source      = source;
                passData.destination = destination;
                passData.material    = m_material;
                builder.UseTexture(source);
                builder.UseTexture(destination, AccessFlags.Write);
                builder.AllowGlobalStateModification(true);
                builder.SetRenderFunc((BlitPassData data, UnsafeGraphContext ctx) =>
                {
                    CommandBuffer cmd = CommandBufferHelpers.GetNativeCommandBuffer(ctx.cmd);
                    cmd.SetGlobalTexture(k_SGSRInputTexture, data.source);
                    cmd.SetRenderTarget(data.destination);
                    Blitter.BlitTexture(cmd, data.source, new Vector4(1f, 1f, 0f, 0f), data.material, 0);
                });
            }
            renderGraph.EndProfilingSampler(k_UpscaleSampler);

            // Write output back to io.cameraColor — UpscalerPostProcessPass reads this to get our result.
            io.cameraColor = destination;
        }
    }
}
#endif // ENABLE_UPSCALER_FRAMEWORK
