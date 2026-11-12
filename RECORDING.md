# Synchronized recording on Play

Show the Game tab, then press Play in SampleScene. Recording starts automatically.
Use the arrow keys as usual. Stop Play to finalize the MP4 and CSV.
Tools > Lever > Record Game View on Play toggles automatic recording (enabled by default).

Outputs:
- Recordings/lever_game_synced_<timestamp>.mp4
- The matching .frames.csv maps video timestamps to completed CSV sample intervals.
- The matching .sync.txt identifies the physics CSV in RecordedData.

The recorder advances physics explicitly with the existing fixed timestep (0.0005 s)
and records the rendered Game view at 100 frames per simulation second. Encoding
may run slower than real time; timestamps use simulation time, not wall time.
Rigidbody interpolation is disabled during recording and restored afterward.
Manual keyboard inputs are sampled as simulation advances.

CSV time_s is the start of the measured step. Contact torque is the step-average
impulse torque for that interval; physics_step_end_time_s is its end.
Compare a video frame with the samples named in its .frames.csv row, and use
post_step_angle_rad / post_step_plunger_compression_m for the rendered state.
total_reaction_torque_nm is the signed sum of friction and contact torque.
100 fps video samples states every 10 ms; faster contact peaks remain in the
full-rate CSV. Video frame 0 is the post-step state of CSV sample 0 (the first reaction torque). Video time zero is offset from CSV step-end time by one fixed timestep; with the fixed timestep this aligns video time with the sample time_s column.
Stopping between rendering and physics can leave final CSV samples without a video
frame; the frame map explicitly identifies every interval present in the video.

The Game tab must remain visible for end-of-frame capture.
Angle Set Mode is disabled and CSV recording enabled for a synchronized recording.
This records live manual input; it does not enable recorded-input replay.

Tools > Lever > Verify Synchronized Recording runs a short MP4/CSV check and
returns to Edit mode. Results: Temp/lever-recording-smoke-check.txt.