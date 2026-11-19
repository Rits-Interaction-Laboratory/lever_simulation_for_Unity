# Lever cam contact adjustment - 2026-11-17

The prior scene used 108 convex wedges from the cam mesh. Their live contact offsets were 10 mm after scene reload. The plunger spring had k=200000 N/m, c=30 N s/m, a 50 N force cap, and no upward follow travel beyond the authored start pose.

Changes:
- Rebuilt 108 convex wedges from Assets/Models/Lever/lever_middle_cam.fbx. Maximum measured deviation of the source collider outline from the original mid-plane cam outline: 0.009582 mm. This measures mesh geometry, not PhysX's cooked hull tolerance.
- k=5000 N/m, c=60 N s/m; force cap remains 50 N.
- Added 8 mm upward spring rest-position offset and corresponding joint travel to maintain preload.
- Set both tip and cam contact offsets to 0.02 mm in Awake, because contactOffset does not survive scene serialization here.
- Solver iterations 20, velocity iterations 10; rotor uses speculative angular CCD.
- No torque filtering, sign clipping, artificial reaction torque, or hinge angle limits were introduced.

Validation uses isolated local physics scenes with a kinematic cam, a 70 g dynamic spherical follower, and dt=0.5 ms. OnCollision callbacks measure actual contact angular impulse. The original mechanism is untouched by these sweeps.

| Setting / sweep | Near-zero torque samples (<1e-5 Nm) | Sign reversals (>0.001 Nm on adjacent samples) | Peak follower speed |
| --- | ---: | ---: | ---: |
| Original, -60 to +60 deg at 60 deg/s | 38.685% | 96 | 0.3576 m/s |
| Final, -90 to +90 deg at 60 deg/s | 0% | 15 | 0.0268 m/s |
| Final, +90 to -90 deg at 60 deg/s | 0% | 13 | 0.0244 m/s |

Both final sweeps had collision callbacks on every measured step. Collider ClosestPoint proximity and PhysX cooked-hull contacts differ; the test reports that separate geometric metric for diagnosis rather than treating it as actual contact loss. Remaining sign changes can occur at cam peaks and valleys; a signed cam reaction torque cannot be required to stay strictly nonzero or have a fixed sign at every possible angle.

Limitations:
- These are controlled angular sweeps, not guarantees for every arbitrary input or high speed.
- The existing profile intentionally leaves the handle opening open. A full 360-degree sweep still loses contact around that opening.
- In an additional 6-second free-mechanism ramp/release test (0.2 Nm input plus existing 0.3 Nm s/rad resistance), gravity rotated the unrestricted lever into that opening. The last second had no contact torque. This remains unresolved pending the intended operating angle range; no arbitrary hinge stop was added.
- Source scripts, scene, and regenerated collider mesh asset retain the user's pre-existing changes. A pre-edit backup is at C:/Users/mitsu/AppData/Local/Temp/lever_contact_backup_20261117_182757.

Repeat controlled sweeps in paused Play mode with recording disabled by invoking LeverContactValidation.Run(k, c, restOffset, contactOffset, speedDegPerSec, startAngleDeg, endAngleDeg). The helper destroys its temporary bodies and unloads each temporary scene after the call.

## Follow-up: raw torque smoothness, 2026-11-17

The user's graph showed small sawtooth changes in raw contact torque. Controlled sweeps confirmed that increasing the collider count alone was insufficient: 320 flat wedges increased second-difference RMS from 0.002424 to 0.004858 Nm.

Implemented LeverCamSmoothContact, using Unity ContactModifyEvent and ContactModifyEventCCD before the physics solver. Adjacent outer edges share averaged endpoint normals; each contact normal is interpolated spatially between those endpoints. The modifier only acts on the known cam segments / plunger tip pair. Front/back face contacts are retained. It copies collider geometry and IDs on the main thread, reads immutable cached values plus solver poses in the worker callback, and unsubscribes when disabled/destroyed. No measured torque samples are averaged, clipped, or filtered over time. The CSV logger is unchanged.

The generated outline tolerance is now 0.001 mm, producing 320 wedges. The generator preserves/adds the normal modifier, and the plunger initializes it in Awake. Spring=5000 N/m, damping=60 N s/m, preload travel=8 mm, and offsets=0.02 mm are unchanged.

Same controlled sweep (-60 to +60 degrees, 60 deg/s, dt=0.5 ms):
- Before: second-difference RMS 0.002424 Nm; largest sample step 0.032767 Nm.
- After: second-difference RMS 0.000428 Nm (-82.3%); largest sample step 0.004791 Nm (-85.4%).
- Contact loss: 0%. Near-zero torque samples: 0.200% around physical zero crossings; contact loss and zero moment are separate quantities.
- Reverse sweep +90 to -90 degrees at 60 deg/s: contact loss 0%; RMS 0.000365 Nm.
- Slow 10 deg/s: contact loss 0%; RMS 0.000089 Nm.
- Fast 180 deg/s: contact loss 0%; RMS 0.001782 Nm.

Raw comparison files and figure:
- ContactValidation/contact_20261117_190809_0234.csv (before)
- ContactValidation/contact_20261117_191209_3129.csv (after)
- ContactValidation/torque_before_after.svg

Recording preferences were restored and the scene returned to Edit mode. The full-turn handle opening limitation still applies. Old recordings are unchanged; record again to see the modified physical response.
