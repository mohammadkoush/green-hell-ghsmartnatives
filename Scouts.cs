// GHSmartNatives - scouts.
//
// His words: "Add scouts. They walk around, they look for me, and they back off when they find me.
// Then they send in a wave."
//
// A scout is one member of a calm camp - a Hunter when the camp has one, because the Hunter script
// is the only human script with a back-off goal - given a far wider walk than the others: long
// steps with a persistent heading, turning a little each step, turning for home when it gets
// ScoutRadius out. It does not count for the camp's notice radius: the camp learns of you from
// the scout only when the scout SEES you (its own SightModule) or walks into you.
//
// When it finds you: it turns for home at a run (HumanAI.m_StartPosition = its home spot, the
// game's own rest goal walks it there, m_MoveStyle = Run), the camp remembers where you were, and
// a wave is spawned at your position through EnemyAISpawnManager.SpawnWave (the same call as the
// game's own waves and the Numpad 7 key), once per cooldown. The scout hangs back in any fight
// like an archer (Holds), and resumes scouting after its retreat.
//
// Language level is C# 5 (stock Framework csc.exe) - no ?., no $"", no ??=.

using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.AI;

namespace GHSmartNatives
{
    public partial class GHSmartNativesPlugin
    {
        private ConfigEntry<bool>  _scoutsEnabled;
        private ConfigEntry<int>   _scoutsPerCamp;
        private ConfigEntry<float> _scoutRadius;
        private ConfigEntry<float> _scoutStep;
        private ConfigEntry<float> _scoutKeep;
        private ConfigEntry<bool>  _scoutWave;
        private ConfigEntry<float> _scoutWaveCooldown;
        private ConfigEntry<bool>  _scoutAlarmsCamp;
        private ConfigEntry<float> _scoutRetreat;
        private ConfigEntry<float> _scoutWatch;
        private ConfigEntry<float> _scoutWatchRadius;
        private ConfigEntry<int>   _scoutUnsureMax;
        private ConfigEntry<bool>  _scoutSilent;
        private ConfigEntry<bool>  _scoutFires;

        private void BindScoutConfig()
        {
            _scoutsEnabled = Config.Bind("Scouts", "Enabled", true,
                "Each calm camp sends a scout out to walk the area and look for you. It backs off when " +
                "it finds you, and a wave comes.");
            _scoutsPerCamp = Config.Bind("Scouts", "ScoutsPerCamp", 1,
                new ConfigDescription("How many members a camp sends out. A camp keeps at least one at home.",
                    new AcceptableValueRange<int>(0, 3)));
            _scoutRadius = Config.Bind("Scouts", "ScoutRadiusMetres", 80f,
                new ConfigDescription("How far from its camp a scout ranges.", new AcceptableValueRange<float>(20f, 300f)));
            _scoutStep = Config.Bind("Scouts", "ScoutStepMetres", 20f,
                new ConfigDescription("How long each of its steps is.", new AcceptableValueRange<float>(6f, 50f)));
            _scoutKeep = Config.Bind("Scouts", "ScoutKeepMetres", 20f,
                new ConfigDescription("In a fight a scout hangs back at this distance, like an archer.",
                    new AcceptableValueRange<float>(5f, 40f)));
            _scoutWave = Config.Bind("Scouts", "ScoutCallsWave", true,
                "A scout that finds you sends a wave at your position.");
            _scoutWaveCooldown = Config.Bind("Scouts", "WaveCooldownSeconds", 180f,
                new ConfigDescription("A camp's scouts cannot call another wave sooner than this.",
                    new AcceptableValueRange<float>(30f, 900f)));
            _scoutAlarmsCamp = Config.Bind("Scouts", "ScoutAlarmsCamp", false,
                "A scout that finds you also brings its own camp (and its neighbours) into the hunt, " +
                "not just a wave.");
            // THE WATCH. His rules, 2026-09-20, in order: "If a scout has been killed by the player
            // without seeing the player, that scout can't call a wave." "Scout must see me for 15
            // seconds before it can call a wave." "A slider from 0 to 15." "Crouches for 15 seconds,
            // as long as the player is within 20 metres and the scout saw the player." "If the player
            // notices the scout and approaches it, the scout runs away instantly, trying to call for
            // a wave" - "runaway means trying to get the timer to reach 15 sec before a player kills
            // it." "If I left the 20 m radius before the 15 seconds are up, then the scout was not
            // sure of what it saw. And if it keeps happening more than three times, then a wave
            // comes in anyway, checking on the spot." One timer, started by the sighting, counting
            // while he stays inside the radius, crouching scout or running scout; it reaches the
            // end - the wave; he leaves the radius - void, one unsure sighting; the scout dies - no
            // wave. Nothing else calls one.
            _scoutWatch = Config.Bind("Scouts", "WatchSeconds", 15f,
                new ConfigDescription("A scout that sees you crouches and watches this long before it can " +
                    "call a wave. 0 = the call is instant.", new AcceptableValueRange<float>(0f, 15f)));
            _scoutWatchRadius = Config.Bind("Scouts", "WatchRadiusMetres", 20f,
                new ConfigDescription("The watch counts only while you are within this distance of the scout. " +
                    "Leave it before the time is up and the scout was not sure of what it saw.",
                    new AcceptableValueRange<float>(5f, 60f)));
            _scoutUnsureMax = Config.Bind("Scouts", "UnsureSightingsBeforeWave", 3,
                new ConfigDescription("After more than this many unsure sightings a wave comes anyway, to " +
                    "check the spot.", new AcceptableValueRange<int>(0, 10)));
            _scoutSilent = Config.Bind("Scouts", "Silent", true,
                "A scout makes no sound on its walk - no calls, no chatter (his rule: 'scout moving " +
                "around, always in silence').");
            _scoutFires = Config.Bind("Scouts", "FiresAreTargets", true,
                "A lit fire of yours that a scout sees - the fire, or its smoke, within the native's " +
                "own sight range - is a sure sighting: a wave is sent to the fire and attacks the base. " +
                "A fire never calls anyone by itself; the scout has to see it.");
            _scoutRetreat = Config.Bind("Scouts", "RetreatSeconds", 25f,
                new ConfigDescription("How long a scout runs for home before it goes scouting again.",
                    new AcceptableValueRange<float>(5f, 120f)));
        }

        private class ScoutState
        {
            public float Heading; public float RetreatUntil; public float FoundAt;
            public Item Errand;             // a trap to walk to and reset - his rule, see TripSendsScout
            public Vector3 ErrandAt; public float ErrandSince;
            public bool ErrandRun;          // a neighbour's scout runs, to make up the distance
            public bool Placing;            // the errand is a spot to set a new trap at, not a trap to reset
            public float NextTrapAt;        // not another trap from this scout before this
            public float WatchSince;        // 0 = no watch; else when the sighting started the count
            public float WatchFrom;         // his distance when it started - closing in makes it bolt
            public Vector3 WatchSpot;       // where he was when seen - the spot a wave checks
            public bool Bolted;             // running home with the count still going
            public int Unsure;              // sightings he walked out of before the count was up
            public float NextFireLook;      // fires are looked for once a second, not every frame
        }

        // A TRIPPED TRAP SENDS A SCOUT, NOT A WAVE. His words: "When triggering a trap the game should
        // send a scout. When the scout reaches the trap it resets it. If the scout sees me or hears
        // me, it runs back home and sends a wave. More logic: it gives the player the chance to
        // avoid being spotted." So the trip is an errand for the camp's scout (one is assigned if
        // none is out): walk to the trap, re-arm it, go back to scouting. The scout's own eyes and
        // ears on the way are the same as ever - a sighting means home at a run and a wave.
        // HIS RULE, 2026-09-20: "A tripped trap, regardless of where I am, should send a scout. If
        // the local tribe is empty, send one from the neighbours' tribe - and cut their time in half
        // so they reach the trap faster. Only if the local tribe is empty." So: the trap's own camp
        // if it is alive and calm; failing that the nearest calm camp within the call radius, whose
        // scout RUNS instead of walking. A camp that is fighting keeps fighting - its scout is not
        // pulled out of the line.
        internal bool TripSendsScout(AIs.HumanAIGroup g, Item trap, Vector3 at)
        {
            if (!_scoutsEnabled.Value) return false;
            AIs.HumanAIGroup camp = null;
            bool neighbour = false;
            if (g != null && g.m_Active && g.m_Members != null && g.m_Members.Count > 0 && g.m_State == AIs.HumanAIGroup.State.Calm) camp = g;
            if (camp == null)
            {
                camp = NearestCalmCamp(at, g);
                neighbour = camp != null;
            }
            if (camp == null)
            {
                // "There needs to be a scout as soon as a trap is sprung." Every active camp is
                // within the game's wake distance of him, so a camp that cannot send one is either
                // fighting him already or dead. Logged once a minute, so the log says which.
                if (Time.time - s_NoScoutSaidAt > 60f)
                {
                    s_NoScoutSaidAt = Time.time;
                    int fighting = 0, alive = 0;
                    if (AIs.HumanAIGroup.s_AIGroups != null)
                        for (int i = 0; i < AIs.HumanAIGroup.s_AIGroups.Count; i++)
                        {
                            AIs.HumanAIGroup o = AIs.HumanAIGroup.s_AIGroups[i];
                            if (o == null || !o.m_Active || !Ours(o) || o.IsWave() || o.IsPatrol() || o.m_Members == null || o.m_Members.Count == 0) continue;
                            alive++;
                            if (o.m_State != AIs.HumanAIGroup.State.Calm) fighting++;
                        }
                    Logger.LogInfo("scouts: trap tripped but no calm camp to send a scout - " + alive + " camp(s) awake with members, " + fighting + " of them fighting or upset");
                }
                return false;
            }

            AssignScouts(camp);
            AIs.HumanAI scout = null;
            for (int i = 0; i < camp.m_Members.Count; i++) if (IsScout(camp.m_Members[i])) { scout = camp.m_Members[i]; break; }
            if (scout == null && camp.m_Members.Count >= 1)
            {
                // A camp of one sends its one.
                scout = camp.m_Members[0];
                if (scout != null && !IsScout(scout)) { ScoutState fresh = new ScoutState(); fresh.Heading = UnityEngine.Random.Range(0f, 360f); s_Scouts[scout] = fresh; }
            }
            if (scout == null) return false;
            ScoutState sc = s_Scouts[scout];
            if (sc.Errand != null && Time.time - sc.ErrandSince < 120f) return true;    // already on its way
            sc.Errand = trap; sc.ErrandAt = at; sc.ErrandSince = Time.time; sc.ErrandRun = neighbour; sc.Placing = false;
            sc.RetreatUntil = 0f;
            scout.m_StartPosition = at;
            Vector3 d = at - scout.transform.position; d.y = 0f;
            if (d.sqrMagnitude > 0.01f) scout.m_StartForward = d.normalized;
            scout.m_MoveStyle = neighbour ? Enums.AIMoveStyle.Run : Enums.AIMoveStyle.Walk;
            Logger.LogInfo("scouts: '" + scout.name + "' of '" + camp.name + "' " + (neighbour ? "(a neighbour camp - the trap's own is gone) RUNS" : "sent")
                + " to a tripped trap " + Mathf.RoundToInt(d.magnitude) + " m away");
            Say(neighbour ? "A native from a nearby camp runs to see about the trap" : "A native comes to see about the trap");
            return true;
        }

        private AIs.HumanAIGroup NearestCalmCamp(Vector3 at, AIs.HumanAIGroup except)
        {
            AIs.HumanAIGroup best = null; float bestD = _callRadius.Value;
            if (AIs.HumanAIGroup.s_AIGroups == null) return null;
            for (int i = 0; i < AIs.HumanAIGroup.s_AIGroups.Count; i++)
            {
                AIs.HumanAIGroup o = AIs.HumanAIGroup.s_AIGroups[i];
                if (o == null || o == except || !o.m_Active || !Ours(o) || o.IsWave() || o.IsPatrol()) continue;
                if (o.m_State != AIs.HumanAIGroup.State.Calm || o.m_Members == null || o.m_Members.Count == 0) continue;
                float d = ClosestMember(o, at);
                if (d < bestD) { bestD = d; best = o; }
            }
            return best;
        }

        private void FinishErrand(AIs.HumanAI m, ScoutState sc)
        {
            Item t = sc.Errand;
            sc.Errand = null;
            if (t == null) return;
            try
            {
                if (_trapsArmed.Value) ArmTrap(t, ItemsManager.Get(), t.transform.position);
                s_TrippedAt.Remove(t);
                Logger.LogInfo("scouts: '" + m.name + "' reset the trap");
            }
            catch (Exception ex) { HuntLog("trap reset failed: " + ex.Message); }
        }
        private static float s_NoScoutSaidAt = -100f;
        private static readonly Dictionary<AIs.HumanAI, ScoutState> s_Scouts = new Dictionary<AIs.HumanAI, ScoutState>();
        private static readonly Dictionary<AIs.HumanAIGroup, float> s_LastScoutWave = new Dictionary<AIs.HumanAIGroup, float>();
        private static int s_ScoutLogged;

        internal static bool IsScout(AIs.HumanAI m) { return m != null && s_Scouts.ContainsKey(m); }

        /// <summary>Make sure a calm camp has its scouts - the Hunter first, and never its last member.</summary>
        private void AssignScouts(AIs.HumanAIGroup g)
        {
            if (!_scoutsEnabled.Value || g.m_Members == null || g.m_Members.Count < 2) return;
            int have = 0;
            for (int i = 0; i < g.m_Members.Count; i++) if (IsScout(g.m_Members[i])) have++;
            int want = Mathf.Min(_scoutsPerCamp.Value, g.m_Members.Count - 1);
            for (int pass = 0; pass < 2 && have < want; pass++)
            {
                for (int i = 0; i < g.m_Members.Count && have < want; i++)
                {
                    AIs.HumanAI m = g.m_Members[i];
                    if (m == null || IsScout(m) || IsBoss(m)) continue;
                    if (pass == 0 && !IsArcher(m)) continue;          // hunters first
                    ScoutState st = new ScoutState();
                    st.Heading = UnityEngine.Random.Range(0f, 360f);
                    s_Scouts[m] = st;
                    have++;
                    if (s_ScoutLogged < 8)
                    {
                        s_ScoutLogged++;
                        Logger.LogInfo("scouts: '" + m.name + "' of '" + g.name + "' goes scouting ("
                            + (IsArcher(m) ? "a hunter, can back off" : "no back-off goal, walks home instead") + ")");
                    }
                }
            }
        }

        /// <summary>The scout's own walk, and its find-and-retreat. Returns true when it handled this member.</summary>
        private bool ScoutStep(AIs.HumanAIGroup g, AIs.HumanAI m, RoamState st, Being target, float now)
        {
            ScoutState sc;
            if (!s_Scouts.TryGetValue(m, out sc)) return false;

            Vector3 here = m.transform.position;

            // A watch in progress: the count, the crouch, the bolt, the call. Before anything else.
            if (sc.WatchSince > 0f) { WatchTick(g, m, sc, st.Home, target, now); return true; }

            // Retreating: leave it to the rest goal, which is walking it home.
            if (now < sc.RetreatUntil) return true;

            // A lit fire of his in sight: a sure sighting, the wave goes to the fire.
            if (_scoutFires.Value && now >= sc.NextFireLook)
            {
                sc.NextFireLook = now + 1f;
                if (LookForFire(g, m, sc, st.Home)) return true;
            }

            // On an errand to a trap: walk there (the rest goal does it), reset it, then scout on.
            // Its eyes stay open on the way - the sighting check below runs first.
            if (sc.Errand != null)
            {
                if (sc.Errand == null || now - sc.ErrandSince > 180f) sc.Errand = null;
                else if (Vector3.Distance(here, sc.ErrandAt) < 2.5f) { FinishErrand(m, sc); return true; }
            }
            if (sc.Placing)
            {
                if (now - sc.ErrandSince > 180f) { sc.Placing = false; sc.NextTrapAt = now + 30f; }
                else if (Vector3.Distance(here, sc.ErrandAt) < 2.5f)
                {
                    sc.Placing = false;
                    sc.NextTrapAt = now + _trapEvery.Value;
                    PlaceTrapAt(g, sc.ErrandAt, m);
                    return true;
                }
            }

            // Found you? Its own eyes (handled first, below, before the group can attack on them)
            // or it walked into you.
            if (target != null)
            {
                bool sees = m.m_SightModule != null && m.m_SightModule.m_VisibleBeings != null && m.m_SightModule.m_VisibleBeings.Contains(target);
                float dt = Vector3.Distance(here, target.transform.position);
                if (sees || dt < 6f) { ScoutSighting(g, m, sc, st.Home, target, dt, sees); return true; }
            }

            // Passing a sprung trap: reset it. "A wandering scout checks on traps."
            ResetTrapsNear(m);

            if (sc.Errand != null || sc.Placing)
            {
                // Keep the spot as the destination; the rest goal re-paths on its own. A neighbour's
                // scout keeps running (set, not chased: only when the game put it back to a walk).
                if (Vector3.Distance(m.m_StartPosition, sc.ErrandAt) > 1f) m.m_StartPosition = sc.ErrandAt;
                if (sc.ErrandRun && m.m_MoveStyle != Enums.AIMoveStyle.Run) m.m_MoveStyle = Enums.AIMoveStyle.Run;
                return true;
            }

            bool arrived = Vector3.Distance(here, m.m_StartPosition) < 3f;
            if (!arrived && now < st.NextAt) return true;
            st.NextAt = now + UnityEngine.Random.Range(_roamEveryMin.Value, Mathf.Max(_roamEveryMin.Value, _roamEveryMax.Value));

            // A persistent heading that wanders, and turns for home past the radius.
            float fromHome = Vector3.Distance(here, st.Home);
            if (fromHome > _scoutRadius.Value)
            {
                Vector3 back = st.Home - here;
                sc.Heading = Mathf.Atan2(back.x, back.z) * Mathf.Rad2Deg + UnityEngine.Random.Range(-30f, 30f);
            }
            else sc.Heading += UnityEngine.Random.Range(-40f, 40f);

            Vector3 dir = Quaternion.Euler(0f, sc.Heading, 0f) * Vector3.forward;
            Vector3 want = here + dir * _scoutStep.Value;
            NavMeshHit hit;
            if (!NavMesh.SamplePosition(want, out hit, 6f, NavMesh.AllAreas))
            {
                sc.Heading += 90f;                                   // blocked: turn, try next time
                return true;
            }
            m.m_StartPosition = hit.position;
            Vector3 fwd = hit.position - here; fwd.y = 0f;
            if (fwd.sqrMagnitude > 0.01f) m.m_StartForward = fwd.normalized;
            m.m_MoveStyle = Enums.AIMoveStyle.Walk;

            // A trap due, and the world short of them: this step's spot becomes the trap's spot -
            // the scout walks there and sets it on arrival (PlaceTrapAt tests the spot again).
            if (_trapsEnabled.Value && now >= sc.NextTrapAt)
            {
                string why;
                if (TrapSpotOk(hit.position, out why))
                {
                    sc.Placing = true; sc.ErrandAt = hit.position; sc.ErrandSince = now; sc.ErrandRun = false;
                }
                else sc.NextTrapAt = now + 15f;         // not here; look again a few steps on
            }
            return true;
        }

        /// <summary>Home at a run; the camp remembers where; the scout's eyes are its own again after RetreatSeconds.</summary>
        private void RunHome(AIs.HumanAIGroup g, AIs.HumanAI m, ScoutState sc, Vector3 home, Vector3 seenAt)
        {
            float now = Time.time;
            sc.Errand = null; sc.Placing = false;
            sc.RetreatUntil = now + _scoutRetreat.Value;
            Vector3 here = m.transform.position;
            m.m_StartPosition = home;
            Vector3 back = home - here; back.y = 0f;
            if (back.sqrMagnitude > 0.01f) m.m_StartForward = back.normalized;
            m.m_MoveStyle = Enums.AIMoveStyle.Run;
            RememberSeen(g, seenAt);
        }

        /// <summary>The wave the watch earned: at your position (the game's wave), the camp told where.</summary>
        private void CallWave(AIs.HumanAIGroup g, Vector3 seenAt, string why)
        {
            RememberSeen(g, seenAt);
            if (_scoutAlarmsCamp.Value) Alarm(g, seenAt, why, true);
            if (_scoutWave.Value) ScoutWave(g);
        }

        /// <summary>A sighting. WatchSeconds 0: the wave at once. Else the watch begins: crouch here and count.</summary>
        private void ScoutSighting(AIs.HumanAIGroup g, AIs.HumanAI m, ScoutState sc, Vector3 home, Being target, float dt, bool sees)
        {
            float now = Time.time;
            sc.FoundAt = now;
            Vector3 at = target.transform.position;
            if (_scoutWatch.Value <= 0f)
            {
                RunHome(g, m, sc, home, at);
                Say("A scout spotted you at " + Mathf.RoundToInt(dt) + " m - it backs off");
                Logger.LogInfo("scouts: '" + m.name + "' found you at " + dt.ToString("F0") + " m (" + (sees ? "saw you" : "walked into you") + ") - running home, the wave is called at once");
                CallWave(g, at, "scout's report");
                return;
            }
            sc.WatchSince = now; sc.WatchFrom = dt; sc.WatchSpot = at; sc.Bolted = false;
            sc.Errand = null; sc.Placing = false;
            // Crouch where it stands: the rest goal crouches a native that is AT its spot.
            m.m_StartPosition = m.transform.position;
            Vector3 face = at - m.transform.position; face.y = 0f;
            if (face.sqrMagnitude > 0.01f) m.m_StartForward = face.normalized;
            m.m_MoveStyle = Enums.AIMoveStyle.Walk;
            Say("A scout has spotted you at " + Mathf.RoundToInt(dt) + " m - it crouches and watches");
            Logger.LogInfo("scouts: '" + m.name + "' saw you at " + dt.ToString("F0") + " m (" + (sees ? "saw you" : "walked into you") + ") - watching for "
                + _scoutWatch.Value.ToString("F0") + " s" + (sc.Unsure > 0 ? " (unsure " + sc.Unsure + " time(s) so far)" : ""));
        }

        /// <summary>Every tick of a watch: out of the radius = unsure; closing in = bolt; time up = the wave.</summary>
        private void WatchTick(AIs.HumanAIGroup g, AIs.HumanAI m, ScoutState sc, Vector3 home, Being target, float now)
        {
            if (target == null) { sc.WatchSince = 0f; return; }
            Vector3 at = target.transform.position;
            float dt = Vector3.Distance(m.transform.position, at);
            float elapsed = now - sc.WatchSince;

            if (dt > _scoutWatchRadius.Value)
            {
                // "Then the scout was not sure of what it saw."
                sc.WatchSince = 0f; sc.Bolted = false;
                sc.Unsure++;
                Logger.LogInfo("scouts: '" + m.name + "' lost you past " + Mathf.RoundToInt(_scoutWatchRadius.Value) + " m after " + elapsed.ToString("F0")
                    + " s - not sure of what it saw (unsure sighting " + sc.Unsure + ")");
                if (sc.Unsure > _scoutUnsureMax.Value)
                {
                    sc.Unsure = 0;
                    Say("The scout has seen enough - a wave comes to check the spot");
                    Logger.LogInfo("scouts: '" + m.name + "' - more than " + _scoutUnsureMax.Value + " unsure sightings, a wave checks the spot");
                    RunHome(g, m, sc, home, sc.WatchSpot);
                    CallWave(g, sc.WatchSpot, "scout's repeated sightings");
                    return;
                }
                Say("The scout was not sure of what it saw");
                RunHome(g, m, sc, home, sc.WatchSpot);      // back to its walk after the retreat
                return;
            }

            if (elapsed >= _scoutWatch.Value)
            {
                sc.WatchSince = 0f; sc.Bolted = false; sc.Unsure = 0;
                Say("The scout is sure - a wave is called");
                Logger.LogInfo("scouts: '" + m.name + "' watched you for " + elapsed.ToString("F0") + " s - the wave is called");
                RunHome(g, m, sc, home, at);
                CallWave(g, at, "scout's report");
                return;
            }

            if (!sc.Bolted && (dt < sc.WatchFrom - 3f || dt < 8f))
            {
                // "If the player notices the scout and approaches it, the scout runs away instantly" -
                // the count goes on; it is running to stay alive until the count is done.
                sc.Bolted = true;
                Vector3 back = home - m.transform.position; back.y = 0f;
                m.m_StartPosition = home;
                if (back.sqrMagnitude > 0.01f) m.m_StartForward = back.normalized;
                m.m_MoveStyle = Enums.AIMoveStyle.Run;
                Say("The scout bolts - " + Mathf.CeilToInt(_scoutWatch.Value - elapsed) + " s to its call");
                Logger.LogInfo("scouts: '" + m.name + "' bolts at " + dt.ToString("F0") + " m with " + (_scoutWatch.Value - elapsed).ToString("F0") + " s left on its watch");
                return;
            }
            if (sc.Bolted)
            {
                if (m.m_MoveStyle != Enums.AIMoveStyle.Run) m.m_MoveStyle = Enums.AIMoveStyle.Run;
                if (Vector3.Distance(m.m_StartPosition, home) > 1f) m.m_StartPosition = home;
                return;
            }
            // Crouched and counting: set, not chased - the spot is where it stands.
            if (Vector3.Distance(m.m_StartPosition, m.transform.position) > 1.5f) m.m_StartPosition = m.transform.position;
        }

        // FIRES. His words: "Can we treat campfire like players? But instead of attacking the player,
        // they go to the campfire to reach it and attack the base. Campfire can't be triggering
        // scouts. A scout must randomly see a fire, or the smoke from a fire." "A smoke can be seen
        // with the maximum distance of a native." "A fire is a sure thing, no 15 seconds." And a
        // fire put out before any scout saw it calls nothing. So: on its walk a scout looks, once a
        // second, for a burning Firecamp within its own sight range with a clear line to the fire or
        // to the top of its smoke; found, the wave is asked of the game WITH the fire's FirecampGroup
        // (EnemyAISpawnManager.SpawnWave's third argument - the same thing the game's own camp raids
        // use), so the wave goes for the base. One raid per fire per FireRaidCooldown.
        private static readonly Dictionary<Firecamp, float> s_FireSeenAt = new Dictionary<Firecamp, float>();

        private bool LookForFire(AIs.HumanAIGroup g, AIs.HumanAI m, ScoutState sc, Vector3 home)
        {
            List<Firecamp> fires = Firecamp.s_Firecamps;
            if (fires == null || fires.Count == 0) return false;
            float range = (m.m_Params != null && m.m_Params.m_SightRange > 0f) ? m.m_Params.m_SightRange : 10f;
            Vector3 eye = m.transform.position + Vector3.up * 1.6f;
            for (int i = 0; i < fires.Count; i++)
            {
                Firecamp f = fires[i];
                if (f == null || !f.m_Burning || f.IsSceneObject()) continue;
                float seenAt;
                if (s_FireSeenAt.TryGetValue(f, out seenAt) && Time.time - seenAt < 600f) continue;
                Vector3 fp = f.transform.position;
                if (Vector3.Distance(eye, fp) > range) continue;
                bool fire = !Physics.Linecast(eye, fp + Vector3.up * 0.6f, ~0, QueryTriggerInteraction.Ignore);
                bool smoke = !fire && !Physics.Linecast(eye, fp + Vector3.up * 6f, ~0, QueryTriggerInteraction.Ignore);
                if (!fire && !smoke) continue;
                s_FireSeenAt[f] = Time.time;
                Say("A scout saw your " + (fire ? "fire" : "smoke") + " - they are coming for the camp");
                Logger.LogInfo("scouts: '" + m.name + "' saw " + (fire ? "the fire" : "the smoke") + " of '" + f.name + "' at "
                    + Vector3.Distance(eye, fp).ToString("F0") + " m - a raid is sent to it");
                RunHome(g, m, sc, home, fp);
                FireWave(g, f);
                return true;
            }
            return false;
        }

        private void FireWave(AIs.HumanAIGroup g, Firecamp f)
        {
            try
            {
                AIs.EnemyAISpawnManager mgr = AIs.EnemyAISpawnManager.Get();
                if (mgr == null) return;
                DifficultySettingsPreset pre = DifficultySettings.ActivePreset;
                if (pre != null && !pre.m_Tribes) return;
                FirecampGroup fg = null;
                try
                {
                    FirecampGroupsManager fgm = FirecampGroupsManager.Get();
                    List<FirecampGroup> groups = (fgm != null) ? Traverse.Create(fgm).Field("m_FirecampGroups").GetValue<List<FirecampGroup>>() : null;
                    if (groups != null) for (int i = 0; i < groups.Count; i++) if (groups[i] != null && groups[i].Contains(f)) { fg = groups[i]; break; }
                }
                catch (Exception) { fg = null; }
                int count = NumbersOn() ? Roll() : Mathf.Max(_membersMin.Value, 1);
                AIs.HumanAIWave wave = mgr.SpawnWave(count, false, fg);
                if (wave == null) { Logger.LogInfo("scouts: the game declined the raid just now"); return; }
                if (g != null) s_LastScoutWave[g] = Time.time;
                Logger.LogInfo("scouts: raid of " + wave.m_Count + " sent " + (fg != null ? "to the fire's camp group" : "to you (the fire is in no camp group)"));
            }
            catch (Exception ex) { HuntLog("fire raid failed: " + ex.Message); }
        }

        // SILENCE. "Scout moving around, always in silence." Every voice a native has goes through
        // HumanAISoundModule.PlaySound; a scout's is refused.
        [HarmonyPatch(typeof(AIs.HumanAISoundModule), "PlaySound", new Type[] { typeof(AIs.HumanAISoundModule.SoundType) })]
        private static class Patch_ScoutSilentType
        {
            private static bool Prefix(AIs.HumanAISoundModule __instance, ref AudioClip __result)
            {
                return !ScoutMuted(__instance, ref __result);
            }
        }

        [HarmonyPatch(typeof(AIs.HumanAISoundModule), "PlaySound", new Type[] { typeof(AudioClip) })]
        private static class Patch_ScoutSilentClip
        {
            private static bool Prefix(AIs.HumanAISoundModule __instance, ref AudioClip __result)
            {
                return !ScoutMuted(__instance, ref __result);
            }
        }

        private static bool ScoutMuted(AIs.HumanAISoundModule module, ref AudioClip result)
        {
            try
            {
                if (s_Self == null || !s_Self._scoutsEnabled.Value || !s_Self._scoutSilent.Value || s_Scouts.Count == 0) return false;
                AIs.HumanAI h = Traverse.Create(module).Field("m_HumanAI").GetValue<AIs.HumanAI>();
                if (h == null || !IsScout(h)) return false;
                result = null;
                return true;
            }
            catch (Exception) { return false; }
        }

        // THE SCOUT SEES FIRST. The log of the session where he "never met a scout - the ones that
        // see me just attack": three scouts assigned, none ever "found you". The race: the scout's
        // own SightModule hands it the player as enemy, HumanAIGroup.UpdateState asks
        // ShouldSetAttackState in that same frame, and the whole camp is in Attack before the
        // scout's walk (a postfix, Calm only) gets a turn. So the scout's sighting is taken here,
        // in a prefix on that very question: a calm camp's scout holding the player as enemy has
        // found you - it turns for home and its enemy is cleared, so the camp does not attack on
        // its report (unless ScoutAlarmsCamp says it should). A grouped native changes state only
        // through its group (HumanAI.UpdateMe, IL), so this is the one door.
        [HarmonyPatch(typeof(AIs.HumanAIGroup), "ShouldSetAttackState")]
        private static class Patch_ScoutSeesFirst
        {
            private static void Prefix(AIs.HumanAIGroup __instance)
            {
                if (s_Self == null || !s_Self._scoutsEnabled.Value || s_Self._scoutAlarmsCamp.Value) return;
                try
                {
                    if (!Ours(__instance) || !__instance.m_Active || __instance.m_Members == null) return;
                    if (__instance.m_State != AIs.HumanAIGroup.State.Calm) return;
                    Being target = null; bool looked = false;
                    for (int i = 0; i < __instance.m_Members.Count; i++)
                    {
                        AIs.HumanAI m = __instance.m_Members[i];
                        if (m == null || m.m_EnemyModule == null || m.m_EnemyModule.m_Enemy == null) continue;
                        ScoutState sc;
                        if (!s_Scouts.TryGetValue(m, out sc)) continue;
                        if (!looked) { looked = true; target = HuntTarget(); }
                        Being e = m.m_EnemyModule.m_Enemy;
                        bool player = (target != null && e == target) || (e.gameObject != null && (GameObjectExtension.IsPlayer(e.gameObject) || e.GetComponentInParent<Player>() != null));
                        if (!player) continue;
                        float now = Time.time;
                        if (sc.WatchSince <= 0f && now >= sc.RetreatUntil && !HighGroundHides(m, e))
                        {
                            RoamState st; Vector3 home = m.m_StartPosition;
                            if (s_Self._roam.TryGetValue(m, out st)) home = st.Home;
                            float dt = Vector3.Distance(m.transform.position, e.transform.position);
                            bool sees = m.m_SightModule != null && m.m_SightModule.m_VisibleBeings != null && m.m_SightModule.m_VisibleBeings.Contains(e);
                            s_Self.ScoutSighting(__instance, m, sc, home, e, dt, sees);
                        }
                        m.m_EnemyModule.SetEnemy(null);
                        m.m_EnemyModule.m_PriorityEnemy = null;
                    }
                }
                catch (Exception ex) { s_Self.HuntLog("scout sighting failed: " + ex.Message); }
            }
        }

        // A DEAD SCOUT CALLS NOTHING. His rules: "If a scout has been killed by the player without
        // seeing the player, that scout can't call a wave", and the watch is "trying to get the timer
        // to reach 15 sec before a player kills it." The timer is the only caller; a death only says
        // in the log where the count stood.
        [HarmonyPatch(typeof(AIs.HumanAI), "OnDie")]
        private static class Patch_ScoutDies
        {
            private static void Postfix(AIs.HumanAI __instance)
            {
                if (s_Self == null) return;
                try
                {
                    ScoutState sc;
                    if (!s_Scouts.TryGetValue(__instance, out sc)) return;
                    s_Scouts.Remove(__instance);
                    string how = sc.WatchSince > 0f
                        ? "during its watch, " + (Time.time - sc.WatchSince).ToString("F0") + " s in" + (sc.Bolted ? ", on the run" : ", crouched")
                        : (sc.FoundAt > 0f ? "after a sighting" : "without ever seeing you");
                    s_Self.Logger.LogInfo("scouts: '" + __instance.name + "' was killed " + how + " - no wave from a dead scout");
                    if (sc.WatchSince > 0f) s_Self.Say("The scout died before its call");
                }
                catch (Exception ex) { s_Self.HuntLog("scout death failed: " + ex.Message); }
            }
        }

        private void ScoutWave(AIs.HumanAIGroup g)
        {
            try
            {
                float last;
                if (g != null && s_LastScoutWave.TryGetValue(g, out last) && Time.time - last < _scoutWaveCooldown.Value)
                {
                    Logger.LogInfo("scouts: wave not called - '" + g.name + "' called one " + Mathf.RoundToInt(Time.time - last) + " s ago");
                    return;
                }
                AIs.EnemyAISpawnManager mgr = AIs.EnemyAISpawnManager.Get();
                if (mgr == null) return;
                DifficultySettingsPreset pre = DifficultySettings.ActivePreset;
                if (pre != null && !pre.m_Tribes) return;
                int count = NumbersOn() ? Roll() : Mathf.Max(_membersMin.Value, 1);
                AIs.HumanAIWave wave = mgr.SpawnWave(count, false, null);
                if (wave == null) { Logger.LogInfo("scouts: the game declined the wave just now"); return; }
                if (g != null) s_LastScoutWave[g] = Time.time;
                Say("The scout's wave is coming - " + wave.m_Count + " native(s)");
            }
            catch (Exception ex) { HuntLog("scout wave failed: " + ex.Message); }
        }

        /// <summary>Dead scouts leave the table; a camp that lost its scout picks a new one next tick.</summary>
        private void SweepScouts()
        {
            List<AIs.HumanAI> gone = new List<AIs.HumanAI>();
            foreach (AIs.HumanAI k in s_Scouts.Keys) if (k == null) gone.Add(k);
            for (int i = 0; i < gone.Count; i++) s_Scouts.Remove(gone[i]);
        }
    }
}
