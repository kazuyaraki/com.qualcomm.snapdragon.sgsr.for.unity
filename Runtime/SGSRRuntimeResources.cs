//============================================================================================================
//
//
//                  Copyright (c) 2026 Qualcomm Innovation Center, Inc. All rights reserved.
//                              SPDX-License-Identifier: BSD-3-Clause
//
//============================================================================================================

using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Qualcomm.Snapdragon.SGSR
{
    /// <summary>
    /// Shader and compute shader assets for all SGSR upscaler variants.
    /// <seealso cref="IRenderPipelineResources"/>
    /// </summary>
    [Serializable]
    [SupportedOnRenderPipeline(typeof(UniversalRenderPipelineAsset))]
    [HideInInspector]
    public class SGSRRuntimeResources : IRenderPipelineResources
    {
        enum Version
        {
            Initial,
            Count,
            Last = Count - 1
        }
        
        [SerializeField, HideInInspector]
        Version m_version = Version.Last;

        int IRenderPipelineGraphicsSettings.version => (int)m_version;
        bool IRenderPipelineGraphicsSettings.isAvailableInPlayerBuild => true;

        // ---- SGSR 1.0 Spatial ----

        [SerializeField, ResourcePath("Shaders/v1/SGSRShader.shader")]
        private Shader m_spatialUpscaleShader;

        /// <summary>
        /// Shader asset for the SGSR 1.0 spatial upscale pass.
        /// </summary>
        public Shader spatialUpscaleShader
        {
            get => m_spatialUpscaleShader;
            set => this.SetValueAndNotify(ref m_spatialUpscaleShader, value);
        }

        // ---- SGSR 2.0 Two-Pass Fragment Shader ----

        [SerializeField, ResourcePath("Shaders/v2/SGSR2ConvertShader.shader")]
        private Shader m_convertShader;

        /// <summary>
        /// Shader asset for the SGSR 2.0 two-pass Convert pass.
        /// </summary>
        public Shader convertShader
        {
            get => m_convertShader;
            set => this.SetValueAndNotify(ref m_convertShader, value);
        }

        [SerializeField, ResourcePath("Shaders/v2/SGSR2UpscaleShader.shader")]
        private Shader m_upscaleShader;

        /// <summary>
        /// Shader asset for the SGSR 2.0 two-pass Upscale pass.
        /// </summary>
        public Shader upscaleShader
        {
            get => m_upscaleShader;
            set => this.SetValueAndNotify(ref m_upscaleShader, value);
        }

        // ---- SGSR 2.0 Three-Pass Compute Shader ----

        [SerializeField, ResourcePath("Shaders/v2/SGSR2CS_Convert.compute")]
        private ComputeShader m_convertCS;

        /// <summary>
        /// Compute shader asset for the SGSR 2.0 three-pass Convert pass.
        /// </summary>
        public ComputeShader convertCS
        {
            get => m_convertCS;
            set => this.SetValueAndNotify(ref m_convertCS, value);
        }

        [SerializeField, ResourcePath("Shaders/v2/SGSR2CS_Activate.compute")]
        private ComputeShader m_activateCS;

        /// <summary>
        /// Compute shader asset for the SGSR 2.0 three-pass Activate pass.
        /// </summary>
        public ComputeShader activateCS
        {
            get => m_activateCS;
            set => this.SetValueAndNotify(ref m_activateCS, value);
        }

        [SerializeField, ResourcePath("Shaders/v2/SGSR2CS_Upscale.compute")]
        private ComputeShader m_upscaleCS;

        /// <summary>
        /// Compute shader asset for the SGSR 2.0 three-pass Upscale pass.
        /// </summary>
        public ComputeShader upscaleCS
        {
            get => m_upscaleCS;
            set => this.SetValueAndNotify(ref m_upscaleCS, value);
        }

        [SerializeField, ResourcePath("Shaders/v2/SGSR2CS_Sharpen.compute")]
        private ComputeShader m_sharpenCS;

        /// <summary>
        /// Compute shader asset for the SGSR 2.0 three-pass optional Sharpen pass.
        /// </summary>
        public ComputeShader sharpenCS
        {
            get => m_sharpenCS;
            set => this.SetValueAndNotify(ref m_sharpenCS, value);
        }
    }
}
