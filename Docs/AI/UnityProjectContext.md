# SUPERRADIANCE — Unity project context

<!-- unity-onboarding:generated:start -->
Analyzed 2026-09-05. Local HEAD: `1336552`; substantial uncommitted work exists.
Local working files are authoritative. Public GitHub main at inspection was
`f419562b851a390b73e87dbcad502366c89b9844` (2026-08-29).

## Game and constraints

Confirmed: 2D side-view ship simulation roguelike. Direct piloting and aiming,
physical penetration, armor subcell damage, module destruction, decompression,
and hull splitting feed a campaign that carries damage forward. Damage is not
just a ship-wide HP bar. `STORY.md` owns canon; its intermediate missions remain
partly TODO. `CLAUDE.md` favors emergent simulation, existing owners, short
explanations, and minimal abstractions.

## Environment and boundaries

- Unity 6000.3.8f1; URP 17.3.0 with 2D renderer usage.
- Input System 1.18.0, activeInputHandler=1; PlayerInput calls Ship controls.
- Only enabled build scene: `Assets/Scenes/SampleScene.unity`.
- Runtime assembly `SUPERRADIANCE` references `SUPERRADIANCE.GUI`, Input System,
  URP, Burst/Mathematics, TMP, VFX and UGUI. Separate GUI and runtime Editor
  assemblies. No confirmed gameplay networking; Multiplayer Center installation
  alone is not evidence of multiplayer.
- Coplay Unity MCP is installed and connected to StrategicSpaceWar@486ba61e.
  Editor state, in-memory C# inspection and Console reads work. Computer Use can
  observe Unity after activating its window; an occluded capture initially
  returned the wrong foreground surface, so verify screenshot identity.

## Source map and startup

| Owner | Responsibility |
| --- | --- |
| Core/TickManager | 60 Hz custom simulation; early/physics/late scheduling |
| Things/Ships/Ship and ShipAi | physical ship, piloting, crew/power, AI |
| Ballistics | deterministic penetration and damage rules |
| StreamingAssets/Ships, Defs | named JSON ship layouts and module definitions |
| Core/Run/Campaign, Battle | sector spawning, travel, battle end, route progression |
| Core/Run/RunState | damaged ship and progression saves; preserve existing saves |
| UI/ShipSelectScreen | pauses world for ship choice, reloads same scene |
| Story/ScriptManager | timed dialogue coroutine and JSON cache |
| Story/DramaManager | event routing and cue dispatch |
| Story/CutSceneManager | borrows player input; creates and removes temporary ships |
| UI/CameraSystem | aim-follow gameplay camera and borrowed cutscene framing |
| Story/DialogueManager | radio/crew/system lanes and dialogue lifetime |
| UI/ShipStatusHud, ContactView | flight/weapons/damage telemetry and contacts |

Bootstrap creates TickManager before scene load. Runtime UI/managers install via
RuntimeInitializeOnLoadMethod. Ship choice precedes opening; Campaign waits for
opening completion, then sector script delays the spawn queue. `endCut` is
required for sector-owned cues; opening cleanup alone does not release these.
Dialogue waits use real time, while Campaign timeout uses simulation ticks.

## Measured scale and camera findings (before this task's rework)

- ShipGrid is 1 m. Layout coordinate spans (not full renderer bounds): V2
  53×48, destroyer 68×22, scout 23×8, dart 31×7.
- ContactView sensor range=1200 m, identification=500 m. Previous cutscene
  contacts at 240/290 m were already inside identification range.
- Campaign entryDistance=1500 m; spawn positions include an entry offset.
- Scene player: V2 at (39.3,-4.4). Camera Aim mode: minZoom=20, maxZoom=400,
  lookAhead=300, moveSmooth=zoomSmooth=0.4. Serialized ortho size=21.98.
- CutsceneDamp saved values but Following/Centering ignored them: position used
  Lerp(...,0.1) per frame, pair zoom assigned immediately. Single-target look
  retained the previous pair zoom. Same underlying issue exists in public main.
- Campaign had a 45-second spawn-wait failsafe. Long controlled briefings must
  not reach live spawning while player input is still borrowed.

## Conventions and validation

Use the existing owners; no new camera package or scene required. Maintain
serialized names. Existing tests are Editor menu self-tests (ballistics, grids,
definitions, logistics, saves etc.); Test Framework 1.6.0 is installed. No
comprehensive PlayMode suite or CI build was established in this inspection.
Before edits the Console already contained a NullReferenceException and
TickManager teardown registration warnings; distinguish them from new errors.

## Unknowns

No confirmed warp-in visual cue exists in the inspected cue dispatcher; dialogue
announces completed warp. Do not claim a warp VFX was added. No dedicated outpost
17 visual asset was identified. Temporary enemy contacts are a cinematic preview,
not a transfer of those objects into the campaign fleet. Other ship sizes and
aspect ratios require broader composition checks after the default case.

Primary evidence: CLAUDE.md, STORY.md, ProjectVersion.txt, manifest.json,
EditorBuildSettings.asset, GraphicsSettings.asset, SampleScene.unity and the
owners named above. See `Sector01CutsceneDesign.md` for rework and validation.
<!-- unity-onboarding:generated:end -->
