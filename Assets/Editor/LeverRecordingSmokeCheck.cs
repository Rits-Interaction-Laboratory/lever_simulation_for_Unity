using System;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

[InitializeOnLoad]
public static class LeverRecordingSmokeCheck
{
    const string ActiveKey = "LeverRecordingSmokeCheck.Active";
    static double started;
    static LeverRecordingSmokeCheck() { EditorApplication.update += Monitor; EditorApplication.delayCall += CheckRequest; }
    static void CheckRequest()
    {
        if (!File.Exists("Temp/lever-recording-smoke-check.request") || EditorApplication.isPlayingOrWillChangePlaymode) return;
        File.Delete("Temp/lever-recording-smoke-check.request");
        Run();
    }
    [MenuItem("Tools/Lever/Verify Synchronized Recording")]
    public static void Run()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Stop Play first.");
        EditorApplication.ExecuteMenuItem("Window/General/Game");
        EditorPrefs.SetBool("LeverGameViewRecording." + Application.dataPath, true);
        SessionState.SetBool(ActiveKey, true);
        started = EditorApplication.timeSinceStartup;
        EditorApplication.isPlaying = true;
    }
    static void Monitor()
    {
        CheckRequest();
        if (!SessionState.GetBool(ActiveKey, false) || !EditorApplication.isPlaying) return;
        if (started == 0) started = EditorApplication.timeSinceStartup;
        try
        {
            var recorder = LeverGameViewRecordingAutoStart.Current;
            if (!recorder || recorder.FrameCount < 12)
            {
                if (EditorApplication.timeSinceStartup - started > 90) throw new Exception("Recording startup timed out.");
                return;
            }
            var lever = UnityEngine.Object.FindAnyObjectByType<LeverRotorTorqueTest>();
            string csv = lever.RecordingFilePath, video = recorder.OutputPath;
            recorder.Finish();
            var lines = File.ReadAllLines(csv);
            var header = lines[0].Split(',');
            var data = lines.Skip(1).Select(l => l.Split(',')).ToArray();
            int torque = Array.IndexOf(header, "plunger_contact_reaction_torque_nm");
            int total = Array.IndexOf(header, "total_reaction_torque_nm");
            int friction = Array.IndexOf(header, "friction_torque_nm");
            int end = Array.IndexOf(header, "physics_step_end_time_s");
            int angle = Array.IndexOf(header, "post_step_angle_rad");
            double Parse(string value) => double.Parse(value, CultureInfo.InvariantCulture);
            if (data.Length == 0 || torque < 0 || end < 0 || new FileInfo(video).Length < 1000) throw new Exception("Missing output.");
            foreach (var row in data)
            {
                if (row.Length != header.Length || row.Any(c => double.IsNaN(Parse(c)) || double.IsInfinity(Parse(c)))) throw new Exception("Invalid CSV.");
                if (Math.Abs(Parse(row[total]) - Parse(row[friction]) - Parse(row[torque])) > 1e-5) throw new Exception("Torque sum mismatch.");
            }
            var frames = File.ReadAllLines(Path.ChangeExtension(video, ".frames.csv")).Skip(1).Select(l => l.Split(',')).ToArray();
            if (frames.Length == 0 || Parse(frames[0][1]) != 0 ||
                int.Parse(frames[0][3]) != 0 || int.Parse(frames[0][4]) != 0)
                throw new Exception("Video frame zero must correspond to CSV sample zero.");
            double origin = Parse(data[0][end]);
            foreach (var frame in frames)
            {
                int sample = int.Parse(frame[4]);
                if (sample < 0) continue;
                if (sample >= data.Length || Math.Abs(Parse(frame[2]) - Parse(data[sample][end])) > 1e-8 ||
                    Math.Abs(Parse(frame[5]) - Parse(data[sample][angle])) > 1e-6 ||
                    Math.Abs(Parse(frame[1]) - (Parse(frame[2]) - origin)) > 1e-6) throw new Exception("Frame/CSV synchronization mismatch.");
            }
            File.WriteAllText("Temp/lever-recording-smoke-check.txt", "PASS first_frame_sample=0 video_start=0 frames=" + frames.Length + " samples=" + data.Length + Environment.NewLine + "VIDEO=" + video + Environment.NewLine + "CSV=" + csv);
            Debug.Log("Synchronized recording verification passed: " + video);
            SessionState.SetBool(ActiveKey, false);
            EditorApplication.isPlaying = false;
        }
        catch (Exception e)
        {
            File.WriteAllText("Temp/lever-recording-smoke-check.txt", "FAIL " + e);
            SessionState.SetBool(ActiveKey, false);
            Debug.LogException(e);
            EditorApplication.isPlaying = false;
        }
    }
}