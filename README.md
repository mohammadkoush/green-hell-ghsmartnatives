# GHSmartNatives

**Natives that hunt you, walk their camps, and come in numbers.**

Green Hell's tribes sit in their camps until you walk into them. This mod changes three things,
each on its own switch, all through the game's own AI:

- **Hunt.** A camp that has you within reach notices you and comes for you. The game's own attack
  state is started early, and kept up while you stay within a second, larger radius. Beyond it they
  give up as they always did.
- **Roam.** Natives in a calm camp never sit. Each one is always walking to a spot, and the moment
  it arrives it gets the next one. With you inside the search radius the next spot is a step toward
  you; otherwise it is a random one around the camp. The game's own rest behaviour does the walking,
  so the animation and pathing are the game's. The moment one of them has you within the notice
  radius, the hunt starts.
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
| `Roam.SearchRadiusMetres` | 150 | inside this, a calm native's next step is toward you |
| `Roam.StepMetres` | 12 | how long each searching step is |
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
