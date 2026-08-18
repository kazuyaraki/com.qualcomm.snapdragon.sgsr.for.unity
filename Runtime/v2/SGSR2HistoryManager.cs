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
    /// Per-camera state for SGSR v2 temporal history.
    /// </summary>
    internal class SGSR2HistoryState
    {
        public RTHandle prevHistory;
        public RTHandle historyOutput;
        public Matrix4x4 prevViewProj;
        public bool needsReset;
        public Vector2Int resolution;
    }

    /// <summary>
    /// Manages per-camera ping-pong history RTHandles for SGSR v2.
    /// Keyed by camera EntityId. Implements IDisposable; call Dispose() when the upscaler is destroyed.
    /// </summary>
    internal class SGSR2HistoryManager : IDisposable
    {
        private readonly Dictionary<EntityId, SGSR2HistoryState> m_states = new Dictionary<EntityId, SGSR2HistoryState>();
        private bool m_disposed;

        /// <summary>
        /// Returns the history state for the given camera, allocating or reallocating as needed.
        /// <paramref name="displayResolution"/> is the output (display) resolution.
        /// </summary>
        public SGSR2HistoryState GetOrCreate(Camera camera, Vector2Int displayResolution)
        {
            EntityId id = camera.GetEntityId();

            if (!m_states.TryGetValue(id, out SGSR2HistoryState state))
            {
                state = new SGSR2HistoryState();
                m_states[id] = state;
            }

            if (state.prevHistory == null || state.resolution != displayResolution)
            {
                ReleaseState(state);
                Allocate(state, displayResolution);
                state.needsReset = true;
            }

            return state;
        }

        /// <summary>
        /// Swaps prevHistory and historyOutput for the given camera after the Upscale pass completes.
        /// </summary>
        public void Swap(Camera camera)
        {
            EntityId id = camera.GetEntityId();
            if (!m_states.TryGetValue(id, out SGSR2HistoryState state))
            {
                return;
            }

            RTHandle tmp = state.prevHistory;
            state.prevHistory = state.historyOutput;
            state.historyOutput = tmp;
            state.needsReset = false;
        }

        /// <summary>
        /// Marks the history for the given camera as needing a full reset on the next frame.
        /// Call this on scene load, camera cut, or teleport.
        /// </summary>
        public void MarkReset(Camera camera)
        {
            EntityId id = camera.GetEntityId();
            if (m_states.TryGetValue(id, out SGSR2HistoryState state))
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

            foreach (SGSR2HistoryState state in m_states.Values)
            {
                ReleaseState(state);
            }

            m_states.Clear();
        }

        // ---- private helpers ----

        private static void Allocate(SGSR2HistoryState state, Vector2Int resolution)
        {
            state.resolution = resolution;
            state.prevHistory = RTHandles.Alloc(
                resolution.x, resolution.y,
                colorFormat: GraphicsFormat.R16G16B16A16_SFloat,
                filterMode: FilterMode.Bilinear,
                wrapMode: TextureWrapMode.Clamp,
                name: "SGSR2_PrevHistory");

            state.historyOutput = RTHandles.Alloc(
                resolution.x, resolution.y,
                colorFormat: GraphicsFormat.R16G16B16A16_SFloat,
                filterMode: FilterMode.Bilinear,
                wrapMode: TextureWrapMode.Clamp,
                name: "SGSR2_HistoryOutput");
        }

        private static void ReleaseState(SGSR2HistoryState state)
        {
            if (state.prevHistory != null)
            {
                RTHandles.Release(state.prevHistory);
                state.prevHistory = null;
            }

            if (state.historyOutput != null)
            {
                RTHandles.Release(state.historyOutput);
                state.historyOutput = null;
            }
        }
    }
}
#endif // ENABLE_UPSCALER_FRAMEWORK
