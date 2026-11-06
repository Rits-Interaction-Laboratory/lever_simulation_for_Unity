using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;

// Menu-driven integration check. A one-shot Temp request also supports unattended validation.
[InitializeOnLoad]
public static class LeverReplayVerification
{
    const string Request = "Temp/lever-replay-check.request";
    const string Active = "Temp/lever-replay-check.active";
    const string Report = "Temp/lever-replay-check.result.txt";
    const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;
    static double started;
    static double nextProgress;

    static LeverReplayVerification()
    {
        EditorApplication.delayCall += CheckRequest;
        EditorApplication.update += Monitor;
    }

    [MenuItem("Tools/Lever/Verify CSV Torque Replay")]
    public static void Run()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
            throw new InvalidOperationException("Stop Play mode before validation.");
        File.WriteAllText(Request, "requested");
        CheckRequest();
    }

    static void CheckRequest()
    {
        if (!File.Exists(Request) || EditorApplication.isPlayingOrWillChangePlaymode)
            return;
        try
        {
            var source = LeverTorqueReplayData.Load("RecordedData/lever_training_20260909_175238.csv");
            if (source.Samples.Length != 66152 || Math.Abs(source.StepSeconds - 0.0005) > 1e-10)
                throw new Exception("Unexpected source sample count or timestep.");
            if (LeverTorqueReplayData.OrientImpulse(Vector3.down, Vector3.up) != Vector3.up ||
                LeverTorqueReplayData.OrientImpulse(Vector3.up, Vector3.up) != Vector3.up)
                throw new Exception("Contact impulse sign check failed.");
            CheckRejectedInput("time_s,input_torque_nm,hinge_inertia_kg_m2\n0,0,1\n0,1,1\n");
            CheckRejectedInput("time_s,input_torque_nm,hinge_inertia_kg_m2\n0,0,1\n0.0005,NaN,1\n");
            CheckRejectedInput("time_s,input_torque_nm,hinge_inertia_kg_m2\n0,0,1\n0.0005,1,1\n0.002,0,1\n");
            File.Move(Request, Active);
            File.WriteAllText(Report, "RUNNING: parser/sign checks passed; starting full replay.");
            started = EditorApplication.timeSinceStartup;
            EditorApplication.isPaused = false;
            EditorApplication.isPlaying = true;
        }
        catch (Exception e)
        {
            File.WriteAllText(Report, "FAIL: " + e);
            if (File.Exists(Request)) File.Delete(Request);
            Debug.LogException(e);
        }
    }

    static void CheckRejectedInput(string csv)
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, csv);
            bool rejected = false;
            try { LeverTorqueReplayData.Load(path); }
            catch (InvalidDataException) { rejected = true; }
            if (!rejected) throw new Exception("Malformed replay was accepted.");
        }
        finally { File.Delete(path); }
    }

    static object Get(LeverRotorTorqueTest lever, string field) =>
        typeof(LeverRotorTorqueTest).GetField(field, Fields).GetValue(lever);

    static void Monitor()
    {
        if (!File.Exists(Active) || !EditorApplication.isPlaying)
            return;
        if (started == 0) started = EditorApplication.timeSinceStartup;
        try
        {
            var lever = UnityEngine.Object.FindFirstObjectByType<LeverRotorTorqueTest>();
            if (!lever) throw new Exception("Lever controller missing.");
            if (!lever.enabled) throw new Exception("Replay controller disabled on startup.");
            if ((bool)Get(lever, "replayFinished"))
            {
                ValidateOutput((string)Get(lever, "recordingFilePath"));
                File.Delete(Active);
                EditorApplication.isPlaying = false;
                return;
            }
            if (EditorApplication.timeSinceStartup - started > 600)
                throw new Exception("Replay validation timed out.");
            if (EditorApplication.timeSinceStartup >= nextProgress)
            {
                nextProgress = EditorApplication.timeSinceStartup + 5;
                File.WriteAllText(Report, "RUNNING: replay sample " + Get(lever, "replayIndex"));
            }
        }
        catch (Exception e)
        {
            File.WriteAllText(Report, "FAIL: " + e);
            File.Delete(Active);
            EditorApplication.isPlaying = false;
            Debug.LogException(e);
        }
    }

    static void ValidateOutput(string path)
    {
        var source = LeverTorqueReplayData.Load("RecordedData/lever_training_20260909_175238.csv");
        var ci = CultureInfo.InvariantCulture;
        int count = 0, contactRows = 0, emptyRows = 0;
        double maxTorque = 0, maxForceY = 0;
        using (var reader = new StreamReader(path))
        {
            string[] h = reader.ReadLine().Split(',');
            int torque = Array.IndexOf(h, "plunger_contact_reaction_torque_nm");
            int forceY = Array.IndexOf(h, "plunger_contact_force_y_n");
            int points = Array.IndexOf(h, "plunger_contact_point_count");
            int input = Array.IndexOf(h, "input_torque_nm");
            int time = Array.IndexOf(h, "time_s");
            int postSpeed = Array.IndexOf(h, "post_step_angular_velocity_rad_s");
            int speed = Array.IndexOf(h, "angular_velocity_rad_s");
            double previousPostSpeed = 0;
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                var c = line.Split(',');
                if (c.Length != h.Length || count >= source.Samples.Length) throw new Exception("Output row mismatch.");
                if (float.Parse(c[input], ci) != source.Samples[count].Torque ||
                    double.Parse(c[time], ci) != source.Samples[count].Time)
                    throw new Exception("Input/time mismatch at sample " + count);
                foreach (string value in c)
                {
                    double d = double.Parse(value, ci);
                    if (double.IsNaN(d) || double.IsInfinity(d)) throw new Exception("Non-finite output.");
                }
                double measured = double.Parse(c[torque], ci);
                int n = int.Parse(c[points], ci);
                if (n > 0) contactRows++;
                else { emptyRows++; if (measured != 0) throw new Exception("Stale torque without contact."); }
                if (count > 0 && Math.Abs(double.Parse(c[speed], ci) - previousPostSpeed) > 0.00001)
                    throw new Exception("Post-step response is not aligned to the next state.");
                previousPostSpeed = double.Parse(c[postSpeed], ci);
                maxTorque = Math.Max(maxTorque, Math.Abs(measured));
                maxForceY = Math.Max(maxForceY, double.Parse(c[forceY], ci));
                count++;
            }
        }
        if (count != source.Samples.Length || contactRows == 0 || maxTorque == 0 || maxForceY <= 0)
            throw new Exception("Missing replay samples or contact measurements.");
        string report = $"PASS: {count} input/time samples matched exactly. Contact rows={contactRows}, zero-contact rows={emptyRows}, max_abs_contact_torque_nm={maxTorque:R}, max_upward_contact_force_n={maxForceY:R}\nCSV={path}";
        File.WriteAllText(Report, report);
        Debug.Log(report);
    }
}