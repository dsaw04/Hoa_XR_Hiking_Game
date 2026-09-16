# Plant Harvest Minigame — Design

**Status:** implemented; not yet playtested in a headset
**Date:** 2026-09-03
**Supersedes:** the static-ring implementation in `Assets/Scripts/PlantHarvest.cs`

> **Two deliberate deviations from the agreed design**, both made during
> implementation and both explained in place below:
>
> 1. **The direction cone is 45°, not 60°** (§4.3). Two 60° cones whose axes are
>    90° apart overlap, which would have let one diagonal punch satisfy both
>    Straight and Uppercut — about two thirds of a chart, over the pass
>    threshold. At 45° the cones are disjoint.
> 2. **`MovementTarget` keeps its own copy of the shared visual code** (§8.1).
>    `PunchOrb` exists and `HarvestNote` uses it, but folding the locomotion orbs
>    into it would have meant editing the punch-locomotion hot path, which §8.1
>    is specifically about not doing.

---

## 1. What this changes

Gathering a plant sample currently spawns a static ring of 3–8 orbs at fixed
positions in front of the plant. It is untimed, order-free, unfailable, and
cleared at whatever pace the player likes.

This document replaces it with a **fixed-duration HIIT work interval**: a
beat-gridded stream of directional punch notes that travel toward the player,
graded against a generous accuracy threshold, separated from the next interval
by a distance-based active-recovery gate.

Locomotion is untouched. The hike, the sector rule, the torch progression and
the info card all keep working exactly as they do now.

---

## 2. The organising principle

The existing code has one knob driving three things. `MovementMode` scales
reach, punch force *and* orb count, while `MovementIntensity` does nothing to
the harvest at all. That is why Gentle mode currently reads as "shorter game"
rather than "lighter game" — the outcome `PlantCollection`'s own comments say
they were trying to avoid.

The split adopted here:

> **Mode = the *shape* of each rep. Intensity = the *volume* of reps.**

| Axis | Driven by | Rationale |
|---|---|---|
| Lane spread (lateral/vertical extent) | `MovementMode` → `ReachScale` | How far you can safely reach; how big your room is |
| Required punch speed | `MovementMode` → `EffortScale` | How forceful a motion your body is up for |
| **Note travel speed** | **Fixed — nothing scales it** | Reaction time is vision/cognition, not fitness |
| Note density | `MovementIntensity` | Volume of work per unit time |
| Interval duration | `MovementIntensity` | Total volume of work |

This is validated by the corners, both of which the current design cannot
express:

- **Gentle + High** — small, close, light punches, but dense and long. Someone
  in a studio apartment who still wants to sweat.
- **Energy Boost + Low** — big, committed, full-extension punches, sparse and
  short. Someone who wants range of motion but is tired or recovering.

**Fixed note speed is deliberate and is the decision most likely to be
questioned.** Scaling it would be an accessibility trap: reaction time degrades
with age and with several visual conditions, and it is uncorrelated with
fitness. Tying it to the mode selector would punish a player who asked for big
arm movements with a twitch test they did not ask for. Density is the honest
intensity dial — more work in the same time — and it gates nobody out.

---

## 3. The interval

### 3.1 Duration, not workload

A harvest is a **fixed-length work interval**, not a fixed-size set of orbs.
Notes stream continuously for the duration; the interval ends when the clock
ends, not when the player has finished.

This is what makes the HIIT framing real. A count-based set lets the player
control the duration — go slow, take three minutes — which is not an interval.
It also makes `MovementModeManager.SessionMinutes` meaningful for the first
time, since intervals can now be budgeted.

### 3.2 Grading and failure

| Outcome | Condition | Effect |
|---|---|---|
| **Pass** | ≥ 65% of notes **spawned** were hit cleanly | Sample banks via `PlantCollection.TryGather` |
| **Fail** | Below threshold | Nothing banks. **The plant is not consumed** and stays `Collectable` |

Three properties make this compatible with the accessibility ethos:

1. **The threshold is a percentage of notes spawned, not an absolute count.**
   Density already scales with intensity, so a Gentle player hitting 70% of 20
   notes and an Energy Boost player hitting 70% of 90 both pass. Everyone is
   graded against their own prescription. This is the single detail that makes
   a fail state defensible here at all.
2. **The threshold is generous — 65%, not 90%.** The interval is the workout.
   The threshold exists to stop someone standing still, not to demand precision.
   Rank tiers (Bronze / Silver / Gold) sit on top for players who want them.
3. **Failure costs time, and time is exercise.** In a fitness app, "you have to
   do it again" is not a punishment, it is the product. This is the one genre
   where a fail loop is aligned with the user's actual goal.

`PlantHarvest` must therefore report a *result*, not just a plant.
`PlantCollection.TryGather` gains a "completed but did not qualify" path so a
failed interval cannot silently count.

### 3.3 Lifecycle

```
press → LEAD-IN (4 s, free to abort) → NOTES (20/30/45 s) → RESULT → info card
```

**Lead-in (~4 s = 8 beats at 120 BPM = 2 bars).** Not a countdown for its own
sake — it does three jobs. It fades the lane in so the player can see where
notes will come from and orient to it. It pulses the beat on the plant so the
tempo is established before it is tested. And it gives the player a moment to
plant their feet and get their hands up, which matters when the first note may
be a hook. A player who has not internalised the tempo will miss the entire
first phrase, which under a 65% threshold is a meaningful part of their budget
spent on not knowing what tempo they are at.

**Abort semantics:**

| Exit point | Cost |
|---|---|
| During lead-in (press again, or walk away) | **Free.** No attempt recorded, no cooldown |
| After the first note (press, or leave the lane) | **Counts as a failed attempt** → 20 s retry rest |

Abort cannot be free throughout, because that is a direct bypass of the retry
rest: start, inspect the first phrase, quit if it looks hard, restart
immediately. Making abandonment cost exactly what failing costs removes the
incentive without inventing a new punishment — **quitting and failing are the
same outcome, so there is nothing to game.** The free lead-in exists for the
opposite reason: a player who pressed by accident or is facing the wrong way
must be able to back out. That is a commit window, not a trap.

**Momentum is zeroed at interval start.** `HikingPlayerController` decays
momentum at 2.2/s, so a player who punch-ran up to a plant would keep drifting
forward into their own lane for a second or two, and the notes would feel like
they were rushing them. `TorchGateMist` already sets the precedent of zeroing
momentum for exactly this class of problem.

---

## 4. The lane and the notes

### 4.1 Delivery: approaching notes on a hard-locked lane

Notes spawn ~3.5 m out at the plant and travel toward a **strike plane ~0.5 m
from the head** — 3.0 m of travel at 4.3 m/s, ≈ 0.70 s of flight.

**Why approaching notes rather than spawn-in-place:** the decisive argument is
punch quality, and it is a fitness argument rather than a game-feel one. A
target that already exists at a fixed point in space rewards the *smallest*
motion that reaches it. `MovementTarget` only checks proximity plus
`speed >= minHitSpeed`, so a Gentle-mode player can clear the current ring with
six-inch twitches at 0.66 m/s. A note moving *toward* you must be intercepted —
you extend into it or it passes. **The exercise is enforced by geometry instead
of by a speed threshold that can be gamed.**

Secondary benefit: distance to the strike plane *is* the timing error, so
accuracy is objective. "Notes that crossed the plane unhit" is unambiguous;
"notes that despawned while lit" is muddy and feels unfair when three light up
at once.

**The lane hard-locks to the plant.** It is struck from the plant to the
player's head position at the moment of pressing and does not follow the head
afterward. You are harvesting *that plant*; facing it is the correct posture,
and the instinct to face what you are punching does the work with no nagging.
This also stops mid-interval wandering, which `AbortDistance` already
half-enforces.

**`AbortDistance` should shrink from 4.5 m.** That value was matched to
`NatureInfoUI.hideDistance` and suited a static ring you stand at. The lane is
now ~3 m long and locked, so 4.5 m of drift means you have walked out the far
end. Tie it to the lane instead — roughly 2 m of lateral drift — and keep it
forgiving vertically, since terrain slope moves the player's head without them
meaning to walk away.

### 4.2 Note vocabulary: six types

Hand (2) × punch type (3).

| Punch type | Motion | Recruits |
|---|---|---|
| **Straight** (jab/cross) | Forward extension into the note | Chest, front delt, triceps |
| **Hook** | Lateral, inward across the body | Rotational core, obliques, rear delt |
| **Uppercut** | Rising from low | Biceps, front delt, hips if standing |

**Direction is the entire difference between Beat Saber and whack-a-mole.**
Strip the arrows out of Beat Saber and it is a reaction test. But the primary
argument here is a fitness one: a "touch the glowing sphere" target is cleared
by whatever motion is cheapest, whereas three enforced punch types mean the
player *cannot* do the whole interval with one muscle group. The choreography
forces variety, which is what makes this training rather than repetitive
strain. A 45-second interval of nothing but jabs is a good way to give someone
shoulder impingement.

It is also teachable and real — jab/hook/uppercut is boxing vocabulary players
either arrive with or learn in one session.

**Position and required direction must be correlated, never rolled
independently.** Uppercuts spawn low, hooks spawn wide on their own hand's
side. Random position × random direction generates notes like "high note,
uppercut it," which are physically absurd and read as a bug.

**Explicitly excluded:**

- **Bombs / do-not-hit notes.** A punish mechanic in a design that deliberately
  does not punish hard. They also fight the generous threshold — a player
  flailing at 65% is exactly the player who will clip one.
- **Dodge walls.** `MovementModeManager` documents zero lower-body mechanics.
  Even the lean-only variant, which *is* seated-accessible, is the wrong ask for
  a Gentle-mode player with back pain — the exact user that system protects.
  `AllowLowerBody` remains the gate if this is ever revisited.

### 4.3 Hit, miss, and wrong

**A note listens only to its assigned hand.**

| Event | Result |
|---|---|
| Assigned hand, correct direction, above `minHitSpeed` | **Hit** |
| Assigned hand, wrong direction, above `minHitSpeed` | **Miss**, note consumed, brief red flash |
| Other hand, any direction | **Ignored**, note lives on |
| Crosses strike plane untouched | **Miss**, silent fade-through |

**Why not "only a correct hit resolves it":** that makes flailing strictly
optimal and deletes the justification for six note types. If a wrong attempt
costs nothing and the note survives, the correct strategy is to windmill both
arms until something registers.

**Why not "any contact resolves it":** wrong-hand consumption produces misses
that feel like bugs. The off-hand is legitimately busy — mid-recovery from its
own note, travelling across a 3×3 grid — and will pass near notes it has no
business resolving. Players will report it as broken hit detection and they will
be right.

**Why the chosen rule works:** the off-hand moves freely, so false consumption
disappears; but the assigned hand must still arrive moving the right way, so
flailing does not pass. Real arm motion is not uniformly distributed — a
flailing player matches maybe a third to a half of the time, well under the 65%
threshold.

**The cone is 45° half-angle, revised down from the 60° originally specified.**
The required directions for Straight (forward) and Uppercut (up) are 90° apart,
and two 60° cones whose axes are 90° apart *overlap*: a single diagonal
up-and-forward punch satisfies both. Roughly two thirds of chart notes are
straights and uppercuts, so 60° would have handed the player a way to clear
~66% of an interval with one repeated motion — over the pass threshold, and it
would have deleted the entire reason for having three punch types. At 45° the
two cones are disjoint and the hole closes. Still generous: you have to punch
roughly the right way, not precisely.
**The generous threshold and the direction check defend each other:** the
threshold is forgiving enough that a genuine attempt passes comfortably, and
the direction check is strict enough that a non-attempt does not.

Contact is evaluated **only when the hand is above `minHitSpeed`**, so a hand
resting inside the lane cannot consume notes by loitering. `MovementTarget`
already gates on speed before proximity, in that order, and `HarvestNote` keeps
that ordering.

A wrong-direction consume must look different from a timing miss. Both count
identically in the tally, but the player needs to tell "I was late" from "I
threw the wrong punch" — those require completely different corrections.

### 4.4 Reading a note in 0.7 seconds

Three redundant channels:

| Cue | Encodes | Resolves at |
|---|---|---|
| **Colour** | Hand | Immediately, and in periphery |
| **Silhouette** | Punch type | Immediately, and in periphery |
| **Arrow** | Punch direction + polarity | ~1.5 m, foveal |
| Lane slot | Both (partially) | Passively, cumulatively |

**Silhouette:** sphere = straight, horizontally elongated capsule = hook,
vertically elongated capsule = uppercut. The rule is one sentence — *the long
axis is the way you swing; a ball means straight* — so it is learnable in a
single harvest with no tutorial. All three are non-uniformly scaled primitives
built the way `MovementTarget.Create` already builds spheres. No textures, no
meshes, no new art pipeline.

**Arrows are kept alongside silhouette, not instead of it**, because the two
are readable at *different ranges* and are therefore sequential rather than
redundant. Gross shape resolves at 3 m the instant the note spawns; the arrow
only becomes legible around 1.5 m. That gives a coarse read early — enough to
begin loading the right motion, which takes ~300 ms to initiate — and a precise
confirmation late. Arrows alone would leave the first half of every note's
flight carrying no information. Silhouette also survives peripheral viewing at
high density, where two or three notes are in flight and cannot all be
foveated; arrows do not.

The arrow adds **polarity**, which silhouette cannot carry: a horizontal capsule
says "hook" but not which way across the body.

**Build the arrow as a small procedural mesh, not a texture.** `UIShapes`
establishes SDF-rasterise-to-sprite, but it targets canvas `Image` at 100 PPU;
reusing it means a texture, a quad child, UVs, a transparent material, and
alpha-sorting against the note body. An arrow is seven vertices. A flat unlit
mesh is less code, has no transparency sorting, stays sharp at every distance,
and needs no cache. Rotating it about the note's local forward axis serves all
six types.

**Accessibility notes:**

- Hand colours are chosen for **luminance** contrast, not hue — cyan and orange,
  or cyan and white, never red/green. Distinguishable in greyscale means
  distinguishable to everyone. Relying on colour alone for hand assignment is
  the most-cited accessibility complaint about Beat Saber, and it would
  contradict this project's stated ethos in the most avoidable way. Cyan is
  already `MovementTarget.ReadyColor` and stays as the left hand.
- The arrow gets **luminance** contrast against the note body (white or
  near-black), not hue contrast — otherwise the problem returns one layer down.
- **Billboard the note's roll** so the capsule's long axis and the arrow's plane
  stay perpendicular to the view direction. Without this, hooks look like
  spheres from some angles and arrows seen edge-on vanish — a silent failure of
  the whole scheme.

---

## 5. Timing and charting

### 5.1 Metronomic grid, no music

Notes are quantised to a **120 BPM grid** (0.5 s per beat), on quarter notes at
most — one punch per beat is already brisk to sustain for 45 seconds. There is
**no backing track.**

The grid earns its place independently of music: it **structurally solves the
readability problem** created by fixed note speed plus scaling density. Two
notes cannot be 40 ms apart because the grid has no slot there. Density stops
being a continuous rate and becomes *which subdivisions are occupied* — exactly
how a Beat Saber chart works — which turns a fiddly minimum-separation
scheduler into a simple beat-slot filler.

Music was considered and dropped as too much scope. The project ships zero
audio files (`TorchAudio` synthesises everything procedurally from a fixed
seed), so a backing loop would have had to be synthesised too. What music would
have added — an audible work/rest boundary, and tempo as an adherence aid — is
partly recovered by the lead-in pulse and the hit tick below.

**Notes spawn ahead of their beat by exactly the flight time (~0.70 s), so they
*arrive* at the strike plane on the beat.** This scheduling inversion is the
thing every naive rhythm-game implementation gets wrong: spawn on the beat and
every note lands late by its flight time, which testers report as
"unresponsive."

### 5.2 Charts: a combo library, procedurally sequenced

Charts are assembled from **~10 hand-authored phrases** of 1–2 bars (2–5 notes
each), each a list of *(beat offset, hand, punch type, lane slot)* and tagged
with a density weight.

**Why not fully procedural:** random generation produces physically impossible
notes. The failure case is specific — a right hook far right, then a right
uppercut low left half a beat later: same hand, ~1.2 m of transit, 250 ms. It
does not read as "hard," it reads as broken. Preventing it needs a runtime
reachability check on every candidate against the previous note *for that hand*,
which is fiddly to tune and fails in ways that only surface in playtest. A
phrase library encodes feasibility once, by hand, and **cannot** generate an
invalid pair within a phrase. The check is then needed only at phrase
boundaries — a tiny fraction of transitions.

**Why not hand-authored full charts:** prohibitive across 1340 plants.

**Combos are the fitness content.** Random notes produce flailing. Real
combinations — jab–cross, jab–jab–hook, cross–hook–uppercut — exist because
they chain biomechanically: each punch's recovery loads the next one's rotation.
That chaining is what turns arm movement into coordinated full-torso motion,
and it is most of the training value. It also makes the game *learnable* —
after ten harvests a player recognises phrases and starts anticipating, which is
the moment a rhythm game clicks. Note salad never produces that moment.

**Lane grid: 3 wide × 3 high, fixed topology.** `ReachScale` scales the grid's
physical extent but not its shape, so phrases reference slot indices and a
Gentle player gets the same choreography compressed into a smaller volume. This
is the cleanest expression of "mode = shape, intensity = volume."

**Sequencer rules:**

- Never repeat a phrase twice consecutively.
- Prefer a phrase whose first note uses the opposite hand from the previous
  phrase's last note. `HikingPlayerController` already pays an `altBonus` for
  alternation and a `sameArmPenalty` for repetition, so the locomotion has
  *already* taught players that alternating is correct. The harvest choreography
  should speak the same language rather than contradict it.

**Seeded from plant identity, not from time.** This follows the project's
existing convention (`TorchAudio`'s seed 7301, `WalkingTrackGenerator`'s
`new System.Random(2025)`) and buys something concrete: a failed harvest retried
on the same plant gives **the same chart**. The player gets to learn the phrase
they fluffed instead of being handed a fresh random one, which makes the retry
loop feel fair rather than arbitrary. Different plants still differ, so there is
no global repetition. It also makes auto-play test runs fully deterministic.

**Known limitation:** ten phrases across a full loop (5 plants × N torches) will
start to feel familiar. Sequence variation mitigates it, and phrases can be
gated behind torch progress so later sectors introduce new material. Genuine
long-tail variety would need either many more phrases or the procedural
generator with a reachability check; the variety is not judged worth the tuning
risk.

---

## 6. Rest: the distance gate

**A plant cannot be harvested until ~50 m of path length has been travelled
since the last harvest.**

This closes an existing hole. `PlantCollection` locks samples to the current
sector — ~200 plants per wedge — which stops trailhead farming but does *not*
stop harvesting five bushes within ten metres of each other. Under the old
untimed ring that was merely dull. Under a HIIT framing it is actively harmful:
five back-to-back max-effort intervals with no recovery is how people get hurt
and how the training stimulus collapses.

**Why distance rather than a cooldown timer:**

1. **The work:rest ratio becomes the level design.** Work = harvest. Rest =
   hiking to the next plant. The rest phase does not need building — punch
   locomotion is textbook **active recovery**: lower intensity, continuous,
   keeps heart rate elevated in Zone 2 rather than letting it crash. That is a
   better recovery protocol than standing still, and it is what the game already
   does.
2. **It closes the clustering hole** in the same spatial, diegetic idiom the
   sector rule already uses. No new UI.
3. **A cooldown timer is the worst outcome for an exergame**, because it teaches
   the player that the correct action is to stop moving. Every second watching a
   cooldown is a fitness app training someone to be still.

**Parameters:**

- **~50 m.** `maxSpeed` is 5 m/s at full momentum but that is peak; realistic
  sustained punch-locomotion is well under it and momentum decays at 2.2/s, so
  50 m is roughly 30–60 s of actual punching. Work:rest lands near **1:1 to
  1:2**, the right range for intervals of this length.
- **Retries are the exception.** Making someone who failed at 60% walk 50 m and
  come back is a punishment loop. Retry on the *same* plant is gated by a flat
  **~20 s rest** (roughly one interval); a *different* plant needs the distance.
  This also quietly discourages retry-farming one easy bush.
- **Distance is accumulated path length, not displacement** — sum
  `cc.velocity.magnitude * Time.deltaTime`. Displacement from the last harvest
  point would read walking 25 m out and back as zero, and players would learn to
  pace in circles.

**Non-gated plants still open the info card with no workout.** This is already
what `NatureInfoUI` does for `QuotaMet` and `AlreadyGathered` plants, and it
preserves the stated design value that the hike is "a place you can wander and
read about plants, not only a place you grind them." It also softens the
friction of walking past valid plants.

---

## 7. Feedback: diegetic only

**No HUD, and no text at all during the interval.**

`NatureInfoUI`'s existing comment already found this problem once: the card
deliberately does not open during the workout because it hangs 1.2 m in front of
the face while the orbs sit out at the plant, so "the player would be punching
through it." Every HUD element has the same problem, and it is now worse — notes
traverse the entire length of that corridor rather than sitting still at the far
end.

But the decisive argument is not occlusion, it is that **text is unreadable to
someone working at intensity.** A player 25 seconds into a max-effort interval,
head moving, arms swinging, heart rate at 160, cannot parse
`62% · 14/22 · 0:12`. They can absolutely parse *the plant is glowing green now*
and *the arc is nearly empty*. Every gym display that works is a colour and a
bar; every one that fails is a number. This population explicitly includes
players who chose Gentle mode for fatigue reasons — the people least able to
context-switch to reading mid-effort.

| Signal | Carrier |
|---|---|
| Time remaining | Thin arc at the lane base, depleting |
| Passing or not | Plant colour: dim → green |
| Threshold crossed | Plant snaps to a lit state + haptic pulse on **both** controllers |
| Hit confirmation | Short procedural tick, pitch rising with combo streak |
| Miss | Near-silent |
| Wrong direction | Brief red flash on the consumed note |
| All numbers | The result card, after |

Putting progress on the plant also keeps the player's gaze where the game wants
it, which solves the "player turns away from the hard-locked lane" risk with no
nagging.

**The threshold-crossing moment deserves punctuation.** It converts the generous
threshold from an invisible rule into a felt event — *you have it; everything
else is upside.* Beat Saber has nothing like this; FitXR's milestone callouts do,
and they work.

**Hit audio matters more now that music is gone.** Haptics exist
(`PlantHarvest.Buzz`) and the visual pop exists (`MovementTarget` flash +
`PopOut`), but with no backing track the interval will feel eerily silent, and
silence reads as unresponsiveness. A short procedural tick in `TorchAudio`'s
idiom is ~20 lines. Pitch-shifting it up with combo streak makes **the rising
pitch the combo meter** — audible, needs no display, rewards flow directly.

**Misses stay near-silent.** A miss sound at a 35% miss rate nags the player 15
times in 30 seconds. The absence of the tick is enough.

**Tradeoff accepted:** without a timer readout, players cannot pace precisely
and some will be surprised when the interval ends. The depleting arc mitigates
it. Precise pacing matters in a competitive rhythm game, not in a 30-second
harvest whose instruction is "work hard until it stops."

---

## 8. Code structure

### 8.1 `MovementTarget` is not touched

After the decisions above, locomotion orbs and harvest notes have diverged
sharply:

| | Locomotion orb | Harvest note |
|---|---|---|
| Lifetime | Permanent | ~0.7 s of flight |
| Hits | Repeatedly, forever (what `cooldown` is for) | Exactly once |
| Motion | Follows head, driven externally in `LateUpdate` | Travels the lane itself |
| Direction | Irrelevant — magnitude only | Cone check against punch type |
| Miss | No such concept | Plane crossing *is* the fail unit |

**The argument for separating them is risk, not elegance.** Punch locomotion is
the game — if it regresses nothing else is playable, and it is the hardest thing
in the project to test without a headset. Folding travel, direction validation
and miss detection into the class the locomotion orbs run through makes every
one of those changes a chance to break what already works. `cooldown` alone is a
trap: load-bearing for locomotion (the same orb is punched hundreds of times),
meaningless for a single-use note.

Only the **visual scaffolding** is shared — create sphere, strip collider, unlit
material with the URP/Unlit fallback, colour setter, `PopOut` shrink coroutine.
~40 lines, and the part where one source of truth is genuinely wanted so notes
and locomotion orbs look like the same family of object. A **static helper, not
a base class** — inheritance would drag the hit logic along, which is the thing
being kept apart.

> **As implemented:** `PunchOrb` exists and `HarvestNote` builds itself from it,
> but `MovementTarget` still carries its own copy of that construction code. The
> two constraints in this section pull against each other — "one source of truth
> for the look" and "do not touch the locomotion path" — and the second one wins,
> because it is the one protecting the system that makes the game playable. The
> cost is that the two can drift apart visually. Folding `MovementTarget` into
> `PunchOrb` is a safe follow-up under its own test pass; it is not something to
> do in the same change as the harvest rewrite.

### 8.2 Files

| File | Status | Responsibility |
|---|---|---|
| `HarvestNote.cs` | new | Travel, direction cone, hit/miss resolution |
| `HarvestChart.cs` | new | Phrase library + sequencer, seeded per plant |
| `PunchOrb.cs` | new, small | Shared visual construction + arrow mesh |
| `HarvestAudio.cs` | new | Hit tick, lead-in pulse, threshold-crossing cue (§7) |
| `PlantHarvest.cs` | rewritten | Lane geometry, beat clock, spawning, scoring, result |
| `PlantCollection.cs` | small change | `PlantState.Recovering`, distance accumulator, pass/fail path |
| `NatureInfoUI.cs` | small change | Result display, one new prompt case |
| `HikingPlayerController.cs` | small change | Push travelled distance into `PlantCollection` |
| `DesktopHikeSimulator.cs` | small change | Auto-play harness |
| `MovementTarget.cs` | **untouched** | — |

### 8.3 `PlantState.Recovering`

The distance gate lives in `PlantCollection` and is expressed as a new
`PlantState`. That enum already *is* the eligibility vocabulary, and
`Evaluate()` is already the single place answering "what can I do with this
plant." Two things then fall out for free:

- `NatureInfoUI.UpdatePromptText` gets one more case, and its `default:` branch
  **already** falls through to `[ E ] Identify` — so "non-gated plants still
  open the card" needs no new code path.
- `TryGather` already re-checks `Evaluate() != Collectable`, so a harvest that
  started legally and finished after the gate closed is already blocked. That
  defensive re-check is written and commented; it simply starts covering one
  more case.

The accumulator needs a source, and `PlantCollection` does not know about the
player. `HikingPlayerController` pushes distance in — matching how
`TorchProgressionManager.Initialise` is already called from `BeginSession`
rather than the manager reaching outward.

### 8.4 Purity constraint

`HarvestChart` should be a **plain C# class with no `MonoBehaviour` and no
Unity-object dependency** — in: a seed and a difficulty; out: a list of
*(beat, hand, punch type, slot)*. The sequencer is the single most bug-prone
piece of this design, and keeping it pure makes it verifiable by compiling and
running it standalone, without Unity in the loop, which fits the existing
headless compile-check workflow. All editor-only hooks stay inside
`#if UNITY_EDITOR`.

---

## 9. Testing without a headset

`DesktopHikeSimulator` exists because this project is developed without a
headset attached, and `PlantHarvest.SimulatePunch` has an editor hook whose
comment describes a deliberate fallback — it grabs any remaining orb "so J/K can
always finish a set rather than deadlocking." That works only because the
current ring is order-free and untimed. **Everything above breaks it:** notes
now have a direction, a lane slot and a ~200 ms window, and grabbing any
remaining note is meaningless when only one is in flight.

**Build auto-play first; manual keys are optional.**

Game feel is unverifiable on a keyboard — whether a hook reads correctly,
whether 4.3 m/s is too fast, whether 60° is too tight, all need arms. Effort
spent making keyboard play authentic is spent on the one thing keyboard play
cannot report.

What *is* verifiable without a headset is the state machine, which is where the
bugs will be:

- Does a pass call `TryGather` and a fail not?
- Does a failed interval start the 20 s rest?
- Does `PlantState.Recovering` clear after 50 m?
- Does the card show the right rank?
- Does aborting after the first note cost an attempt, but during the lead-in not?
- Does the sequencer ever emit an unreachable transition at a phrase boundary?

An auto-player with a configurable hit rate exercises all of them in seconds, on
both branches, repeatedly. Set it either side of the threshold — say 0.5 and 0.8
— and fail and pass are both covered without touching a controller.

**This composes with the seeding decision.** Charts are seeded from plant
identity, so an auto-play run over a given plant is fully deterministic: same
chart, same hit pattern, same outcome. Bugs reproduce instead of appearing once
and vanishing.

**Shape:** an editor-only toggle on `DesktopHikeSimulator` with a hit-rate
field, resolving notes directly rather than faking controller velocity.
`HarvestNote` exposes an internal `SimulateResolve(bool hit)` in the same
`#if UNITY_EDITOR` idiom `SimulatePunch` already uses. Manual keys, if added
later: three per hand, straight/hook/uppercut — useful for eyeballing chart
pacing, little else.

---

## 10. Parameters

| Parameter | Value | Notes |
|---|---|---|
| Interval duration | 20 / 30 / 45 s | by `MovementIntensity` Low/Med/High |
| Pass threshold | **65%** of notes spawned | generous by design |
| Tempo | 120 BPM (0.5 s/beat) | quarter notes maximum |
| Beat-slot occupancy | 50% / 75% / 100% | by intensity |
| Resulting note count | **19 / 42 / 90** | measured over 400 seeds; **needs playtest — see §11** |
| Note travel speed | 4.3 m/s, **fixed** | never scaled |
| Spawn plane | 3.5 m from head | at the plant |
| Strike plane | 0.5 m from head | |
| Flight time | ~0.70 s | = spawn lead |
| Lane grid | 3 wide × 3 high | topology fixed; extent × `ReachScale` |
| Direction cone | **45°** half-angle | revised down from 60°; see §4.3 |
| Lead-in | 4 s (8 beats / 2 bars) | free abort window |
| Retry rest (same plant) | ~20 s | |
| Distance gate (new plant) | ~50 m path length | work:rest ≈ 1:1–1:2 |
| Abort distance | ~2 m lateral drift | forgiving vertically |
| Phrase library | ~10 phrases, 1–2 bars | 2–5 notes each |
| Hand colours | cyan (L) / orange (R) | luminance-separated |
| Chart seed | plant identity | deterministic retries |

---

## 11. Open items

- **Note counts at High intensity need playtesting.** 90 punches over 45 s is
  2/s sustained — every single beat carries a note at 100% occupancy. That is
  realistic for a boxing burst but very demanding to hold for the full interval.
  Beat-slot occupancy is the dial to back off, and it is the first thing to try
  if High mode proves unholdable.
- **Rank tier boundaries are provisional** — currently Bronze ≥65%, Silver ≥80%,
  Gold ≥93%, set in `HarvestResult.Rank`.
- **Hit-tick timbre and the combo pitch curve** need tuning by ear
  (`HarvestAudio`). All three clips are synthesised from fixed seeds.
- **Everything geometric needs a headset**: whether 4.3 m/s reads as fast or
  slow, whether the 45° cone feels fair, whether the 3×3 grid extent is right at
  each `ReachScale`, whether hooks read at 3 m. None of this is checkable on a
  desktop, by construction.

### What has been verified

- **Whole assembly compiles clean** against Unity's Roslyn with the project's
  real define constants (the headless check, no editor needed).
- **`HarvestChart` verified standalone**, with no Unity in the loop — which is
  what §8.4's purity constraint was for. Over 400 seeds per intensity: note
  counts land where §10 says, **zero unreachable same-hand transitions**, zero
  notes violating the slot conventions in §5.2, charts are deterministic per
  seed and differ across seeds.
- That check found and fixed two real bugs before any of it ran in Unity: the
  `Jab-jab-cross-hook` phrase asked the right hand to cross two slots in one
  beat, and the seam guard advanced the cursor by a single beat when it could
  need two, so it under-corrected from a zero gap.

---

## 12. Explicitly out of scope

- **Torch lighting stays a pure reward.** A sector already costs five 20–45 s
  work intervals separated by four ~50 m active-recovery walks — a complete
  round of 5 bouts and 4 recoveries, roughly 5–8 minutes of continuous effort.
  The correct thing at the end of a round is not another work bout; it is the
  thing that tells the body the round is over: the fog dissolving, the torch
  catching with `TorchAudio.Ignite()`, the next sector opening. If the torch also
  demands work, the sector has no terminal beat and the game never lets the
  player feel they finished anything — the specific failure mode of exergames
  people quit. `Torch.cs` already invests in that ignition moment; the moment is
  doing a job. A "finisher" interval remains trivially addable later, since
  `TryLight` is a single gated call.
- **Session summary / stats screen.** `SessionMinutes` is still unused and its
  own comment admits it. Duration-based intervals make it newly meaningful and a
  summary is the natural home for rank and accuracy data, but nothing above
  depends on it.
- **Persisting harvest performance.** `TorchProgressionManager` already has
  `CaptureState`/`RestoreState` that ranks would ride along with. Later.
- **Bombs, dodge walls, lower-body mechanics.** See §4.2.
- **Backing music.** See §5.1.
