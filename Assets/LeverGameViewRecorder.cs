#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEditor.Media;
using UnityEngine;

// Recording owns the physics clock. Encoding speed never changes simulation time.
public sealed class LeverGameViewRecorder : MonoBehaviour
{
    const int FrameRate = 100;
    MediaEncoder encoder;
    RenderTexture screenTexture;
    RenderTexture videoTexture;
    Texture2D frameTexture;
    StreamWriter frameMap;
    bool recording;
    bool clockOwned;
    float originalTimeScale;
    bool originalRunInBackground;
    SimulationMode originalSimulationMode;
    readonly Dictionary<Rigidbody, RigidbodyInterpolation> interpolation = new Dictionary<Rigidbody, RigidbodyInterpolation>();
    LeverRotorTorqueTest lever;
    DetentPlungerDebug plunger;
    LeverRecordingViewport.Snapshot viewport;
    long lastTimestamp = -1;
    int lastMappedSample = -1;
    public int FrameCount { get; private set; }
    public string OutputPath { get; private set; }
    public bool IsRecording => recording;
    public static bool OwnsPhysicsClock { get; private set; }

    public void Begin()
    {
        viewport = LeverRecordingViewport.Load();
        lever = FindFirstObjectByType<LeverRotorTorqueTest>();
        plunger = FindFirstObjectByType<DetentPlungerDebug>();
        if (!lever || !plunger)
            throw new InvalidOperationException("Synchronized recording requires the lever and plunger.");
        if (Physics.simulationMode != SimulationMode.FixedUpdate)
            throw new InvalidOperationException("Synchronized recording requires automatic fixed-step physics before starting.");
        originalTimeScale = Time.timeScale;
        originalSimulationMode = Physics.simulationMode;
        clockOwned = OwnsPhysicsClock = true;
        // Rendering must keep advancing in the Editor; physics is stepped only below.
        Time.timeScale = 1f;
        originalRunInBackground = Application.runInBackground;
        Application.runInBackground = true;
        Physics.simulationMode = SimulationMode.Script;
        foreach (var body in FindObjectsByType<Rigidbody>(FindObjectsSortMode.None))
        {
            interpolation.Add(body, body.interpolation);
            body.interpolation = RigidbodyInterpolation.None;
        }
        recording = true;
        StartCoroutine(CaptureFrames());
    }

    IEnumerator CaptureFrames()
    {
        var endOfFrame = new WaitForEndOfFrame();
        // All scene Start methods and the initial Game view must complete first.
        yield return endOfFrame;
        while (recording)
        {
            if (EditorApplication.isPaused || Screen.width < 2 || Screen.height < 2)
            {
                yield return endOfFrame;
                continue;
            }
            bool stopPlay = false;
            try
            {
                if (!lever.RecordingReady)
                    throw new InvalidOperationException("The lever CSV writer is not ready.");
                CaptureFrame();
                if (lever.RecordingReplayComplete)
                {
                    lever.FinishSynchronizedReplay();
                    stopPlay = true;
                    Finish();
                }
                else
                {
                    // Integer physics steps; the final frame may contain fewer steps.
                    int steps = Math.Max(1, (int)Math.Round(1.0 / (FrameRate * (double)Time.fixedDeltaTime)));
                    for (int i = 0; i < steps && !lever.RecordingReplayComplete; i++)
                    {
                        double stepTime = lever.NextRecordingStepTime;
                        lever.PrepareSynchronizedStep();
                        plunger.AdvancePhysicsStep(stepTime);
                        Physics.SyncTransforms();
                        Physics.Simulate(Time.fixedDeltaTime);
                        lever.CompleteSynchronizedStep();
                    }
                }
            }
            catch (Exception exception)
            {
                Debug.LogError("Synchronized Game view recording failed: " + exception);
                stopPlay = true;
                Finish();
            }
            if (stopPlay)
            {
                EditorApplication.isPlaying = false;
                yield break;
            }
            // The next render includes the completed physics state, UI and inset camera.
            yield return null;
            yield return endOfFrame;
        }
    }

    void CaptureFrame()
    {
        if (!screenTexture || screenTexture.width != Screen.width || screenTexture.height != Screen.height)
        {
            ReleaseTexture(ref screenTexture);
            screenTexture = new RenderTexture(Screen.width, Screen.height, 0,
                RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB) { hideFlags = HideFlags.HideAndDontSave };
            screenTexture.Create();
        }
        if (encoder == null)
        {
            int width = viewport.width, height = viewport.height;
            videoTexture = new RenderTexture(width, height, 0,
                RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB) { hideFlags = HideFlags.HideAndDontSave };
            videoTexture.Create();
            frameTexture = new Texture2D(width, height, TextureFormat.RGBA32, false)
                { hideFlags = HideFlags.HideAndDontSave };
            string directory = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Recordings"));
            Directory.CreateDirectory(directory);
            OutputPath = Path.Combine(directory, $"lever_game_synced_{DateTime.Now:yyyyMMdd_HHmmss_fffffff}.mp4");
            var attributes = new VideoTrackAttributes {
                frameRate = new MediaRational(FrameRate), width = (uint)width,
                height = (uint)height, includeAlpha = false
            };
            encoder = new MediaEncoder(OutputPath, attributes);
            frameMap = new StreamWriter(Path.ChangeExtension(OutputPath, ".frames.csv"));
            frameMap.WriteLine("frame,video_time_s,csv_state_time_s,first_completed_sample,last_completed_sample,post_step_angle_rad,post_step_plunger_compression_m");
            File.WriteAllText(Path.ChangeExtension(OutputPath, ".sync.txt"),
                "CSV=" + lever.RecordingFilePath + Environment.NewLine +
                "Video time 0 corresponds to CSV time " + lever.RecordingTimeOrigin.ToString("R", CultureInfo.InvariantCulture) + " s." + Environment.NewLine +
                "Each frame shows the post-physics state at csv_state_time_s. Initial frame has sample -1." + Environment.NewLine +
                "Reaction torque is a step average over [time_s, physics_step_end_time_s]; compare response peaks using physics_step_end_time_s." + Environment.NewLine +
                "Physics dt=" + Time.fixedDeltaTime.ToString("R", CultureInfo.InvariantCulture) + " s; target video 100 fps. No wall-clock rescaling or rigidbody interpolation." + Environment.NewLine +
                "Sub-frame peaks remain in the full-rate CSV; video sampling is about 10 ms. The frames CSV maps every completed sample interval.");
            Debug.Log("Synchronized Game view recording started: " + OutputPath);
        }
        ScreenCapture.CaptureScreenshotIntoRenderTexture(screenTexture);
        RenderTexture previous = RenderTexture.active;
        bool previousSrgbWrite = GL.sRGBWrite;
        try
        {
            GL.sRGBWrite = QualitySettings.activeColorSpace == ColorSpace.Linear;
            LeverRecordingViewport.Blit(screenTexture, videoTexture, viewport);
            RenderTexture.active = videoTexture;
            frameTexture.ReadPixels(new Rect(0, 0, videoTexture.width, videoTexture.height), 0, 0, false);
            frameTexture.Apply(false, false);
        }
        finally
        {
            RenderTexture.active = previous;
            GL.sRGBWrite = previousSrgbWrite;
        }
        double stateTime = lever.CompletedRecordingTime;
        long timestamp = (long)Math.Round((stateTime - lever.RecordingTimeOrigin) * 1000000.0);
        if (timestamp <= lastTimestamp)
            throw new InvalidOperationException("Simulation video timestamps must strictly increase.");
        if (!encoder.AddFrame(frameTexture, new MediaTime(timestamp, 1000000, 1)))
            throw new IOException("The video encoder rejected a synchronized frame.");
        int lastSample = lever.CompletedRecordingSamples - 1;
        frameMap.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0},{1:R},{2:R},{3},{4},{5:R},{6:R}",
            FrameCount, timestamp / 1000000.0, stateTime, lastMappedSample + 1, lastSample,
            lever.RecordingAngleRadians, lever.RecordingPlungerCompression));
        frameMap.Flush();
        lastMappedSample = lastSample;
        lastTimestamp = timestamp;
        FrameCount++;
    }

    public void SavePreview(string path)
    {
        if (frameTexture) File.WriteAllBytes(path, frameTexture.EncodeToPNG());
    }

    public void Finish()
    {
        if (!recording && encoder == null && !clockOwned) return;
        recording = false;
        StopAllCoroutines();
        try { encoder?.Dispose(); }
        finally
        {
            encoder = null;
            frameMap?.Dispose();
            frameMap = null;
            ReleaseTexture(ref screenTexture);
            ReleaseTexture(ref videoTexture);
            if (frameTexture) DestroyImmediate(frameTexture);
            frameTexture = null;
            if (clockOwned)
            {
                Physics.simulationMode = originalSimulationMode;
                Time.timeScale = originalTimeScale;
                Application.runInBackground = originalRunInBackground;
                foreach (var pair in interpolation)
                    if (pair.Key) pair.Key.interpolation = pair.Value;
                interpolation.Clear();
                clockOwned = OwnsPhysicsClock = false;
            }
        }
        if (FrameCount > 0)
            Debug.Log($"Synchronized Game view saved ({FrameCount} frames): {OutputPath}");
        else
            Debug.LogWarning("No video frames were captured. Keep the Game tab visible while recording.");
    }

    static void ReleaseTexture(ref RenderTexture texture)
    {
        if (!texture) return;
        texture.Release();
        DestroyImmediate(texture);
        texture = null;
    }

    void OnDisable() => Finish();
    void OnDestroy() => Finish();
}

[InitializeOnLoad]
public static class LeverGameViewRecordingAutoStart
{
    const string MenuPath = "Tools/Lever/Record Game View on Play";
    static string PreferenceKey => "LeverGameViewRecording." + Application.dataPath;
    public static LeverGameViewRecorder Current { get; private set; }
    public static bool Enabled => EditorPrefs.GetBool(PreferenceKey, true);

    static LeverGameViewRecordingAutoStart()
    {
        EditorApplication.playModeStateChanged += OnPlayModeChanged;
        EditorApplication.update += PumpRecording;
        AssemblyReloadEvents.beforeAssemblyReload += Finish;
        EditorApplication.quitting += Finish;
    }

    [MenuItem(MenuPath)]
    static void Toggle() => EditorPrefs.SetBool(PreferenceKey, !Enabled);

    [MenuItem(MenuPath, true)]
    static bool ValidateToggle()
    {
        Menu.SetChecked(MenuPath, Enabled);
        return !EditorApplication.isPlayingOrWillChangePlaymode;
    }

    static void OnPlayModeChanged(PlayModeStateChange state)
    {
        if (state == PlayModeStateChange.ExitingEditMode && Enabled)
        {
            try { LeverRecordingViewport.Store(); }
            catch (Exception exception)
            {
                SessionState.EraseString(LeverRecordingViewport.SessionKey);
                Debug.LogError("Cannot determine the visible Game view recording area: " + exception.Message);
            }
        }
        if (state == PlayModeStateChange.ExitingPlayMode)
            Finish();
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void StartBeforeFirstPhysicsStep()
    {
        if (!Enabled || Current) return;
        var host = new GameObject("Game View Video Recorder") { hideFlags = HideFlags.HideAndDontSave };
        UnityEngine.Object.DontDestroyOnLoad(host);
        Current = host.AddComponent<LeverGameViewRecorder>();
        try { Current.Begin(); }
        catch (Exception exception)
        {
            Debug.LogError("Game view recording could not start: " + exception);
            UnityEngine.Object.Destroy(host);
            Current = null;
        }
    }

    static void PumpRecording()
    {
        if (!EditorApplication.isPlaying || EditorApplication.isPaused || !Current || !Current.IsRecording) return;
        EditorApplication.QueuePlayerLoopUpdate();
        var type = typeof(UnityEditor.Editor).Assembly.GetType("UnityEditor.GameView");
        foreach (var window in Resources.FindObjectsOfTypeAll(type))
            ((EditorWindow)window).Repaint();
    }

    static void Finish()
    {
        if (Current) Current.Finish();
        Current = null;
    }
}

// Internal GameView access is isolated here because Unity has no public viewport API.
public static class LeverRecordingViewport
{
    public const string SessionKey = "LeverRecordingViewport.Snapshot";
    const System.Reflection.BindingFlags Flags = System.Reflection.BindingFlags.Instance |
        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;

    [Serializable]
    public struct Snapshot
    {
        // Normalized image coordinates with a top-left origin.
        public Rect crop;
        public int width;
        public int height;
    }

    static object Read(object instance, string name)
    {
        for (var type = instance.GetType(); type != null; type = type.BaseType)
        {
            var property = type.GetProperty(name, Flags);
            if (property != null) return property.GetValue(instance);
            var field = type.GetField(name, Flags);
            if (field != null) return field.GetValue(instance);
        }
        throw new MissingMemberException(instance.GetType().Name, name);
    }

    static EditorWindow FindGameView()
    {
        var type = typeof(UnityEditor.Editor).Assembly.GetType("UnityEditor.GameView", true);
        var windows = Resources.FindObjectsOfTypeAll(type);
        foreach (var window in windows)
            if (window == EditorWindow.focusedWindow) return (EditorWindow)window;
        if (windows.Length == 1) return (EditorWindow)windows[0];
        if (windows.Length == 0) throw new InvalidOperationException("Open the Game tab before recording.");
        throw new InvalidOperationException("Multiple Game tabs are open. Focus the one to record before pressing Play.");
    }

    public static Snapshot Measure()
    {
        var window = FindGameView();
        var target = (Rect)Read(window, "targetInView");
        var zoom = Read(window, "m_ZoomArea");
        var draw = (Rect)Read(zoom, "drawRect");
        float dpi = Convert.ToSingle(Read(window, "backingScale"));
        if (target.width <= 0 || target.height <= 0 || draw.width <= 0 || draw.height <= 0)
            throw new InvalidOperationException("Game view layout is not ready. Display the Game tab first.");
        // targetInView is relative to the drawing region, not the window toolbar.
        float x0 = Mathf.Max(0, Mathf.Round(target.x));
        float y0 = Mathf.Max(0, Mathf.Round(target.y));
        float x1 = Mathf.Min(draw.width, Mathf.Round(target.x) + target.width);
        float y1 = Mathf.Min(draw.height, Mathf.Round(target.y) + target.height);
        if (x1 <= x0 || y1 <= y0)
            throw new InvalidOperationException("No game image is visible in the Game tab.");
        return new Snapshot
        {
            crop = new Rect((x0 - Mathf.Round(target.x)) / target.width,
                (y0 - Mathf.Round(target.y)) / target.height,
                (x1 - x0) / target.width, (y1 - y0) / target.height),
            width = Mathf.Max(2, Mathf.FloorToInt((x1 - x0) * dpi / 2f) * 2),
            height = Mathf.Max(2, Mathf.FloorToInt((y1 - y0) * dpi / 2f) * 2)
        };
    }

    public static void Store()
    {
        var snapshot = Measure();
        SessionState.SetString(SessionKey, JsonUtility.ToJson(snapshot));
        Debug.Log($"Game view recording area locked: {snapshot.width}x{snapshot.height}, crop={snapshot.crop}");
    }

    public static Snapshot Load()
    {
        var json = SessionState.GetString(SessionKey, "");
        if (string.IsNullOrEmpty(json))
            throw new InvalidOperationException("No recording viewport was captured. Show the Game tab and restart Play mode.");
        var snapshot = JsonUtility.FromJson<Snapshot>(json);
        if (snapshot.width < 2 || snapshot.height < 2 || snapshot.crop.width <= 0 || snapshot.crop.height <= 0)
            throw new InvalidOperationException("Invalid recording viewport.");
        return snapshot;
    }

    public static void Blit(RenderTexture source, RenderTexture destination, Snapshot snapshot)
    {
        var crop = snapshot.crop;
        // Crop and flip together, retaining the same orientation as the Game tab.
        var scale = new Vector2(crop.width, SystemInfo.graphicsUVStartsAtTop ? -crop.height : crop.height);
        var offset = new Vector2(crop.x, SystemInfo.graphicsUVStartsAtTop ? crop.yMax : 1f - crop.yMax);
        Graphics.Blit(source, destination, scale, offset);
    }

    [MenuItem("Tools/Lever/Save Recording Viewport Preview")]
    public static void SaveViewportPreview()
    {
        var snapshot = Measure();
        var source = (RenderTexture)Read(FindGameView(), "m_RenderTexture");
        if (!source || !source.IsCreated())
            throw new InvalidOperationException("Display the Game tab once before saving a preview.");
        var previous = RenderTexture.active;
        bool srgb = GL.sRGBWrite;
        var target = RenderTexture.GetTemporary(snapshot.width, snapshot.height, 0,
            RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
        var pixels = new Texture2D(snapshot.width, snapshot.height, TextureFormat.RGBA32, false);
        try
        {
            GL.sRGBWrite = QualitySettings.activeColorSpace == ColorSpace.Linear;
            Blit(source, target, snapshot);
            RenderTexture.active = target;
            pixels.ReadPixels(new Rect(0, 0, snapshot.width, snapshot.height), 0, 0);
            pixels.Apply();
            var directory = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Recordings"));
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "recording_viewport_preview.png");
            File.WriteAllBytes(path, pixels.EncodeToPNG());
            Debug.Log($"Recording viewport preview: {snapshot.width}x{snapshot.height}, crop={snapshot.crop}, {path}");
        }
        finally
        {
            RenderTexture.active = previous;
            GL.sRGBWrite = srgb;
            RenderTexture.ReleaseTemporary(target);
            UnityEngine.Object.DestroyImmediate(pixels);
        }
    }
}
#endif