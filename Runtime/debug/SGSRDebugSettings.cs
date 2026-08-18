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

namespace Qualcomm.Snapdragon.SGSR
{
    /// <summary>
    /// SGSR debug settings for the Unity Rendering Debugger.
    /// Access via Window > Analysis > Rendering Debugger > SGSR (in Editor).
    /// </summary>
    public class SGSRDebugSettings : IDebugData
    {
        const string k_PanelName = "SGSR";

        public enum SGSR2CSDebugOutput
        {
            /// <summary>Normal camera output — no debug overlay.</summary>
            Off,
            /// <summary>Blit the YCoCgLuma buffer (Convert pass output). RGB = YCoCg color, A = perceptual luma.</summary>
            YCoCgLuma,
            /// <summary>Blit the DilatedMotionDepthLuma buffer (Convert pass output). RG = dilated motion, B = nearest depth, A = perceptual luma.</summary>
            DilatedMotionDepthLuma,
            /// <summary>Blit the ReactiveMask buffer (Activate pass output). R = depth-clip disocclusion factor [0,1]. Bright = disoccluded edges.</summary>
            ReactiveMask,
            /// <summary>Blit the NewLocks buffer (Activate/Upscale pass). R = pixel-lock lifetime. Bright = locked thin features. Only meaningful when enablePixelLock is true.</summary>
            NewLocks,
            /// <summary>Blit the HistoryOutput buffer (Upscale pass output). RGB = accumulated color, A = TemporalReactiveFactor.</summary>
            HistoryOutput,
        }

        // Singleton — shared state readable from SGSRRendererFeature each frame
        private static SGSRDebugSettings s_Instance;
        public static SGSRDebugSettings instance => s_Instance;

        // ---- debug upscaler override ----

        /// <summary>
        /// Which SGSR algorithm the debug upscaler is currently running.
        /// Editable via the debug panel only when the "SGSR Debug Upscaler" is the active upscaler.
        /// </summary>
        public SGSRDebugUpscalerMode upscalerOverride
        {
            get;
            private set;
        }

        /// <summary>
        /// True when SGSRDebugIUpscaler is the active upscaler. Controls whether the
        /// "Active Upscaler" enum field in the debug panel is editable or shown as N/A.
        /// </summary>
        public bool isDebugUpscalerActive
        {
            get;
            private set;
        }

        private SGSRDebugUpscalerMode m_defaultUpscalerMode;

        /// <summary>
        /// Global enable/disable override. When false, the SGSR render pass is skipped
        /// and render scale is restored to 1.0 regardless of the Renderer Feature settings.
        /// </summary>
        public bool enabled
        {
            get;
            private set;
        } = true;

        // ---- v2-specific debug state ----

        /// <summary>
        /// When true, the next frame will clear the temporal history for all cameras.
        /// Automatically resets to false after one frame.
        /// </summary>
        public bool resetHistoryRequested
        {
            get;
            private set;
        }

        /// <summary>
        /// When non-Off, the 3-pass CS render pass blits the selected intermediate
        /// texture to the camera output so it is visible in the Game view.
        /// </summary>
        public SGSR2CSDebugOutput csDebugOutput
        {
            get;
            private set;
        } = SGSR2CSDebugOutput.Off;

        /// <summary>Current render scale (input / output width ratio).</summary>
        public float renderScale { get; private set; } = 1.0f;
        /// <summary>Current input (pre-upscale) resolution in pixels.</summary>
        public Vector2Int inputResolution { get; private set; }
        /// <summary>Current output (post-upscale / display) resolution in pixels.</summary>
        public Vector2Int outputResolution { get; private set; }

        internal void UpdateResolutionInfo(float scale, Vector2Int input, Vector2Int output)
        {
            renderScale = scale;
            inputResolution = input;
            outputResolution = output;
        }

        /// <summary>
        /// Returns an Action that resets all debug state to defaults.
        /// Called by the Rendering Debugger when the reset button is pressed.
        /// </summary>
        public Action GetReset() => () =>
        {
            enabled = true;
            resetHistoryRequested = false;
            csDebugOutput = SGSR2CSDebugOutput.Off;
            renderScale = 1.0f;
            inputResolution = default;
            outputResolution = default;
            upscalerOverride = m_defaultUpscalerMode;
        };

        /// <summary>
        /// Clears the one-shot reset flag. Called by SGSR2IUpscaler after it has acted on the request.
        /// </summary>
        internal void ConsumeResetHistoryRequest()
        {
            resetHistoryRequested = false;
        }

        /// <summary>
        /// Called by SGSRDebugIUpscaler on initialization. Seeds upscalerOverride from the
        /// options default and marks the debug upscaler as active so the debug panel shows
        /// the editable enum rather than the N/A label.
        /// </summary>
        internal static void RegisterAsDebugUpscaler(SGSRDebugUpscalerMode defaultMode)
        {
            if (s_Instance == null)
            {
                return;
            }

            s_Instance.isDebugUpscalerActive = true;
            s_Instance.m_defaultUpscalerMode = defaultMode;
            s_Instance.upscalerOverride = defaultMode;
        }

        /// <summary>
        /// Called by SGSRDebugIUpscaler on disposal. Clears the active flag so the debug
        /// panel reverts to the N/A read-only label.
        /// </summary>
        internal static void UnregisterAsDebugUpscaler()
        {
            if (s_Instance == null)
            {
                return;
            }

            s_Instance.isDebugUpscalerActive = false;
        }

        internal static void RegisterDebugPanel()
        {
            if (s_Instance != null)
            {
                return;
            }

            s_Instance = new SGSRDebugSettings();
            DebugManager.instance.RegisterData(s_Instance);

            var panel = DebugManager.instance.GetPanel(k_PanelName, createIfNull: true);
            panel.children.Add(new DebugUI.Widget[]
            {
                new DebugUI.Foldout
                {
                    displayName = "Snapdragon Game Super Resolution",
                    opened = true,
                    children =
                    {
                        new DebugUI.Value
                        {
                            displayName = "Render Scale",
                            getter = () => $"{s_Instance.renderScale:0.##}",
                            refreshRate = 0.1f
                        },
                        new DebugUI.Value
                        {
                            displayName = "Resolution",
                            getter = () => $"{s_Instance.inputResolution.x}x{s_Instance.inputResolution.y} \u2192 {s_Instance.outputResolution.x}x{s_Instance.outputResolution.y}",
                            refreshRate = 0.1f
                        },
                        new DebugUI.BoolField
                        {
                            displayName = "Enabled",
                            tooltip = "Enable or disable SGSR upscaling. When disabled, render scale is restored to 1.0.",
                            getter = () => s_Instance.enabled,
                            setter = value => s_Instance.enabled = value
                        },
                        new DebugUI.EnumField
                        {
                            displayName = "Active Upscaler",
                            tooltip = "Switch the SGSR algorithm at runtime. " +
                                      "Only available when 'SGSR Debug Upscaler' is selected in the URP pipeline asset.",
                            autoEnum = typeof(SGSRDebugUpscalerMode),
                            getter   = () => (int)s_Instance.upscalerOverride,
                            setter   = value => s_Instance.upscalerOverride = (SGSRDebugUpscalerMode)value,
                            getIndex = () => (int)s_Instance.upscalerOverride,
                            setIndex = value => s_Instance.upscalerOverride = (SGSRDebugUpscalerMode)value,
                            isHiddenCallback = () => !(s_Instance?.isDebugUpscalerActive ?? false)
                        },
                        new DebugUI.Value
                        {
                            displayName = "Active Upscaler",
                            getter = () => "N/A \u2014 select 'SGSR Debug Upscaler' in the URP pipeline asset",
                            isHiddenCallback = () => s_Instance?.isDebugUpscalerActive ?? true
                        }
                    }
                },
                new DebugUI.Foldout
                {
                    displayName = "SGSR v2 (Temporal)",
                    opened = true,
                    children =
                    {
                        new DebugUI.Button
                        {
                            displayName = "Reset History",
                            tooltip = "Clear the temporal history for all cameras on the next frame. Use this to remove ghosting artifacts after a scene change.",
                            action = () => s_Instance.resetHistoryRequested = true
                        },
                        new DebugUI.EnumField
                        {
                            displayName = "3-pass CS Debug Output",
                            tooltip = "Blit a 3-pass CS intermediate texture to the Game view. Off = normal output.",
                            autoEnum = typeof(SGSR2CSDebugOutput),
                            getter = () => (int)s_Instance.csDebugOutput,
                            setter = value => s_Instance.csDebugOutput = (SGSR2CSDebugOutput)value,
                            getIndex = () => (int)s_Instance.csDebugOutput,
                            setIndex = value => s_Instance.csDebugOutput = (SGSR2CSDebugOutput)value,
                        }
                    }
                }
            });
        }

        internal static void UnregisterDebugPanel()
        {
            if (s_Instance == null)
            {
                return;
            }

            DebugManager.instance.UnregisterData(s_Instance);
            DebugManager.instance.RemovePanel(k_PanelName);
            s_Instance = null;
        }
    }
}
#endif // ENABLE_UPSCALER_FRAMEWORK
