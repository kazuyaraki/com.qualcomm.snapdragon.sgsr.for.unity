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

#if UNITY_EDITOR
using UnityEditor;
#endif // UNITY_EDITOR

namespace Qualcomm.Snapdragon.SGSR
{
    /// <summary>
    /// Selects which SGSR algorithm the debug upscaler runs.
    /// Changed at runtime via Window > Analysis > Rendering Debugger > SGSR > Active Upscaler.
    /// </summary>
    public enum SGSRDebugUpscalerMode
    {
        /// <summary>SGSR 1.0 — single-pass spatial upscaler.</summary>
        Spatial,
        /// <summary>SGSR 2.0 — two-pass temporal upscaler (fragment shader).</summary>
        TwoPassFS,
        /// <summary>SGSR 2.0 — three-pass temporal upscaler (compute shader).</summary>
        ThreePassCS,
        /// <summary>No SGSR upscaling — bilinear blit to output resolution.</summary>
        Bilinear,
    }

    /// <summary>
    /// Serialized options for the SGSR Debug Upscaler. References the options objects for all three SGSR
    /// variants so the active algorithm can be switched at runtime via the Rendering Debugger.
    /// Select "SGSR Debug Upscaler" in the URP pipeline asset to opt in.
    /// </summary>
    public class SGSRDebugOptions : UpscalerOptions
    {
        /// <summary>Which upscaler algorithm is active when the scene starts.</summary>
        [Tooltip("Which upscaler algorithm is active when the scene starts. " +
                 "Can be changed at runtime via Window > Analysis > Rendering Debugger > SGSR > Active Upscaler.")]
        public SGSRDebugUpscalerMode defaultUpscaler = SGSRDebugUpscalerMode.ThreePassCS;

        /// <summary>Options for the SGSR 1.0 spatial upscaler variant.</summary>
        [Header("SGSR 1.0 Spatial")]
        public SGSROptions spatial;

        /// <summary>Options for the SGSR 2.0 two-pass fragment shader upscaler variant.</summary>
        [Header("SGSR 2.0 2-Pass Fragment Shader")]
        public SGSR2Options twoPass;

        /// <summary>Options for the SGSR 2.0 three-pass compute shader upscaler variant.</summary>
        [Header("SGSR 2.0 3-Pass Compute Shader")]
        public SGSR2CSOptions cs;

#if UNITY_EDITOR
        private void OnValidate()
        {
            string assetPath = AssetDatabase.GetAssetPath(this);
            if (string.IsNullOrEmpty(assetPath))
            {
                return;
            }

            bool dirty = false;

            if (RequiresSettingsAsset(spatial, assetPath))
            {
                spatial = CreateSettingsAsset(assetPath, spatial);
                dirty = true;
            }

            if (RequiresSettingsAsset(twoPass, assetPath))
            {
                twoPass = CreateSettingsAsset(assetPath, twoPass);
                dirty = true;
            }

            if (RequiresSettingsAsset(cs, assetPath))
            {
                cs = CreateSettingsAsset(assetPath, cs);
                dirty = true;
            }

            if (dirty)
            {
                EditorUtility.SetDirty(this);
            }
        }

        private static bool RequiresSettingsAsset(ScriptableObject settings, string parentAssetPath)
        {
            if (settings == null)
                return true;
#if UNITY_6000_5_OR_NEWER
            return AssetDatabase.GetAssetPath(settings) == parentAssetPath;
#else
            return false;
#endif
        }

        private static T CreateSettingsAsset<T>(string parentAssetPath, T existing) where T : ScriptableObject
        {
#if UNITY_6000_5_OR_NEWER
            // Unity 6.5+ deletes UpscalerOptions sub-assets that are not directly in the
            // pipeline's options list. Store debug variant settings separately so editing
            // the pipeline cannot delete them. Clone existing values when upgrading.
            var sub = existing != null ? Instantiate(existing) : CreateInstance<T>();
            sub.hideFlags = HideFlags.None;
            string path = System.IO.Path.ChangeExtension(parentAssetPath, null)
                + ".SGSRDebug." + typeof(T).Name + ".asset";
            AssetDatabase.CreateAsset(sub, AssetDatabase.GenerateUniqueAssetPath(path));
#else
            var sub = ScriptableObject.CreateInstance<T>();
            sub.hideFlags = HideFlags.HideInHierarchy;
            AssetDatabase.AddObjectToAsset(sub, parentAssetPath);
#endif
            return sub;
        }
#endif // UNITY_EDITOR
    }

#if UNITY_EDITOR
    [CustomEditor(typeof(SGSRDebugOptions))]
    class SGSRDebugOptionsEditor : Editor
    {
        private Editor m_spatialEditor;
        private Editor m_twoPassEditor;
        private Editor m_csEditor;

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            EditorGUILayout.PropertyField(serializedObject.FindProperty("defaultUpscaler"));
            serializedObject.ApplyModifiedProperties();

            EditorGUILayout.Space();

            var opts = (SGSRDebugOptions)target;
            DrawSubInspector("SGSR 1.0 Spatial", opts.spatial, ref m_spatialEditor);
            DrawSubInspector("SGSR 2.0 2-Pass Fragment Shader", opts.twoPass, ref m_twoPassEditor);
            DrawSubInspector("SGSR 2.0 3-Pass Compute Shader", opts.cs, ref m_csEditor);
        }

        private static void DrawSubInspector(string header, ScriptableObject obj, ref Editor editor)
        {
            EditorGUILayout.LabelField(header, EditorStyles.boldLabel);

            if (obj == null)
            {
                EditorGUILayout.HelpBox(
                    "Not initialized. Select the pipeline asset to trigger OnValidate.",
                    MessageType.Warning);
                EditorGUILayout.Space();
                return;
            }

            CreateCachedEditor(obj, null, ref editor);

            var so = editor.serializedObject;
            so.Update();

            SerializedProperty prop = so.GetIterator();
            bool enterChildren = true;
            while (prop.NextVisible(enterChildren))
            {
                enterChildren = false;
                if (prop.propertyPath == "m_Script")
                {
                    continue;
                }
                EditorGUILayout.PropertyField(prop, true);
            }

            so.ApplyModifiedProperties();
            EditorGUILayout.Space();
        }

        private void OnDisable()
        {
            DestroyImmediate(m_spatialEditor);
            DestroyImmediate(m_twoPassEditor);
            DestroyImmediate(m_csEditor);
            m_spatialEditor = null;
            m_twoPassEditor = null;
            m_csEditor = null;
        }
    }
#endif // UNITY_EDITOR
}
#endif // ENABLE_UPSCALER_FRAMEWORK
