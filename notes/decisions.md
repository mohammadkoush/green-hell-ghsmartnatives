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
