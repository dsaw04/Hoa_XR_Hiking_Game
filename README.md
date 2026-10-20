# VR Hiking Game — Research Compendium (VR component)

A Unity VR exergame for **Meta Quest 3**. The player hikes a loop trail around a Mt Eden /
Maungawhau-inspired volcanic cone using **punch locomotion**: punching floating orbs in front of
the head builds momentum and walks the player forward. Along the trail the player gathers native
plant samples through a short HIIT-style punch interval ("harvest") and spends them to light
torches, which clear the fog walls gating the next stretch of the loop.

This folder contains the complete Unity project for the VR side of the P4P project, plus the
anonymised questionnaire data from the user evaluation. The companion mobile app was developed
separately and is not part of this folder.

- **Author (VR component):** Dhruv Sawant (dsaw164)
- **Engine:** Unity 6000.4.9f1 (Unity 6)
- **Target device:** Meta Quest 3 (standalone Android; min SDK 32, target SDK 34)

---

## 1. Folder structure

```
Hiking_Game/
├── README.md                        This file
├── Data/
│   └── survey_responses_anonymised.csv   Post-session questionnaire responses (see §6)
├── Assets/
│   ├── Scenes/
│   │   ├── MenuScene.unity          Home / check-in screen (build index 0)
│   │   └── SampleScene.unity        Gameplay: terrain, trail, torches, plants (build index 1)
│   ├── Scripts/                     All runtime C# (see §4)
│   │   ├── Locomotion/              Punch locomotion and movement modes
│   │   ├── Harvest/                 Plant-harvest HIIT interval and sample quota
│   │   ├── Progression/             Torches, fog gates, trail progression
│   │   ├── Nature/                  Plant interaction, ID cards, wind
│   │   ├── Menu/                    Menu scene entry point and home-screen UI
│   │   ├── UI/                      Shared world-space panel, button and pointer code
│   │   └── DevTools/                Desktop simulator for testing without a headset
│   ├── Editor/                      Editor-only tools: world generation and optimisation (see §5)
│   ├── Settings/                    URP pipeline assets (Mobile_* used on Quest), volume profiles,
│   │                                menu-scene materials
│   ├── XR/                          OpenXR loader and package settings
│   ├── Oculus/                      Meta XR project config
│   ├── Plugins/Android/             AndroidManifest.xml for the Quest build
│   ├── Resources/                   Meta XR SDK runtime settings assets
│   ├── TerrainSampleAssets/         Unity Terrain Sample Asset Pack (third party; see its
│   │                                README.pdf and ThirdPartyNotices.txt)
│   ├── TextMesh Pro/                TextMesh Pro essentials (third party)
│   ├── HDRPDefaultResources/        HDRP global settings referenced by GraphicsSettings
│   ├── New Terrain.asset            Terrain data for the hiking environment
│   ├── CraterLake_Water.mat         Crater lake material (created by WalkingTrackGenerator)
│   └── InputSystem_Actions.inputactions
├── Packages/                        manifest.json + packages-lock.json (package versions)
└── ProjectSettings/                 Unity project settings
```

Folders Unity regenerates (`Library/`, `Logs/`, `UserSettings/`, `Temp/`, `*.csproj`, `*.sln`)
are not included. Unity rebuilds them the first time the project is opened.

## 2. Requirements

| Item | Version / detail |
|---|---|
| Unity Editor | 6000.4.9f1, with the **Android Build Support** module |
| Headset | Meta Quest 3 |
| XR runtime | OpenXR (`com.unity.xr.openxr` 1.16.1), loader in `Assets/XR/Loaders` |
| Meta SDK | `com.meta.xr.sdk.all` / `com.meta.xr.sdk.core` 201.0.0 |
| Render pipeline | URP 17.4.0 (`Assets/Settings/Mobile_RPAsset.asset` for Quest) |
| Input | Input System 1.19.0 |

The full package list with exact versions is in `Packages/manifest.json` and
`Packages/packages-lock.json`. Unity resolves these when the project is opened.

## 3. Opening, running and building

1. Open the `Hiking_Game` folder in Unity Hub with Editor **6000.4.9f1**. The first import takes
   a while because `Library/` is rebuilt.
2. Open `Assets/Scenes/MenuScene.unity` and press Play.
   - **With a headset** connected (e.g. via Quest Link), the game runs in the headset.
   - **Without a headset**, the menu is driven by the mouse (point + left click), and
     `DesktopHikeSimulator` is added automatically when `SampleScene` starts with no XR device
     active (editor only):

     | Input | Action |
     |---|---|
     | Hold RMB + move mouse | Look around |
     | J / K | Punch left / right target |
     | Space | Punch targets alternately (simulates a stride) |
     | Hold Left Shift | Punch at full strength |
     | E | Investigate the plant you are next to |
     | F | Light a torch you are standing next to |
     | P | Cycle harvest auto-play: off → 50% → 80% → off |
3. To build for Quest 3: *File ▸ Build Profiles*, switch to **Android**, keep `MenuScene` and
   `SampleScene` in the scene list (in that order), then Build or Build And Run with the headset
   connected over USB.

**Game flow:** `MenuScene` (choose movement mode, intensity and duration) → `SampleScene`
(hike the loop: punch to walk → harvest plants → light torches → fog gates drop).

## 4. Source code (`Assets/Scripts`)

The project is built **code-first**: scenes hold a camera rig, terrain and environment, and the
interactive objects and UI panels are built at runtime in C# (no prefabs, no scene-authored UI,
no imported audio). Each script starts with a header comment explaining its design.

| Folder | Script | Purpose |
|---|---|---|
| Locomotion | `HikingPlayerController` | Punch locomotion: two orbs float in front of the player; hits add momentum and walk the player forward via a `CharacterController` |
| | `MovementModeManager` | Stores the chosen movement mode / intensity / duration and exposes tuning scalars (persisted in PlayerPrefs) |
| | `MovementTarget` | A punchable orb; distance-based hit detection plus a minimum controller speed |
| | `PunchOrb` | Shared visuals for everything the player punches |
| Harvest | `PlantHarvest` | The HIIT work interval that gathers one plant sample |
| | `HarvestNote` | A single punch note travelling down the lane towards the player |
| | `HarvestChart` | Note charts: timing, hand, punch type (Straight / Hook / Uppercut) and lane |
| | `PlantCollection` | Sample quota per trail sector and plant cooldown state |
| | `HarvestClearance` | Finds a standing position where the harvest lane is unobstructed |
| | `HarvestMist` | Mist behind the lane during an interval for note readability |
| | `HarvestAudio` | Procedurally synthesised harvest audio |
| Progression | `TorchProgressionManager` | Linear progression gating around the loop trail |
| | `Torch` | A torch that needs plant samples before it can be lit |
| | `TorchGateMist` | The fog wall (with colliders) at an unlit torch's gate |
| | `TorchPrompt` | Floating sign showing the current torch's quota |
| | `TorchAudio` | Procedurally synthesised ignition and fire-loop audio |
| Nature | `NatureInteractable` | Marks a plant as interactable |
| | `NatureInfoUI` | Proximity prompt and identification card; entry point to a harvest |
| | `WindSystem` | Ambient wind |
| Menu | `MenuSceneBootstrap` | Menu scene entry point; loads `SampleScene` on start |
| | `MenuSceneUI` | Home screen: session types, check-in, bottom navigation |
| UI | `VRPanel` | Shared placement/scale for world-space panels |
| | `VRButton` | Pointer-clickable region (no EventSystem) |
| | `VRPointer` | Controller laser pointer with haptic click |
| | `UIShapes` | Runtime-generated rounded / shadow / ring sprites |
| DevTools | `DesktopHikeSimulator` | Editor-only keyboard/mouse stand-in for headset and controllers |

### Movement modes

Defined in `Locomotion/MovementModeManager.cs`; the player picks one per session.

| Mode | Reach scale | Effort scale | Speed scale | Menu description |
|---|---|---|---|---|
| Gentle Hike | 0.60 | 0.60 | 0.85 | Small, easy motions close to you — fits a small space |
| Steady Hike | 1.00 | 1.00 | 1.00 | Comfortable arm-powered walking. No squats or jumps |
| Energy Boost | 1.15 | 1.25 | 1.10 | Full-body, high energy. Everything on |

Intensity (Low / Medium / High) multiplies speed by 0.80 / 1.00 / 1.25 and pace by
0.80 / 1.00 / 1.30. Duration options are 10, 20 and 30 minutes.

## 5. Editor tools (`Assets/Editor`)

Available from the Unity **Tools** menu:

| Menu item | Script | What it does |
|---|---|---|
| Tools ▸ Generate Mt Eden Hiking Trail | `WalkingTrackGenerator` | Generates the volcanic cone terrain, crater lake, circular trail, NZ native bush and terrain textures |
| Tools ▸ Repaint Mt Eden Textures Only | `WalkingTrackGenerator` | Re-applies terrain textures only |
| Tools ▸ Build Menu Scene | `MenuSceneBuilder` | Regenerates `MenuScene.unity` (overwrites it) |
| Tools ▸ Optimization ▸ Set Android Texture Compression | `AndroidTextureCompressionSetup` | One-shot Android (ASTC) import overrides for the Terrain Sample Asset Pack textures |
| Tools ▸ Optimization ▸ Convert NZFlora To Terrain Trees | `ConvertFloraToTerrainTrees` | Converts individually rendered flora into terrain tree instances (GPU-instanced) |

## 6. Evaluation data (`Data/survey_responses_anonymised.csv`)

Raw export of the post-session questionnaire, one row per participant, columns
exactly as exported. The questionnaire was shared with the companion mobile-app study, so
columns 10–29 cover the mobile app and the app-to-headset handoff; the VR-game items are the
columns from *"Which movement mode did you use?"* onwards.

- **Anonymisation:** the `Name` column has been replaced by `Participant`, with no other changes.
  IDs **P1–P19** follow the CSV row order and match the IDs used in the report.
- **Analysis set:** n = 19 (P1–P19). The final row (labelled `Excluded (under 18)`) is a
  16-year-old's response and was excluded from all analysis.
- **Device:** P4 used the desktop simulator; all others used the headset. VR-specific items
  are therefore reported for the 18 headset users.
- **Scales:** agreement items are 1–5 (5 = strongly agree). The motivation item is 1–5, where
  3 = about the same as normal exercise.
- **Test conditions:** informal sessions with no game briefing or tutorial; the researchers
  answered questions when asked. The game was streamed from a laptop to the Quest 3 rather
  than run as an installed build. No frame-rate or in-game logging was recorded.

## 7. Known limitations and suggestions for further development

- **Session duration is not enforced.** The chosen duration (`SessionMinutes`) and
  `SessionLengthScale` are stored but not yet used by gameplay; a session/trail timer would
  make the duration choice meaningful.
- **Mobile-app handoff not implemented on the headset.** The VR game does not receive sessions
  from the companion app. In testing, the handoff was simulated.
- **Lower-body mechanics.** Locomotion is upper-body only; `AllowLowerBody` is a placeholder for
  future squat/jump mechanics in Energy Boost.
- **Harvest score tiers are provisional** (see `PlantHarvest.cs`) and should be tuned in a
  headset.
- **Gentle mode** places the orbs closest to the player (reach scale 0.60). In the evaluation,
  Gentle-mode participants needed locomotion explained and reported discomfort, so this tuning
  should be revisited first.
- **Installed-build testing.** Future studies should sideload an installed build and add
  in-game logging (frame time, session length, hits) to complement the questionnaire.
