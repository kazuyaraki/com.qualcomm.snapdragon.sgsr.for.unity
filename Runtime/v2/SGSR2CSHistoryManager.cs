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
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace Qualcomm.Snapdragon.SGSR
{
    /// <summary>
    /// Per-camera persistent buffer state for the SGSR v2 3-pass compute shader upscaler.
    ///
    /// Persistent buffers:
    ///   HistoryOutput — RGBA16F at display resolution. Ping-pong: written by Upscale, read next frame.
    ///                   Alpha channel stores TemporalReactiveFactor.
    ///   NewLocks      — R16F at display resolution. Pixel-lock lifetimes; persists across frames.
    ///                   Written by Activate and Upscale; read by Upscale.
    /// </summary>
    internal class SGSR2CSHistoryState
    {
        public RTHandle  prevHistoryOutput;   // read by Upscale this frame
        public RTHandle  historyOutput;       // written by Upscale this frame
        public RTHandle  newLocks;            // pixel-lock lifetimes (display res)
        public Vector2Int resolution;
        public bool      needsReset;
    }

    /// <summary>
    /// Manages per-camera persistent RTHandles for the SGSR v2 3-pass compute shader upscaler.
    /// Keyed by camera EntityId. Call Dispose() when the upscaler is destroyed.
    /// </summary>
    internal class SGSR2CSHistoryManager : IDisposable
    {
        private readonly Dictionary<EntityId, SGSR2CSHistoryState> m_states =
            new Dictionary<EntityId, SGSR2CSHistoryState>();

        private bool m_disposed;

        /// <summary>
        /// Returns the history state for the given camera, allocating or reallocating as needed.
        /// <paramref name="displayResolution"/> is the output (display) resolution.
        /// </summary>
        public SGSR2CSHistoryState GetOrCreate(Camera camera, Vector2Int displayResolution)
        {
            EntityId id = camera.GetEntityId();

            if (!m_states.TryGetValue(id, out SGSR2CSHistoryState state))
            {
                state = new SGSR2CSHistoryState();
                m_states[id] = state;
            }

            if (state.prevHistoryOutput == null || state.resolution != displayResolution)
            {
                ReleaseState(state);
                Allocate(state, displayResolution);
                state.needsReset = true;
            }

            return state;
        }

        /// <summary>
        /// Swaps prevHistoryOutput and historyOutput after the Upscale pass completes.
        /// </summary>
        public void Swap(Camera camera)
        {
            EntityId id = camera.GetEntityId();
            if (!m_states.TryGetValue(id, out SGSR2CSHistoryState state))
            {
                return;
            }

            RTHandle tmp             = state.prevHistoryOutput;
            state.prevHistoryOutput  = state.historyOutput;
            state.historyOutput      = tmp;
            state.needsReset         = false;
        }

        /// <summary>
        /// Marks the history for the given camera as needing a full reset on the next frame.
        /// </summary>
        public void MarkReset(Camera camera)
        {
            EntityId id = camera.GetEntityId();
            if (m_states.TryGetValue(id, out SGSR2CSHistoryState state))
            {
                state.needsReset = true;
            }
        }

        /// <summary>
        /// Releases all per-camera RTHandles. Call when the upscaler is destroyed.
        /// </summary>
        public void Dispose()
        {
            if (m_disposed)
            {
                return;
            }

            m_disposed = true;

            foreach (SGSR2CSHistoryState state in m_states.Values)
            {
                ReleaseState(state);
            }

            m_states.Clear();
        }

        // ---- private helpers ----

        private static void Allocate(SGSR2CSHistoryState state, Vector2Int resolution)
        {
            state.resolution = resolution;

            state.prevHistoryOutput = RTHandles.Alloc(
                resolution.x, resolution.y,
                colorFormat: GraphicsFormat.R16G16B16A16_SFloat,
                filterMode:  FilterMode.Bilinear,
                wrapMode:    TextureWrapMode.Clamp,
                enableRandomWrite: true,
                name: "SGSR2CS_PrevHistoryOutput");

            state.historyOutput = RTHandles.Alloc(
                resolution.x, resolution.y,
                colorFormat: GraphicsFormat.R16G16B16A16_SFloat,
                filterMode:  FilterMode.Bilinear,
                wrapMode:    TextureWrapMode.Clamp,
                enableRandomWrite: true,
                name: "SGSR2CS_HistoryOutput");

            state.newLocks = RTHandles.Alloc(
                resolution.x, resolution.y,
                colorFormat: SystemInfo.IsFormatSupported(GraphicsFormat.R16_SFloat, GraphicsFormatUsage.LoadStore)
                    ? GraphicsFormat.R16_SFloat
                    : GraphicsFormat.R32_SFloat,
                filterMode:  FilterMode.Point,
                wrapMode:    TextureWrapMode.Clamp,
                enableRandomWrite: true,
                name: "SGSR2CS_NewLocks");
        }

        private static void ReleaseState(SGSR2CSHistoryState state)
        {
            if (state.prevHistoryOutput != null)
            {
                RTHandles.Release(state.prevHistoryOutput);
                state.prevHistoryOutput = null;
            }

            if (state.historyOutput != null)
            {
                RTHandles.Release(state.historyOutput);
                state.historyOutput = null;
            }

            if (state.newLocks != null)
            {
                RTHandles.Release(state.newLocks);
                state.newLocks = null;
            }
        }
    }
}
#endif // ENABLE_UPSCALER_FRAMEWORK
