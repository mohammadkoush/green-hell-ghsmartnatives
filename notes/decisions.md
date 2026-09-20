# GHSmartNatives - decisions and parked rules

Running record of his rules for this mod, dated, in his words where they matter. The code
carries the reasoning next to each rule; this file is the queue of what is said but not yet built.

## 2026-09-20 - parked under LISTEN, not built

**A scout killed without having seen the player calls no wave.**
His words: "If a scout has been killed by the player without seeing the player, that scout
can't call a wave." Today's 1.8.0 makes every scout death call a wave; that is too much. The
wave is the scout's report, and a scout that never saw him has nothing to report.

Understanding so far (not yet confirmed by him):
- "Seen" = the scout found him recently (the sighting that sends it home), or has him in sight
  or as its enemy at the moment it dies - a scout that is fighting him has seen him.
- A silent kill (bow from cover, from behind, while it walks) brings nothing: no wave, and the
  camp does not learn the spot either.
- Open: how long a past sighting counts. A scout that saw him five minutes ago and then died
  out of sight - is that a seen kill or a silent one?

**A scout must see him for 15 seconds before it can call a wave.**
His words: "Scout must see me for 15 seconds before it can call a wave." Today the sighting
is instant: the first frame the scout has him in sight it turns for home and the wave is
called. So the wave needs a watch: 15 seconds of the scout seeing him, then home and the wave.
Kill it inside those 15 seconds and there is no wave (which is the rule above, made exact).

Understanding so far (not yet confirmed by him):
- The 15 seconds are of SEEING, not of being near: the scout's own sight module holding him.
  Out of sight resets or pauses the count - open which.
- During the 15 seconds the scout keeps watching (holds where it is, or keeps its distance),
  it does not walk up and it does not attack; the camp is not told yet.
- The 15 seconds are a setting on the Scouts tab. His words: "Let's make it a slider from 0 to
  15 seconds." Range 0 to 15; 0 = the call is instant, as today. Default open - 15 is what he
  named first.
- Open: is the count continuous (broken line of sight starts over) or accumulated?

**The scout moves in silence, and watches crouched.**
His words: "Scout moving around, always in silence. Crouches for 15 seconds, as long as the
player is within 20 meters and the scout saw the player." This answers "back off or freeze":
it crouches where it is. Read as:
- A scout makes no sound on its walk - no calls, no chatter, no footstep noise the game plays
  for natives (open which of these the game actually has for a HumanAI).
- When it has seen him and he is within 20 m, it drops to a crouch and holds there, watching,
  for the slider's seconds (0 to 15). Then home at a run and the wave.
- The watch holds only while both are true: he is within 20 m AND the scout saw him. Him
  walking out past 20 m, or out of its sight, ends the watch. Open whether that resets the
  count or pauses it (the open question above, still open).
- 20 m is a number; open whether it is its own slider or the existing ScoutKeep (20 m today).

**Approached during the watch: it bolts, and tries for the wave.**
His words: "Within the 15 seconds, if the player notices the scout and approaches it, the scout
runs away instantly, trying to call for a wave." Read as: while the scout crouches and counts,
him closing the distance breaks the watch - the scout is up and running home at once. "Trying
to call" = the wave is not guaranteed: it calls only if it gets away; kill it on the run and
there is no wave (rule one).
His correction: "Runaway means trying to get the timer to reach 15 sec before a player kills
it." So ONE timer, started at the first sighting, running whether the scout crouches or runs.
It reaches 15 s: the wave is called, wherever the scout is. The scout dies first: no wave. The
run is not a second phase, it is the same count with the scout moving instead of crouching -
its way of staying alive until the count is done. After the call it keeps going home as today.
- Open: what "approaches" is - read as him getting closer than he was when the watch began,
  by a few metres (~3 m), or within ~8 m of it.
His answer to "keeps running or pauses": "I need to stay in the 20 m radius for 15 seconds
after the scout noticed me, so the counter does not start till the scout sees the player. Then
it waits for the timer, or runs away trying to stay alive till the timer is up. But if I left
the 20 m radius before the 15 seconds are up, then the scout was not sure of what it saw. And
if it keeps happening more than three times, then a wave comes in anyway. Checking on the
spot." So:
- The count starts only at a sighting. It runs while he stays within 20 m of the scout -
  crouching scout or running scout, either way.
- He leaves the 20 m before it is up: the count is void, the scout "was not sure of what it
  saw" - it goes back to scouting, no wave. That is one UNSURE sighting.
- More than three unsure sightings (the fourth): the scout has seen enough - a wave comes
  anyway, "checking on the spot": the wave is sent to where he was last seen, not to him.
- Open: unsure sightings counted per scout, or per camp. Read as per scout (it is the one
  that keeps seeing him); a new scout starts at zero.
- Open: do the unsure sightings ever expire. Read as: no, until the scout dies or calls.

**Thugs: "not joining the waves for sure."**
Reported 2026-09-20 after a session that ran 1.7.2 (the log's load line). The camp-attack Thug
fix (BossCampMinMembers, 1.7.3) was built during that session and first deployed today inside
1.8.0, so it has not been played yet. His follow-up: "Check your logs, as it is still the same."
Checked again (log closed 14:34, header line says 1.7.2): the camps that attacked had rolled 2
and 3, and 1.7.2 returned silently below four members - no camp-attack line could exist. The
1.7.3 rule (floor of two, every second attack, reason logged when skipped) is in 1.8.0, untested.
If a 1.8.0 log still has no "numbers: camp ... attacks" line, the fault is upstream of the count.

**Sense through walls, by what he is doing.**
His words: "Natives sense me two metres from behind walls. That's it. And especially if I'm
making walking noise. And 4 m when I'm running noise, but not when I'm moving in crouch
position." Today SenseRangeMetres is one number (4 m). Read as three:
- crouched (sneaking, or crouched and still): 0 - they do not sense him through walls at all
- walking: 2 m
- running: 4 m
The game's sense (AIParams.m_EnemySenseRange, shared per kind) does not know his move style,
so the number is set each tick from FPPController (IsDuck / IsRunning) - set, not chased.
- Open: standing still, upright - walking's 2 m, or crouching's 0? Read as 2 m (upright).
- Open: three sliders, or the two numbers with crouch fixed at 0.

**High ground: on alert, not blind.**
His words, on the untested item "high ground hides you": "Instead of not sensing me in high
grounds, make it where they're on high alert, but still can't see me." Read as: when he is
above them (up a tree, on a ledge) and close enough that they would otherwise have him, they
do not get him as an enemy - no attack, no sight - but the camp goes on alert: the game's own
Upset state (HumanAIGroup.State.Upset, the one between Calm and Attack), searching and sweeping
around where the sense fired, looking up nothing. They find him only when he comes down, or
when an eye actually lands on him.
- Open: what "high ground" is in metres - read as his feet more than ~2.5 m above the native.
- Open: how long the alert lasts after he goes quiet - read as the search's ForgetSeconds.
- Open: does a trap tripped from above count the same.

**The campfire is a target, found by sight or by smoke.**
His words (dictated; "Can't fire" = campfire, "sawed afire" = saw a fire): "Can we treat
campfire like players? But instead of attacking the player, they go to the campfire to reach it,
and attack the base. Campfire can't be triggering scouts. A scout must randomly see a fire, or
the smoke from a fire." Read as:
- A LIT fire of his is a thing a scout can find - by seeing the fire itself (its own sight, the
  fire within its sight range and in view), or by seeing the smoke, which shows from farther
  (open how far; a smoke column is seen well beyond 10 m - read as a wider radius, its own
  slider, line of sight to the column's top).
- No trigger by distance, no "the fire calls them": the scout has to be walking where it can
  see it. Random in the sense of the scout's own wandering.
- Found: the same watch and wave as for the player, but the wave's target is the FIRE: they
  walk to it and attack the base - the game's own construction damage (Construction.TakeDamage,
  CanBeDestroyedByAI; the AI has this for tribal raids). The player is attacked if met.
- Open: does the scout count the watch on a fire too, or is a fire a sure sighting at once.
  Read as: at once - a fire does not run away.
- Open: which constructions they hit - the fire only, or everything near it. Read as: anything
  of his within reach of the fire that the game lets AI destroy.
- Open: a fire that goes out before they arrive - do they still come. Read as: yes, to the spot.
- Earlier offer from my side (a scout visiting his firecamp, "smoke") - this is his version of it.

**Thugs, again: "still no thugs in any wave."** Said 2026-09-20 with the game closed; the only
log on disk is still the 1.7.2 one (14:34), which also has no "numbers: wave" line at all - the
game spawned no wave that session; what he calls waves are camp attacks. Stands as above: the
first 1.8.0 log decides.

**Night: one metre off everything.**
His words: "Natives at night, they lose one meter on everything, sight and sound." Read as:
between dusk and dawn (the game's own day/night), every native's sight range and every hearing
range (sneak, walk, run, swim, action) is one metre shorter. Shared AIParams, set and restored
like the stealth numbers, so it reaches every kind at once.
- Open: the through-walls sense too ("everything"), or only eyes and ears ("sight and sound").
  Read as eyes and ears only.
- Open: a fixed 1 m, or a slider. Read as a slider, default 1.
