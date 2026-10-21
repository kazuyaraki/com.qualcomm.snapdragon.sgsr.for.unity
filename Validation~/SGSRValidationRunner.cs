#if UNITY_EDITOR && ENABLE_UPSCALER_FRAMEWORK
using System;
using System.IO;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Qualcomm.Snapdragon.SGSR;

public class SGSRValidationRunner : MonoBehaviour
{
    [Serializable] public class Sample
    {
        public string variant, stage, activeType;
        public int width, height, inputWidth, outputWidth;
        public float mean, deviation;
        public int cameraContexts;
        public bool rendererInitialized, jitterMatches, passed;
    }
    [Serializable] public class Report
    {
        public string unity, gpu, api;
        public bool passed;
        public List<Sample> samples = new List<Sample>();
        public List<string> errors = new List<string>();
        public List<string> editorDiagnostics = new List<string>();
        public List<string> lifecycleChecks = new List<string>();
    }
    const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    public UniversalRenderPipelineAsset[] pipelines;
    public Camera cameraA, cameraB;
    public Transform movingObject;
    Report report = new Report();
    int variant = -1, frame;
    string resultDirectory;
    RenderTexture targetA, targetB;
    static readonly string[] Labels = { "spatial", "2pass", "3pass", "debug-spatial", "debug-2pass", "debug-3pass", "debug-bilinear" };

    public static void Run()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var runner = new GameObject("SGSR Validation").AddComponent<SGSRValidationRunner>();
        runner.pipelines = new UniversalRenderPipelineAsset[Labels.Length];
        var source = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>("Assets/Settings/PC_RPAsset.asset")
            ?? GraphicsSettings.defaultRenderPipeline as UniversalRenderPipelineAsset
            ?? QualitySettings.renderPipeline as UniversalRenderPipelineAsset;
        if (source == null)
        {
            foreach (var guid in AssetDatabase.FindAssets("t:UniversalRenderPipelineAsset", new[] { "Assets" }))
            {
                source = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(AssetDatabase.GUIDToAssetPath(guid));
                if (source != null) break;
            }
        }
        if (source == null) throw new Exception("URP asset is missing");
        Directory.CreateDirectory("Assets/SGSRValidationGenerated");
        for (int i = 0; i < Labels.Length; ++i)
        {
            var asset = Instantiate(source);
            asset.name = Labels[i];
            asset.renderScale = 0.5f;
            asset.msaaSampleCount = 1;
            asset.supportsCameraDepthTexture = true;
            AssetDatabase.CreateAsset(asset, "Assets/SGSRValidationGenerated/" + Labels[i] + ".asset");
            string name = i == 0 ? SGSRIUpscaler.UPSCALER_NAME : i == 1 ? SGSR2IUpscaler.UPSCALER_NAME : i == 2 ? SGSR2CSIUpscaler.UPSCALER_NAME : SGSRDebugIUpscaler.UPSCALER_NAME;
            var serialized = new SerializedObject(asset);
            var optionsProperty = serialized.FindProperty("m_UpscalerOptions");
            optionsProperty.ClearArray();
            serialized.ApplyModifiedPropertiesWithoutUndo();
            serialized.Update();
#if UNITY_6000_7_OR_NEWER
            UpscalerOptions.ValidateSerializedUpscalerOptionReferencesWithinRPAsset(asset, optionsProperty, new[] { name });
#else
            UpscalerOptions.ValidateSerializedUpscalerOptionReferencesWithinRPAsset(asset, optionsProperty);
#endif
            serialized.ApplyModifiedPropertiesWithoutUndo();
            serialized.Update();
#if UNITY_6000_7_OR_NEWER
            var priority = serialized.FindProperty("m_UpscalerPriority");
            priority.arraySize = 1;
            priority.GetArrayElementAtIndex(0).FindPropertyRelative("upscalerId").stringValue = name;
            priority.GetArrayElementAtIndex(0).FindPropertyRelative("upscalerName").stringValue = name;
#else
            var selected = serialized.FindProperty("m_SelectedUpscalerName") ?? serialized.FindProperty("m_IUpscalerName");
            selected.stringValue = name;
#if !UNITY_6000_5_OR_NEWER
            var filter = serialized.FindProperty("m_UpscalingFilter");
            filter.intValue = (int)UpscalingFilterSelection.IUpscaler;
#endif
#endif
            var scaling = serialized.FindProperty("m_ScalingMode");
            if (scaling != null) scaling.enumValueIndex = Array.IndexOf(scaling.enumNames, "Upscaling");
            serialized.ApplyModifiedPropertiesWithoutUndo();
            foreach (var sub in AssetDatabase.LoadAllAssetsAtPath(AssetDatabase.GetAssetPath(asset)))
            {
                if (sub is SGSRDebugOptions debug)
                {
                    if (i >= 3) debug.defaultUpscaler = (SGSRDebugUpscalerMode)(i - 3);
                    typeof(SGSRDebugOptions).GetMethod("OnValidate", All).Invoke(debug, null);
                    if (debug.spatial == null || debug.twoPass == null || debug.cs == null)
                        throw new Exception("Debug options failed to initialize");
                    EditorUtility.SetDirty(debug);
                }
            }
            // Repeat the inspector's option cleanup to detect loss of nested debug settings.
            serialized.Update();
#if UNITY_6000_7_OR_NEWER
            UpscalerOptions.ValidateSerializedUpscalerOptionReferencesWithinRPAsset(asset, serialized.FindProperty("m_UpscalerOptions"), new[] { name });
#else
            UpscalerOptions.ValidateSerializedUpscalerOptionReferencesWithinRPAsset(asset, serialized.FindProperty("m_UpscalerOptions"));
#endif
            serialized.ApplyModifiedPropertiesWithoutUndo();
            foreach (var sub in AssetDatabase.LoadAllAssetsAtPath(AssetDatabase.GetAssetPath(asset)))
                if (sub is SGSRDebugOptions debug && (debug.spatial == null || debug.twoPass == null || debug.cs == null))
                    throw new Exception("Pipeline inspector cleanup deleted debug settings");
            EditorUtility.SetDirty(asset);
            runner.pipelines[i] = asset;
        }
        runner.cameraA = CreateCamera("Camera A", new Vector3(0, 1.2f, -6));
        runner.cameraB = CreateCamera("Camera B", new Vector3(1, 2, -7));
        runner.cameraB.depth = 1;
        var shader = Shader.Find("Universal Render Pipeline/Unlit");
        for (int x = -8; x <= 8; ++x)
        for (int y = -4; y <= 4; ++y)
        {
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.transform.position = new Vector3(x * 0.35f, y * 0.35f, 1);
            cube.transform.localScale = new Vector3(0.14f, 0.28f, 0.14f);
            var material = new Material(shader);
            material.color = (x + y) % 2 == 0 ? new Color(0.95f, 0.8f, 0.2f) : new Color(0.1f, 0.45f, 0.95f);
            cube.GetComponent<Renderer>().sharedMaterial = material;
        }
        var moving = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        moving.transform.position = new Vector3(0, 0, -1);
        moving.GetComponent<Renderer>().sharedMaterial = new Material(shader) { color = Color.red };
        runner.movingObject = moving.transform;
        AssetDatabase.SaveAssets();
        // Disable domain reload so transient scene materials survive entering play mode.
        EditorSettings.enterPlayModeOptionsEnabled = true;
        EditorSettings.enterPlayModeOptions = EnterPlayModeOptions.DisableDomainReload | EnterPlayModeOptions.DisableSceneReload;
        EditorApplication.isPlaying = true;
    }

    public static void CheckUpgrade()
    {
#if UNITY_6000_5_OR_NEWER
        var asset = ScriptableObject.CreateInstance<UniversalRenderPipelineAsset>();
        string path = AssetDatabase.GenerateUniqueAssetPath("Assets/SGSRValidationGenerated/upgrade.asset");
        AssetDatabase.CreateAsset(asset, path);
        var debug = ScriptableObject.CreateInstance<SGSRDebugOptions>();
        AssetDatabase.AddObjectToAsset(debug, asset);
        debug.spatial = ScriptableObject.CreateInstance<SGSROptions>();
        debug.twoPass = ScriptableObject.CreateInstance<SGSR2Options>();
        debug.cs = ScriptableObject.CreateInstance<SGSR2CSOptions>();
        AssetDatabase.AddObjectToAsset(debug.spatial, asset);
        AssetDatabase.AddObjectToAsset(debug.twoPass, asset);
        AssetDatabase.AddObjectToAsset(debug.cs, asset);
        debug.spatial.quality = SGSRQuality.VR;
        debug.spatial.useHalfPrecision = false;
        debug.twoPass.enableJitter = false;
        debug.twoPass.minLerpContribution = 0.17f;
        debug.cs.enablePixelLock = false;
        debug.cs.sharpness = 0.73f;
        typeof(SGSRDebugOptions).GetMethod("OnValidate", All).Invoke(debug, null);
        var serialized = new SerializedObject(asset);
        var options = serialized.FindProperty("m_UpscalerOptions");
        options.arraySize = 1;
        options.GetArrayElementAtIndex(0).objectReferenceValue = debug;
        serialized.ApplyModifiedPropertiesWithoutUndo();
        serialized.Update();
#if UNITY_6000_7_OR_NEWER
        UpscalerOptions.ValidateSerializedUpscalerOptionReferencesWithinRPAsset(asset, options, new[] { SGSRDebugIUpscaler.UPSCALER_NAME });
#else
        UpscalerOptions.ValidateSerializedUpscalerOptionReferencesWithinRPAsset(asset, options);
#endif
        serialized.ApplyModifiedPropertiesWithoutUndo();
        AssetDatabase.SaveAssets();
        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
        if (debug.spatial == null || debug.twoPass == null || debug.cs == null
            || debug.spatial.quality != SGSRQuality.VR || debug.spatial.useHalfPrecision
            || debug.twoPass.enableJitter || debug.twoPass.minLerpContribution != 0.17f
            || debug.cs.enablePixelLock || debug.cs.sharpness != 0.73f
            || AssetDatabase.GetAssetPath(debug.spatial) == path
            || AssetDatabase.GetAssetPath(debug.twoPass) == path
            || AssetDatabase.GetAssetPath(debug.cs) == path)
            throw new Exception("Upgrading legacy debug settings lost values or references");
        var directory = Environment.GetEnvironmentVariable("SGSR_RESULTS");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "migration-report.json"), "{\"unity\":\"" + Application.unityVersion + "\",\"passed\":true}\n");
        Debug.Log("SGSR_MIGRATION_PASSED");
#endif
    }

    static Camera CreateCamera(string name, Vector3 position)
    {
        var camera = new GameObject(name).AddComponent<Camera>();
        camera.transform.position = position;
        camera.transform.LookAt(Vector3.zero);
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = new Color(0.04f, 0.06f, 0.09f);
        var data = camera.GetUniversalAdditionalCameraData();
        data.renderPostProcessing = true;
        data.antialiasing = AntialiasingMode.TemporalAntiAliasing;
        camera.allowMSAA = false;
        return camera;
    }

    void Start()
    {
        if (!Application.isPlaying) return;
        resultDirectory = Environment.GetEnvironmentVariable("SGSR_RESULTS") ?? Path.GetFullPath("../validation-results/" + Application.unityVersion);
        Directory.CreateDirectory(resultDirectory);
        report.unity = Application.unityVersion;
        report.gpu = SystemInfo.graphicsDeviceName;
        report.api = SystemInfo.graphicsDeviceType.ToString();
        Application.logMessageReceived += OnLog;
        RenderPipelineManager.endFrameRendering += EndFrame;
        NextVariant();
    }
    void OnLog(string message, string stack, LogType type)
    {
        if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert)
        {
            // A Unity 6.3 batch-mode Search indexing exception is unrelated to the rendering under test.
            if (stack.Contains("UnityEditor.Search.SearchDatabase"))
                report.editorDiagnostics.Add(message + "\n" + stack);
            else if (report.errors.Count < 50) report.errors.Add(message + "\n" + stack);
        }
    }
    void Resize(int width, int height)
    {
        cameraA.targetTexture = null;
        if (targetA != null) { targetA.Release(); Destroy(targetA); }
        targetA = new RenderTexture(width, height, 24, RenderTextureFormat.ARGBHalf);
        targetA.Create();
        cameraA.targetTexture = targetA;
    }
    void NextVariant()
    {
        if (++variant >= Labels.Length) { Finish(); return; }
        frame = 0;
        Resize(640, 360);
        if (targetB == null)
        {
            targetB = new RenderTexture(384, 256, 24, RenderTextureFormat.ARGBHalf);
            targetB.Create();
            cameraB.targetTexture = targetB;
        }
        cameraB.enabled = true;
        QualitySettings.renderPipeline = pipelines[variant];
        GraphicsSettings.defaultRenderPipeline = pipelines[variant];
        cameraA.transform.rotation = Quaternion.LookRotation(-cameraA.transform.position);
        Debug.Log("SGSR_VALIDATION_START " + Labels[variant]);
    }
    void Update()
    {
        if (!Application.isPlaying || variant < 0 || variant >= Labels.Length) return;
        movingObject.position = new Vector3(Mathf.Sin(frame * 0.1f), 0, -1);
        if (frame == 16) Resize(800, 450);
        if (frame == 28) cameraA.transform.Rotate(0, 35, 0);
        if (frame == 32) cameraA.transform.rotation = Quaternion.LookRotation(-cameraA.transform.position);
        if (frame == 40) cameraB.enabled = false;
        if (frame == 44)
        {
            cameraB.enabled = true;
            SetJitter(false);
        }
        if (frame == 52) SetJitter(true);
    }
    void SetJitter(bool enabled)
    {
        foreach (var sub in AssetDatabase.LoadAllAssetsAtPath(AssetDatabase.GetAssetPath(pipelines[variant])))
        {
            if (sub is SGSR2Options fs) fs.enableJitter = enabled;
            if (sub is SGSR2CSOptions cs) cs.enableJitter = enabled;
            if (sub is SGSRDebugOptions debug)
            {
                if (debug.twoPass != null) debug.twoPass.enableJitter = enabled;
                if (debug.cs != null) debug.cs.enableJitter = enabled;
            }
        }
    }
    void EndFrame(ScriptableRenderContext context, Camera[] cameras)
    {
        if (variant < 0 || variant >= Labels.Length) return;
        ++frame;
        try
        {
            if (frame == 12 || frame == 24 || frame == 36 || frame == 48 || frame == 60)
                Capture(frame == 12 ? "initial" : frame == 24 ? "resized" : frame == 36 ? "cut" : frame == 48 ? "jitter-off" : "jitter-on");
#if UNITY_6000_6_OR_NEWER
            if (variant == 2 && frame >= 64)
            {
                if (frame == 64) cameraB.enabled = false;
                if (frame == 480)
                {
                    AssertContextCount(1);
                    report.lifecycleChecks.Add("Unused camera context expired while the other camera kept rendering");
                    cameraB.enabled = true;
                }
                if (frame == 496)
                {
                    AssertContextCount(2);
                    report.lifecycleChecks.Add("Re-enabled camera acquired a new context");
                    NextVariant();
                }
                return;
            }
#endif
            if (frame >= 64) NextVariant();
        }
        catch (Exception ex) { report.errors.Add(ex.ToString()); Finish(); }
    }
    void Capture(string stage)
    {
        var sample = new Sample { variant = Labels[variant], stage = stage, width = targetA.width, height = targetA.height };
        var upscaling = typeof(UniversalRenderPipeline).GetField("upscaling", All)?.GetValue(null);
        if (upscaling == null) upscaling = typeof(UniversalRenderPipeline).GetProperty("upscaling", All)?.GetValue(null);
        var active = upscaling?.GetType().GetProperty("activeUpscaler", All)?.GetValue(upscaling)
            ?? upscaling?.GetType().GetMethod("GetActiveUpscaler", All)?.Invoke(upscaling, null);
        sample.activeType = active?.GetType().Name ?? "none";
        bool actualRendererInitialized = false;
#if UNITY_6000_6_OR_NEWER
        var manager = upscaling?.GetType().GetField("m_ContextManager", All)?.GetValue(upscaling);
        var contexts = manager?.GetType().GetField("m_Contexts", All)?.GetValue(manager) as IDictionary;
        if (contexts != null)
        {
            actualRendererInitialized = contexts.Count >= 2;
            foreach (var value in contexts.Values)
            {
                var renderer = value.GetType().GetProperty("upscaler", All)?.GetValue(value);
                if (renderer == null || !Initialized(renderer)) actualRendererInitialized = false;
                else ++sample.cameraContexts;
            }
        }
        ((IUpscaler)active).CalculateJitter(3, 2f, out var jitter, out var allowScaling);
#else
        actualRendererInitialized = active != null && Initialized(active);
        ((IUpscaler)active).CalculateJitter(3, out var jitter, out var allowScaling);
#endif
        bool expectedZero = variant == 0 || variant == 3 || variant == 6 || stage == "jitter-off";
        sample.jitterMatches = (jitter == Vector2.zero) == expectedZero && !allowScaling;
        var settingsType = typeof(SGSRIUpscaler).Assembly.GetType("Qualcomm.Snapdragon.SGSR.SGSRDebugSettings");
        var settings = settingsType.GetProperty("instance", All).GetValue(null);
        if (settings != null)
        {
            sample.inputWidth = ((Vector2Int)settingsType.GetProperty("inputResolution", All).GetValue(settings)).x;
            sample.outputWidth = ((Vector2Int)settingsType.GetProperty("outputResolution", All).GetValue(settings)).x;
            sample.rendererInitialized = actualRendererInitialized && sample.inputWidth > 0 && sample.inputWidth < sample.outputWidth;
        }
        RenderTexture.active = targetA;
        var texture = new Texture2D(targetA.width, targetA.height, TextureFormat.RGBA32, false);
        texture.ReadPixels(new Rect(0, 0, targetA.width, targetA.height), 0, 0);
        texture.Apply();
        RenderTexture.active = null;
        double sum = 0, squares = 0;
        var pixels = texture.GetPixels32();
        foreach (var pixel in pixels) { double v = (pixel.r + pixel.g + pixel.b) / 765.0; sum += v; squares += v * v; }
        sample.mean = (float)(sum / pixels.Length);
        sample.deviation = (float)Math.Sqrt(squares / pixels.Length - sample.mean * sample.mean);
        sample.passed = sample.rendererInitialized && sample.activeType.Contains("SGSR") && sample.jitterMatches && sample.deviation > 0.01f;
        File.WriteAllBytes(Path.Combine(resultDirectory, Labels[variant] + "-" + stage + ".png"), texture.EncodeToPNG());
        Destroy(texture);
        report.samples.Add(sample);
        Debug.Log("SGSR_VALIDATION_SAMPLE " + JsonUtility.ToJson(sample));
    }
#if UNITY_6000_6_OR_NEWER
    void AssertContextCount(int expected)
    {
        var upscaling = typeof(UniversalRenderPipeline).GetField("upscaling", All).GetValue(null);
        var manager = upscaling.GetType().GetField("m_ContextManager", All).GetValue(upscaling);
        var contexts = (IDictionary)manager.GetType().GetField("m_Contexts", All).GetValue(manager);
        if (contexts.Count != expected) throw new Exception("Expected " + expected + " camera contexts, got " + contexts.Count);
        var settingsType = typeof(SGSRIUpscaler).Assembly.GetType("Qualcomm.Snapdragon.SGSR.SGSRDebugSettings");
        if (settingsType.GetProperty("instance", All).GetValue(null) == null)
            throw new Exception("Cleaning one context removed the other camera's debug panel");
    }
#endif
    bool Initialized(object renderer)
    {
        if (renderer is SGSRDebugIUpscaler)
        {
            bool ready = (bool)renderer.GetType().GetField("m_initialized", All).GetValue(renderer);
            var mode = (SGSRDebugUpscalerMode)renderer.GetType().GetField("m_activeMode", All).GetValue(renderer);
            return ready && (int)mode == variant - 3;
        }
        return renderer.GetType().GetField("m_renderPass", All)?.GetValue(renderer) != null;
    }
    void Finish()
    {
        RenderPipelineManager.endFrameRendering -= EndFrame;
        Application.logMessageReceived -= OnLog;
        report.passed = report.errors.Count == 0 && report.samples.Count == Labels.Length * 5 && report.samples.TrueForAll(x => x.passed);
        File.WriteAllText(Path.Combine(resultDirectory, "report.json"), JsonUtility.ToJson(report, true));
        Debug.Log("SGSR_VALIDATION_DONE " + report.passed);
        EditorApplication.Exit(report.passed ? 0 : 2);
    }
}
#endif
