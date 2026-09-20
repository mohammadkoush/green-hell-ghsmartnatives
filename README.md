# GHSmartNatives

**Natives that hunt you, walk their camps, and come in numbers.**

Green Hell's tribes sit in their camps until you walk into them. This mod changes three things,
each on its own switch, all through the game's own AI:

- **Hunt.** A camp that has you within reach notices you and comes for you. The game's own attack
  state is started early, and kept up while you stay within a second, larger radius. Beyond it they
  give up as they always did.
- **Roam and search.** Natives in a calm camp never sit. Each one is always walking to a spot, and
  the moment it arrives it gets the next one. The search is honest: they head for where a member
  last **saw or heard** you, sweep a widening circle there, and give up after a while — never toward
  where you actually are. With nothing to go on they wander the camp. The game's own rest behaviour
  does the walking, so the animation and pathing are the game's.
- **Scouts.** Each calm camp sends one member out (its hunter, when it has one) to range far
  around the camp with long, persistent steps, looking for you. A scout does not count for the
  camp's notice radius: it has to see you, or walk into you. When it does, it turns for home at a
  run, the camp remembers where you were, and a wave is spawned at your position (once per
  cooldown). A scout hangs back in any fight.
- **Tactics.** Archers (hunters) hold their distance and throw while the others close in; the Thug
  waits at a distance until you are surrounded, a member is lost, or a timer runs out — then comes.
- **Stealth.** Crouched steps are heard from two metres less; standing still while crouched halves
  how far natives see you; your own footsteps sound as loud as the noise they make.
- **Alarm.** A camp that starts hunting calls every calm camp within range into it. Each camp wakes
  with a ring of the tribes' own spike traps around it (bow traps by choice); stepping on one hurts,
  and sends a scout to look.
- **Numbers.** Camp groups, patrols and waves spawn a random number between a floor and a ceiling
  (default 2 to 5) instead of the game's slow ramp. A wave of 4 or more always brings one Thug.

Also here, moved from Pickup Doctor: a spawn-cooldown multiplier (sooner groups and waves) and a
wave-on-demand key for testing.

**J** opens the panel: a live line (groups awake, hunting, nearest native, next spawn timers), then
a radio row per feature and sliders under each.

Quest and challenge camps are left alone unless *Story groups too* is on.

## Install

Needs [BepInEx 5](https://github.com/BepInEx/BepInEx) (x64). Drop `GHSmartNatives.dll` into
`Green Hell\BepInEx\plugins\GHSmartNatives\`. Single player. Tribes must be ON in the difficulty
preset — the panel says so when they are not.

## Settings

`BepInEx/config/com.mohammadkoush.ghsmartnatives.cfg`, written on first run. Everything is on the
panel too.

| | default | |
|---|---|---|
| `General.OpenKey` | J | the panel |
| `General.StoryGroupsToo` | off | also touch quest and challenge camps |
| `Hunt.NoticeRadiusMetres` | 45 | how close you get to a calm camp before they come |
| `Hunt.KeepHuntingRadiusMetres` | 80 | they do not calm down while you are inside this |
| `Hunt.GiveUpAfterSeconds` | 60 | seconds after losing sight of you before a native drops the hunt |
| `Roam.SearchRadiusMetres` | 60 | how wide the sweep around the last sighting grows |
| `Roam.ForgetSeconds` | 120 | no sighting or sound for this long ends the search |
| `Roam.StepMetres` | 12 | how long each searching step is |
| `Stealth.CrouchHeardMinusMetres` | 2 | crouched steps heard from this much less (the game's 5 m) |
| `Stealth.StillCrouchSightFactor` | 0.5 | still and crouched, natives see this fraction of their range |
| `Stealth.StepVolumeCrouched / Walking / Running` | 0.45 / 0.85 / 1.25 | your own steps, as loud as the noise they make |
| `Scouts.ScoutsPerCamp` / `ScoutRadiusMetres` | 1 / 80 | how many go out, how far they range |
| `Scouts.ScoutCallsWave` / `WaveCooldownSeconds` | on / 180 | the wave a sighting brings |
| `Scouts.ScoutAlarmsCamp` | off | a sighting also brings the camp and its neighbours |
| `Tactics.ArcherKeepMetres` | 10 | hunters back off below this and never walk in past it |
| `Tactics.BossWaitsForSurround` / `BossKeepMetres` | on / 14 | the Thug waits here |
| `Tactics.SurroundedCount` / `BossWaitMaxSeconds` | 2 / 45 | what releases the Thug |
| `Alarm.CampsCallEachOther` / `CallRadiusMetres` | on / 120 | a call to arms carries this far |
| `Alarm.TrapsAroundCamp` / `TrapsPerCamp` / `TrapRingMetres` | on / 3 / 18 | the ring of bow traps |
| `Alarm.TrapKind` | Spikes | the tribes' spike trap (no arrow) or their bow trap |
| `Alarm.SpikesHiddenUnderLeaves` | off | the game hides its spikes; here they show, so they can be avoided |
| `Alarm.TrapsHaveArrows` | on | the traps are armed (bow traps shoot); off: alarm only |
| `Alarm.MaxTraps` | 6 | never more in the world; the farthest from you go first to make room |
| `Alarm.TrapLifeMinutes` | 20 | a trap vanishes on its own after this |
| `Alarm.TrapTripMetres` | 1.2 | standing this close fires the trap through the game's own trigger |
| `Alarm.TrapsVanishBeyondMetres` | 150 | a trap this far behind you is removed |
| `Roam.RadiusMetres` | 12 | with nobody to look for, how far from its spot a native wanders |
| `Roam.UnstickSecondsMin / Max` | 8 / 20 | a spot never reached is replaced after this long |
| `Numbers.MembersMin / Max` | 2 / 5 | the random count for groups, patrols and waves |
| `Numbers.BossFromCount` | 4 | a wave of at least this many always has a Thug; 0 = the game's roll |
| `Numbers.SpawnCooldownMultiplier` | 1 | lower is sooner; 0.5 = half the wait |
| `Numbers.SpawnWaveKey` | Numpad 7 | a wave at your position, now |

Nothing here is written into the save. Turn a switch off and the game's own numbers and behaviour
are back at once; the leash values raised on living natives are put back too.

## Build

`powershell -ExecutionPolicy Bypass -File build.ps1` — stock .NET Framework `csc.exe`, references
from the game install. `-NoDeploy` builds without installing. MIT licensed.
