using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

// Strict row-by-row replay avoids interpolation, time drift and skipped input samples.
public sealed class LeverTorqueReplayData
{
    public struct Sample
    {
        public double Time;
        public float Torque;
    }

    public Sample[] Samples { get; private set; }
    public double StepSeconds { get; private set; }
    public double SourceHingeInertia { get; private set; }

    public static LeverTorqueReplayData Load(string path)
    {
        var samples = new List<Sample>();
        double inertia = 0;
        using (var reader = new StreamReader(path))
        {
            string header = reader.ReadLine();
            if (string.IsNullOrWhiteSpace(header))
                throw new InvalidDataException("Replay CSV is empty.");
            string[] columns = header.Split(',');
            int timeIndex = Array.IndexOf(columns, "time_s");
            int torqueIndex = Array.IndexOf(columns, "input_torque_nm");
            int inertiaIndex = Array.IndexOf(columns, "hinge_inertia_kg_m2");
            if (timeIndex < 0 || torqueIndex < 0 || inertiaIndex < 0)
                throw new InvalidDataException("Replay CSV requires time_s, input_torque_nm and hinge_inertia_kg_m2.");
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                string[] values = line.Split(',');
                if (values.Length != columns.Length)
                    throw new InvalidDataException($"Column count mismatch at sample {samples.Count}.");
                double time = ParseFinite(values[timeIndex]);
                double torque = ParseFinite(values[torqueIndex]);
                if (Math.Abs(torque) > float.MaxValue)
                    throw new InvalidDataException("Torque is out of range.");
                if (samples.Count > 0 && time <= samples[samples.Count - 1].Time)
                    throw new InvalidDataException("Replay times must increase strictly.");
                if (samples.Count == 0)
                    inertia = ParseFinite(values[inertiaIndex]);
                samples.Add(new Sample { Time = time, Torque = (float)torque });
            }
        }
        if (samples.Count < 2)
            throw new InvalidDataException("Replay requires at least two samples.");
        double step = samples[1].Time - samples[0].Time;
        if (step <= 0 || step > 0.1 || (float)step <= 0)
            throw new InvalidDataException("Invalid replay physics timestep.");
        // The original float time column has rounding noise, especially late in the run.
        double tolerance = Math.Max(0.000005, step * 0.02);
        for (int i = 0; i < samples.Count; i++)
            if (Math.Abs(samples[i].Time - (samples[0].Time + i * step)) > tolerance)
                throw new InvalidDataException($"Nonuniform or missing physics sample at index {i}.");
        return new LeverTorqueReplayData {
            Samples = samples.ToArray(), StepSeconds = step, SourceHingeInertia = inertia
        };
    }

    static double ParseFinite(string text)
    {
        double value = double.Parse(text, CultureInfo.InvariantCulture);
        if (double.IsNaN(value) || double.IsInfinity(value))
            throw new InvalidDataException("Replay contains a non-finite number.");
        return value;
    }

    public static Vector3 OrientImpulse(Vector3 impulse, Vector3 normalOnReceiver)
    {
        return Vector3.Dot(impulse, normalOnReceiver) < 0f ? -impulse : impulse;
    }
}