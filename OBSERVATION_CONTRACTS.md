# GAMBIT policy contracts

These layouts are release compatibility contracts. Do not reorder, resize, or
reinterpret them without exporting new models and assigning new schema IDs.
All observations are refreshed on the ML-Agents decision step. Finite
differences use `Time.fixedDeltaTime`.

## `phase3v2_c_local45`

The bundled combat model consumes 45 `float32` values.

| Index | Field | Unit and normalization |
|---:|---|---|
| 0–2 | Opponent position in the actor-local frame | Unity metres, unscaled |
| 3–5 | Actor velocity in the actor-local frame | metres/second, clamped to ±50 |
| 6–8 | Actor acceleration in the actor-local frame | metres/second², clamped to ±200 |
| 9 | Opponent distance | Unity metres, unscaled |
| 10–13 | sin/cos pitch error, then sin/cos yaw error | unitless, [-1, 1] |
| 14–16 | View angular velocity XYZ | degrees/second, clamped to ±720 |
| 17–19 | View angular acceleration XYZ | degrees/second², clamped to ±2000 |
| 20–22 | Yaw error, pitch error, combined aim error | degrees |
| 23 | Shooting rhythm category | 0 or 1 |
| 24–36 | Reserved rhythm categories | Always 0 for bundled-model compatibility |
| 37 | Action count | 1 when shooting, otherwise 0 |
| 38 | Has any represented action | 1 when shooting, otherwise 0 |
| 39 | Opponent health fraction | [0, 1] — this is not `hasTargeted` |
| 40–43 | Time since represented action, shot, reload, targeted hit | elapsed time / 5 seconds, clamped [0, 1] |
| 44 | Shot actually fired this step | 0 or 1 |

The 14 rhythm categories intentionally encode shooting only. Normalizer data
for the bundled model confirms indices 24–36 were constant zero during
training. Expanding that vocabulary requires a new schema and retrained model.

## `phase5_actor_obs_v001`

The bundled navigator consumes 231 `float32` values. World-space vectors are
rotated into the actor-yaw frame before being written.

| Indices | Field | Unit and normalization |
|---:|---|---|
| 0–2 | Self velocity XYZ | metres/second divided by 12, clamped [-1, 1] |
| 3–5 | Self angular velocity XYZ | degrees/second divided by 720, clamped [-1, 1] |
| 6 | Grounded | 0 or 1 |
| 7 | Crouched | 0 or 1 |
| 8 | Health | fraction [0, 1] |
| 9 | Ammunition | current/max [0, 1] |
| 10 | Reserved | Always 0 |
| 11 | Weapon cooldown | remaining/fire-cooldown [0, 1] |
| 12–15 | Recent-damage cue | valid, sin bearing, cos bearing, age/5 seconds |
| 16–23 | Previous action | move X/Z, turn, pitch, shoot, reload, jump, crouch |
| 24 | Planar speed | metres/second divided by 12 |
| 25 | Step displacement | metres, clamped [0, 1] |
| 26 | Short stuck mean | 25-step mean [0, 1] |
| 27 | Long stuck mean | 100-step mean [0, 1] |
| 28 | Collision mean | 25-step mean [0, 1] |
| 29 | Time without progress | elapsed/5 seconds [0, 1] |
| 30–93 | 32 torso rays | each pair is distance/40 metres, hit flag; ray `r` uses indices `30+2r`, `31+2r` |
| 94–157 | 32 foot/head rays | each pair is distance/40 metres, hit flag; ray `r` uses `94+2r`, `95+2r` |
| 158–181 | 8 floor/step rays | each triple is floor distance/3 metres, floor-present, step-present |
| 182–184 | Collision normal XYZ | actor-yaw frame, clamped [-1, 1] |
| 185–192 | Free-space summary | minimum torso-ray fraction for each of 8 sectors |
| 193 | Opponent visible | 0 or 1 |
| 194–198 | Visible opponent bearing/elevation/distance | sin/cos bearing, sin/cos elevation, `log(1+d)/log(101)` |
| 199–201 | Visible relative velocity XYZ | metres/second divided by 12, clamped [-1, 1] |
| 202–206 | Last-seen memory | valid, stale relative XYZ/50 metres, age/10 seconds |
| 207–210 | Last-heard memory | valid, sin bearing, cos bearing, age/5 seconds |
| 211–218 | Hunt bearing | one-hot 8-sector noisy bearing |
| 219–223 | Hunt distance | one-hot 5-bin distance: <4, <8, <16, <32, ≥32 metres |
| 224 | Hunt cue valid | 0 or 1 |
| 225 | Hunt cue age | elapsed/2 seconds [0, 1] |
| 226 | Hunt cue dropped | 0 or 1 |
| 227–230 | Tactical mode | one-hot: explore, memory, visible combat, stuck recovery |

## `gambit_policy_action_v1`

The in-process policy produces eight `float32` values per decision:

| Index | Meaning | Conversion to `PlayerCommand` |
|---:|---|---|
| 0 | Strafe | Clamp to [-1, 1] |
| 1 | Forward/back | Clamp to [-1, 1] |
| 2 | Yaw turn | Clamp to [-1, 1] |
| 3 | Pitch | Reserved by the current in-process adapter and forced to 0 |
| 4 | Shoot | True when > 0.5 |
| 5 | Reload | True when > 0.5 |
| 6 | Jump | True when > 0.5 |
| 7 | Crouch | True when > 0.5 |

Startup rejects bundled models whose required input names, known element
counts, or output names do not match these contracts. Each inference also
checks output lengths and rejects NaN or infinity values.

## `gambit_local45_combat_action_v1`

This ML-Agents training action contains two continuous values (yaw and pitch)
plus four binary branches (shoot, reload, jump, crouch). The frozen actor231
navigator owns movement. It also owns yaw and pitch while the opponent is
hidden; the learner owns yaw, pitch, and shooting while the opponent is
visible. A single composition controller produces the final `PlayerCommand`.

## Gen3 unified full-policy contract

`unified_fair_obs_v2_v001` retains 211 audited fields from actor231, removes
embedded previous-action/free-space-summary/tactical-mode duplicates, and
adds six normalized local linear/angular acceleration fields, for 217 values.

At each 30 Hz decision it becomes a 232-value
`gen3_champion_v2_token_v001`: 217 observation values, the previous final
applied eight-action command, four previous event counts
`[shot, hit, miss, kill]`, elapsed decision time, schema-valid, and
padding-valid. The standalone ONNX contract consumes 32 left-padded tokens
plus a 32-value padding mask. During ML-Agents training, one token is emitted
per decision and temporal memory belongs to the trainer/model.

`gen3_champion_v2_action8_v001` directly owns movement, yaw, pitch, shooting,
reload, jump, and crouch. No frozen expert or safety compositor overwrites its
output. No qualified Gen3 ONNX asset currently exists in this repository.
