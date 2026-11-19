#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Unity.Collections;
using UnityEditor;
using UnityEditor.Media;
using UnityEngine;
using UnityEngine.Rendering;
using Unity.Jobs.LowLevel.Unsafe;

// Physics follows elapsed real time. Video frames carry the exact completed CSV state time.
public sealed class LeverGameViewRecorder : MonoBehaviour
{
    const int FrameRate = 60;
    const int ReadbackSlots = 4;
    const double PhysicsBudgetSeconds = 0.100;
    sealed class PendingFrame
    {
        public NativeArray<byte> pixels;
        public AsyncGPUReadbackRequest request;
        public double stateTime, wallTime, angle, compression, lag;
        public int sample;
    }
    MediaEncoder encoder;
    RenderTexture screenTexture, videoTexture;
    Texture2D frameTexture;
    StreamWriter frameMap;
    readonly Queue<PendingFrame> pending = new Queue<PendingFrame>();
    readonly Queue<PendingFrame> available = new Queue<PendingFrame>();
    readonly List<PendingFrame> buffers = new List<PendingFrame>();
    bool recording, clockOwned, timelineStarted, firstSamplePrepared;
    float originalTimeScale, originalFixedDeltaTime;
    bool originalRunInBackground;
    SimulationMode originalSimulationMode;
    readonly Dictionary<Rigidbody, RigidbodyInterpolation> interpolation = new Dictionary<Rigidbody, RigidbodyInterpolation>();
    CollisionDetectionMode originalRotorCollisionMode;
    Rigidbody rotorBody;
    LeverRotorTorqueTest lever;
    DetentPlungerDebug plunger;
    LeverRecordingViewport.Snapshot viewport;
    long lastTimestamp = -1;
    int lastMappedSample = -1, skippedCaptures;
    double clockStart, pausedAt = -1, nextCaptureTime, lastWarningTime;
    double prepareTicks, plungerTicks, physicsTicks, completeTicks, encodeTicks;
    int profiledSteps;
    public string PerformanceSummary => string.Format(CultureInfo.InvariantCulture, "steps={0}; mean_us prepare={1:F2}, plunger={2:F2}, physics={3:F2}, csv_complete={4:F2}; encode_ms_per_frame={5:F2}", profiledSteps, Microseconds(prepareTicks), Microseconds(plungerTicks), Microseconds(physicsTicks), Microseconds(completeTicks), FrameCount > 0 ? encodeTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency / FrameCount : 0);
    double Microseconds(double ticks) => profiledSteps > 0 ? ticks * 1000000.0 / System.Diagnostics.Stopwatch.Frequency / profiledSteps : 0;
    public int FrameCount { get; private set; }
    public string OutputPath { get; private set; }
    public bool IsRecording => recording;
    public static bool OwnsPhysicsClock { get; private set; }
    public double WallSeconds => timelineStarted ? Math.Max(0, (pausedAt >= 0 ? pausedAt : Time.realtimeSinceStartupAsDouble) - clockStart) : 0;
    public double SimulationSeconds => lever ? Math.Max(0, lever.CompletedRecordingTime - lever.RecordingTimeOrigin) : 0;
    public double SimulationLagSeconds => Math.Max(0, WallSeconds - SimulationSeconds);

    public void Begin()
    {
        viewport = LeverRecordingViewport.Load();
        lever = FindFirstObjectByType<LeverRotorTorqueTest>();
        plunger = FindFirstObjectByType<DetentPlungerDebug>();
        if (!lever || !plunger)
            throw new InvalidOperationException("Synchronized recording requires the lever and plunger.");
        if (Physics.simulationMode != SimulationMode.FixedUpdate)
            throw new InvalidOperationException("Recording requires automatic fixed-step physics before starting.");
        originalFixedDeltaTime = Time.fixedDeltaTime;
        // Choose the sampling interval once, before Start opens the CSV; never rescale it mid-recording.
        Time.fixedDeltaTime = LeverGameViewRecordingAutoStart.HighPrecision ? 0.0005f : 0.002f;
        originalTimeScale = Time.timeScale;
        originalSimulationMode = Physics.simulationMode;
        originalRunInBackground = Application.runInBackground;
        clockOwned = OwnsPhysicsClock = true;
        Time.timeScale = 1f;
        Application.runInBackground = true;
        Physics.simulationMode = SimulationMode.Script;
        foreach (var body in FindObjectsByType<Rigidbody>(FindObjectsSortMode.None))
        {
            interpolation.Add(body, body.interpolation);
            body.interpolation = RigidbodyInterpolation.None;
        }
        rotorBody = lever.GetComponent<Rigidbody>();
        originalRotorCollisionMode = rotorBody.collisionDetectionMode;
        // Speculative CCD accounts for angular motion and avoids repeated linear sweeps of the cam.
        rotorBody.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
        EditorApplication.pauseStateChanged += OnPause;
        recording = true;
        StartCoroutine(CaptureFrames());
    }

    void OnPause(PauseState state)
    {
        if (!timelineStarted) return;
        if (state == PauseState.Paused) pausedAt = Time.realtimeSinceStartupAsDouble;
        else if (pausedAt >= 0)
        {
            clockStart += Time.realtimeSinceStartupAsDouble - pausedAt;
            pausedAt = -1;
        }
    }

    void Update()
    {
        if (!recording || !timelineStarted || EditorApplication.isPaused) return;
        try
        {
            DrainReadbacks(false);
            // Rendering/encoding no longer limits advancement to 10 ms per displayed frame.
            double target = lever.RecordingTimeOrigin + WallSeconds;
            double deadline = Time.realtimeSinceStartupAsDouble + PhysicsBudgetSeconds;
            int workers = JobsUtility.JobWorkerCount;
            try
            {
                // This two-body mechanism is faster without waking job workers 2000 times/s.
                // Restore the worker pool before rendering and GPU readback processing.
                JobsUtility.JobWorkerCount = 0;
                while (lever.CompletedRecordingTime + Time.fixedDeltaTime <= target)
                {
                    AdvanceOneStep();
                    if (Time.realtimeSinceStartupAsDouble >= deadline) break;
                }
            }
            finally { JobsUtility.JobWorkerCount = workers; }
            if (SimulationLagSeconds > 0.25 && WallSeconds - lastWarningTime > 5)
            {
                lastWarningTime = WallSeconds;
                Debug.LogWarning($"Lever simulation is {SimulationLagSeconds:F3} s behind real time. CSV steps are retained; video timestamps still follow their actual simulated states.");
            }
        }
        catch (Exception exception) { Fail(exception); }
    }

    IEnumerator CaptureFrames()
    {
        var endOfFrame = new WaitForEndOfFrame();
        // Start methods must open the CSV before the first physics sample.
        yield return endOfFrame;
        while (recording)
        {
            if (!EditorApplication.isPaused && Screen.width >= 2 && Screen.height >= 2)
            {
                try
                {
                    if (!lever.RecordingReady) throw new InvalidOperationException("The lever CSV writer is not ready.");
                    if (!firstSamplePrepared)
                    {
                        InitializeEncoder();
                        AdvanceOneStep();
                        firstSamplePrepared = true;
                        // The following render must show sample 0 before it is captured.
                    }
                    else
                    {
                        if (!timelineStarted)
                        {
                            clockStart = Time.realtimeSinceStartupAsDouble;
                            timelineStarted = true;
                        }
                        DrainReadbacks(false);
                        if (SimulationSeconds + 1e-8 >= nextCaptureTime)
                        {
                            CaptureFrame();
                            nextCaptureTime = (Math.Floor(SimulationSeconds * FrameRate + 1e-8) + 1) / FrameRate;
                        }
                    }
                }
                catch (Exception exception) { Fail(exception); }
            }
            yield return null;
            yield return endOfFrame;
        }
    }

    void AdvanceOneStep()
    {
        double stepTime = lever.NextRecordingStepTime;
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        lever.PrepareSynchronizedStep();
        long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
        plunger.AdvancePhysicsStep(stepTime);
        long t2 = System.Diagnostics.Stopwatch.GetTimestamp();
        // Rigidbody forces/positions already update PhysX; no Transform hierarchy sync is needed per substep.
        Physics.Simulate(Time.fixedDeltaTime);
        long t3 = System.Diagnostics.Stopwatch.GetTimestamp();
        lever.CompleteSynchronizedStep();
        long t4 = System.Diagnostics.Stopwatch.GetTimestamp();
        prepareTicks += t1 - t0; plungerTicks += t2 - t1; physicsTicks += t3 - t2; completeTicks += t4 - t3;
        profiledSteps++;
    }

    void InitializeEncoder()
    {
        // Keep the previously measured visible framing and native viewport pixel dimensions.
        int width = Math.Max(2, viewport.width / 2 * 2);
        int height = Math.Max(2, viewport.height / 2 * 2);
        videoTexture = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB)
            { hideFlags = HideFlags.HideAndDontSave };
        videoTexture.Create();
        if (SystemInfo.supportsAsyncGPUReadback)
        {
            for (int i = 0; i < ReadbackSlots; i++)
            {
                var buffer = new PendingFrame { pixels = new NativeArray<byte>(width * height * 4, Allocator.Persistent, NativeArrayOptions.UninitializedMemory) };
                buffers.Add(buffer);
                available.Enqueue(buffer);
            }
        }
        else frameTexture = new Texture2D(width, height, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
        string directory = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Recordings"));
        Directory.CreateDirectory(directory);
        OutputPath = Path.Combine(directory, $"lever_game_synced_{DateTime.Now:yyyyMMdd_HHmmss_fffffff}.mp4");
        var attributes = new VideoTrackAttributes {
            frameRate = new MediaRational(FrameRate), width = (uint)width, height = (uint)height, includeAlpha = false,
            bitRateMode = VideoBitrateMode.High
        };
        encoder = new MediaEncoder(OutputPath, attributes);
        frameMap = new StreamWriter(Path.ChangeExtension(OutputPath, ".frames.csv"));
        frameMap.WriteLine("frame,video_time_s,csv_state_time_s,first_completed_sample,last_completed_sample,post_step_angle_rad,post_step_plunger_compression_m,wall_time_s,simulation_lag_s");
        File.WriteAllText(Path.ChangeExtension(OutputPath, ".sync.txt"),
            "CSV=" + lever.RecordingFilePath + Environment.NewLine +
            "Video time 0 corresponds to CSV sample 0 end time " + lever.RecordingTimeOrigin.ToString("R", CultureInfo.InvariantCulture) + " s." + Environment.NewLine +
            "Each frame shows the post-physics state at csv_state_time_s. Frame 0 is sample 0." + Environment.NewLine +
            "Physics follows elapsed real time, excluding Editor pause. Physics dt=" + Time.fixedDeltaTime.ToString("R", CultureInfo.InvariantCulture) + " s. All physics samples remain in the CSV." + Environment.NewLine +
            $"Video targets {FrameRate} fps at {width}x{height}, visible Game view crop, High bitrate (render={Screen.width}x{Screen.height}, visible viewport={viewport.width}x{viewport.height}); explicit variable timestamps preserve simulation time when captures are skipped. No interpolated or invented physics samples." + Environment.NewLine +
            "Reaction torque is a step average over [time_s, physics_step_end_time_s]. Compare response peaks using physics_step_end_time_s." + Environment.NewLine +
            "The frame map records exact state times, CSV sample ranges, angles, compression, elapsed real time, and simulation lag.");
        Debug.Log($"Real-time synchronized recording started: {OutputPath} ({width}x{height}, target {FrameRate} fps, async GPU readback={SystemInfo.supportsAsyncGPUReadback})");
    }

    void CaptureFrame()
    {
        if (buffers.Count > 0 && available.Count == 0) { skippedCaptures++; return; }
        if (!screenTexture || screenTexture.width != Screen.width || screenTexture.height != Screen.height)
        {
            ReleaseTexture(ref screenTexture);
            screenTexture = new RenderTexture(Screen.width, Screen.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB)
                { hideFlags = HideFlags.HideAndDontSave };
            screenTexture.Create();
        }
        ScreenCapture.CaptureScreenshotIntoRenderTexture(screenTexture);
        bool previousSrgbWrite = GL.sRGBWrite;
        try
        {
            GL.sRGBWrite = QualitySettings.activeColorSpace == ColorSpace.Linear;
            LeverRecordingViewport.Blit(screenTexture, videoTexture, viewport);
        }
        finally { GL.sRGBWrite = previousSrgbWrite; }
        var frame = buffers.Count > 0 ? available.Dequeue() : new PendingFrame();
        frame.stateTime = lever.CompletedRecordingTime;
        frame.sample = lever.CompletedRecordingSamples - 1;
        frame.angle = lever.RecordingAngleRadians;
        frame.compression = lever.RecordingPlungerCompression;
        frame.wallTime = WallSeconds;
        frame.lag = SimulationLagSeconds;
        if (buffers.Count > 0)
        {
            frame.request = AsyncGPUReadback.RequestIntoNativeArray(ref frame.pixels, videoTexture, 0, TextureFormat.RGBA32);
            pending.Enqueue(frame);
        }
        else
        {
            var previous = RenderTexture.active;
            try
            {
                RenderTexture.active = videoTexture;
                frameTexture.ReadPixels(new Rect(0, 0, videoTexture.width, videoTexture.height), 0, 0, false);
                // AddFrame reads CPU pixel memory; uploading it back to the GPU is unnecessary.
                EncodeFrame(frame, frameTexture.GetRawTextureData<byte>());
            }
            finally { RenderTexture.active = previous; }
        }
    }

    void DrainReadbacks(bool wait)
    {
        while (pending.Count > 0)
        {
            var frame = pending.Peek();
            if (wait && !frame.request.done) frame.request.WaitForCompletion();
            if (!frame.request.done) break;
            pending.Dequeue();
            if (frame.request.hasError) throw new IOException("Asynchronous recording readback failed.");
            EncodeFrame(frame, frame.pixels);
            available.Enqueue(frame);
        }
    }

    void EncodeFrame(PendingFrame frame, NativeArray<byte> pixels)
    {
        long timestamp = (long)Math.Round((frame.stateTime - lever.RecordingTimeOrigin) * 1000000.0);
        if (timestamp <= lastTimestamp) throw new InvalidOperationException("Video timestamps must strictly increase.");
        long encodeStart = System.Diagnostics.Stopwatch.GetTimestamp();
        if (!encoder.AddFrame(videoTexture.width, videoTexture.height, videoTexture.width * 4,
            TextureFormat.RGBA32, pixels, new MediaTime(timestamp, 1000000, 1)))
            throw new IOException("The video encoder rejected a synchronized frame.");
        encodeTicks += System.Diagnostics.Stopwatch.GetTimestamp() - encodeStart;
        frameMap.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0},{1:R},{2:R},{3},{4},{5:R},{6:R},{7:R},{8:R}",
            FrameCount, timestamp / 1000000.0, frame.stateTime, lastMappedSample + 1, frame.sample,
            frame.angle, frame.compression, frame.wallTime, frame.lag));
        if (FrameCount % FrameRate == 0) frameMap.Flush();
        lastMappedSample = frame.sample;
        lastTimestamp = timestamp;
        FrameCount++;
    }

    public void SavePreview(string path)
    {
        if (buffers.Count > 0)
        {
            DrainReadbacks(true);
            if (buffers.Count == 0 || FrameCount == 0) return;
            var texture = new Texture2D(videoTexture.width, videoTexture.height, TextureFormat.RGBA32, false);
            try
            {
                // The most recently completed buffer is at the end of the available queue.
                PendingFrame latest = null;
                foreach (var buffer in available) latest = buffer;
                if (latest == null) return;
                texture.LoadRawTextureData(latest.pixels);
                File.WriteAllBytes(path, texture.EncodeToPNG());
            }
            finally { DestroyImmediate(texture); }
        }
        else if (frameTexture) File.WriteAllBytes(path, frameTexture.EncodeToPNG());
    }

    void Fail(Exception exception)
    {
        Debug.LogError("Real-time synchronized recording failed: " + exception);
        Finish();
        EditorApplication.isPlaying = false;
    }

    public void Finish()
    {
        if (!recording && encoder == null && !clockOwned) return;
        recording = false;
        StopAllCoroutines();
        EditorApplication.pauseStateChanged -= OnPause;
        double wall = WallSeconds, simulation = SimulationSeconds;
        try
        {
            DrainReadbacks(true);
            if (!string.IsNullOrEmpty(OutputPath))
                File.AppendAllText(Path.ChangeExtension(OutputPath, ".sync.txt"), Environment.NewLine +
                    string.Format(CultureInfo.InvariantCulture, "Measured wall_s={0:R}; simulation_s={1:R}; speed_ratio={2:R}; skipped_capture_requests={3}.",
                        wall, simulation, wall > 0 ? simulation / wall : 1, skippedCaptures) + Environment.NewLine + PerformanceSummary);
        }
        finally
        {
            try { encoder?.Dispose(); }
            finally
            {
                encoder = null;
                frameMap?.Dispose();
                frameMap = null;
                // Complete every outstanding transfer before freeing its native memory or source texture.
                foreach (var frame in buffers)
                {
                    if (pending.Contains(frame) && !frame.request.done) frame.request.WaitForCompletion();
                    if (frame.pixels.IsCreated) frame.pixels.Dispose();
                }
                buffers.Clear(); available.Clear(); pending.Clear();
                ReleaseTexture(ref screenTexture); ReleaseTexture(ref videoTexture);
                if (frameTexture) DestroyImmediate(frameTexture);
                frameTexture = null;
                if (clockOwned)
                {
                    if (rotorBody) rotorBody.collisionDetectionMode = originalRotorCollisionMode;
                    Physics.simulationMode = originalSimulationMode;
                    Time.timeScale = originalTimeScale;
                    Time.fixedDeltaTime = originalFixedDeltaTime;
                    Application.runInBackground = originalRunInBackground;
                    foreach (var pair in interpolation) if (pair.Key) pair.Key.interpolation = pair.Value;
                    interpolation.Clear();
                    clockOwned = OwnsPhysicsClock = false;
                }
                if (lever) lever.FinishRecording();
            }
        }
        if (FrameCount > 0)
            Debug.Log($"Synchronized video saved ({FrameCount} frames, simulation/real time={(wall > 0 ? simulation / wall : 1):F3}): {OutputPath}");
        else Debug.LogWarning("No video frames captured. Keep the Game tab visible while recording.");
    }

    static void ReleaseTexture(ref RenderTexture texture)
    {
        if (!texture) return;
        texture.Release(); DestroyImmediate(texture); texture = null;
    }
    void OnDisable() => Finish();
    void OnDestroy() => Finish();
}

[InitializeOnLoad]
public static class LeverGameViewRecordingAutoStart
{
    const string MenuPath = "Tools/Lever/Record Game View on Play";
    const string PrecisionMenuPath = "Tools/Lever/High Precision Recording (0.5 ms)";
    static string PreferenceKey => "LeverGameViewRecording." + Application.dataPath;
    public static LeverGameViewRecorder Current { get; private set; }
    public static bool Enabled => EditorPrefs.GetBool(PreferenceKey, true);
    public static bool HighPrecision => EditorPrefs.GetBool(PreferenceKey + ".HighPrecision", true);

    [MenuItem(PrecisionMenuPath)]
    static void TogglePrecision() => EditorPrefs.SetBool(PreferenceKey + ".HighPrecision", !HighPrecision);

    [MenuItem(PrecisionMenuPath, true)]
    static bool ValidatePrecision()
    {
        Menu.SetChecked(PrecisionMenuPath, HighPrecision);
        return !EditorApplication.isPlayingOrWillChangePlaymode;
    }

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
        if (state == PlayModeStateChange.EnteredEditMode)
            EditorApplication.delayCall += LeverRecordingViewport.RestoreResolution;
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

    const string PreviousSizeKey = "LeverRecordingViewport.PreviousSize";

    static void ConfigureOriginalRenderSize()
    {
        var window = FindGameView();
        var assembly = typeof(UnityEditor.Editor).Assembly;
        var sizesType = assembly.GetType("UnityEditor.GameViewSizes", true);
        var singleton = typeof(ScriptableSingleton<>).MakeGenericType(sizesType);
        var sizes = singleton.GetProperty("instance", System.Reflection.BindingFlags.Static |
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.FlattenHierarchy).GetValue(null);
        var group = Read(sizes, "currentGroup");
        var groupType = group.GetType();
        int count = (int)groupType.GetMethod("GetTotalCount", Flags).Invoke(group, null);
        int index = -1;
        for (int i = 0; i < count; i++)
        {
            var size = groupType.GetMethod("GetGameViewSize", Flags).Invoke(group, new object[] { i });
            if (Convert.ToInt32(Read(size, "width")) == 2560 && Convert.ToInt32(Read(size, "height")) == 1440)
            { index = i; break; }
        }
        if (index < 0)
        {
            var sizeType = assembly.GetType("UnityEditor.GameViewSize", true);
            var kind = assembly.GetType("UnityEditor.GameViewSizeType", true);
            var size = Activator.CreateInstance(sizeType, new object[] {
                Enum.Parse(kind, "FixedResolution"), 2560, 1440, "Lever Recording Original Framing" });
            groupType.GetMethod("AddCustomSize", Flags).Invoke(group, new[] { size });
            index = count;
        }
        SessionState.SetInt(PreviousSizeKey, Convert.ToInt32(Read(window, "selectedSizeIndex")));
        window.GetType().GetMethod("SizeSelectionCallback", Flags).Invoke(window, new object[] { index, null });
        window.Repaint();
    }

    public static void RestoreResolution()
    {
        int index = SessionState.GetInt(PreviousSizeKey, -1);
        if (index < 0) return;
        SessionState.EraseInt(PreviousSizeKey);
        var window = FindGameView();
        window.GetType().GetMethod("SizeSelectionCallback", Flags).Invoke(window, new object[] { index, null });
        window.Repaint();
    }

    public static void Store()
    {
        ConfigureOriginalRenderSize();
        // Preserve the original 2560x1440 render and the visible zoomed/panned crop.
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