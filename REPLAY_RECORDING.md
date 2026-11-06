# Recorded torque replay and plunger reaction recording

Open `Assets/Scenes/SampleScene.unity` and press Play. On GearRotor's LeverRotorTorqueTest, `Replay Recorded Input` defaults to enabled and `Replay Csv Path` defaults to `RecordedData/lever_training_20260909_175238.csv`. Stop Play before changing replay settings.

The 66,152 input_torque_nm samples are applied in order, one per 0.0005 s physics step (33.076 s total). Keyboard input does not affect the applied input torque in replay mode. The original time_s values are retained in the output. Missing files, non-finite values, non-increasing times and missing/nonuniform steps are rejected. The physics timestep is restored on exit.

Output is a new `RecordedData/lever_training_<timestamp>.csv`; existing recordings are never overwritten. After the final sample's physical response is written, the CSV is closed and the Editor pauses. Stop Play to return to the initial scene.

## Contact torque

`plunger_contact_reaction_torque_nm` is the step-average torque exerted on GearRotor by contacts with DetentPlunger:

    tau = -dot(hingeAxis, sum(cross(contactPosition - hingeAnchor, impulseOnGear))) / fixedDeltaTime

The minus sign matches the existing input_torque_nm / angle_rad convention: opposite to right-handed rotation about the hinge's +Z axis. Each contact is counted once, in the GearRotor collision callback. All simultaneous plunger/gear collider pairs are summed, other bodies are excluded, and steps without contact record zero. Contact impulse is oriented toward the receiving gear using the contact normal. It includes horizontal force components and contact effects; this is not an isolated conservative spring term.

Unity API reference: https://docs.unity3d.com/6000.0/Documentation/ScriptReference/ContactPoint-impulse.html

Additional columns:

- `total_reaction_torque_nm`: signed sum of `friction_torque_nm` (axis viscous torque, -coefficient * angular velocity) and `plunger_contact_reaction_torque_nm` for the same physics step, in N m. Appended at the end of the CSV. This is an algebraic sum, not a sum of magnitudes; without contact it equals the viscous torque. The applied viscous torque is buffered before simulation and combined with contact torque after collision callbacks complete.

- `plunger_contact_force_x_n`, `plunger_contact_force_y_n`, `plunger_contact_force_z_n`: step-average force on the gear in world coordinates.
- `plunger_contact_point_count`: number of reported contact points during the step.
- `plunger_applied_spring_force_n`: force actually applied by the plunger script for that step, including damping and its existing clamp.
- `physics_step_end_time_s`: end of the measured interval.
- `post_step_angular_velocity_rad_s`: gear velocity after that interval.
- `step_angular_acceleration_rad_s2`: change in gear angular velocity across that interval divided by dt.

## Time alignment

The existing state columns describe the state BEFORE applying the input in that row, as in the source recording. In particular, the old `angular_acceleration_rad_s2` describes the preceding interval. The newly appended reaction columns describe the interval AFTER applying that row's input. Use `step_angular_acceleration_rad_s2` for comparison with that row's input and contact torque. Rows are buffered until the physics step and collision callbacks complete, so there is no one-row shift between input and contact response. Forces are impulse/dt averages, not instantaneous peak forces.

## Reproduction scope

Replay reproduces the input waveform and timestep, not a saved physics snapshot. It retains the CURRENT scene geometry, mass properties, spring, friction and solver settings. A warning reports differences from the source hinge inertia if settings change. Matching the entire previous trajectory requires matching physical settings and initial state. The verified run below reproduced all existing source columns exactly with the current scene; no mass-property changes were needed.

## Verification

`Tools > Lever > Verify CSV Torque Replay` performs a full replay and returns to Edit mode. It checks source parsing, invalid-input rejection, impulse sign orientation, every input/time sample, finite output, zero torque without contact, nonzero contact measurements, and post-step velocity alignment. It preserves the resulting CSV. Results are written to `Temp/lever-replay-check.result.txt`.
## Verified run (2026-09-16)

- Output: `RecordedData/lever_training_20260916_202427_2732353.csv`.
- All 66,152 samples and all 22 original columns matched the original recording numerically, including angle, velocity and compression.
- Maximum absolute contact reaction torque: 0.4182061 N m, at source time 25.891 s.
- Independent torque balance check, including input, rotational resistance and gravity, had RMS discrepancy 1.46e-6 N m (reversing the measured torque sign gives 0.243 N m).
- Parser rejection checks and finite response checks passed. Unity returned to Edit mode after verification.