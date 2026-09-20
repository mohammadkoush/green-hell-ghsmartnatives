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
            if (camp == null) return false;

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
            sc.Errand = trap; sc.ErrandAt = at; sc.ErrandSince = Time.time; sc.ErrandRun = neighbour;
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

            // Retreating: leave it to the rest goal, which is walking it home.
            if (now < sc.RetreatUntil) return true;

            // On an errand to a trap: walk there (the rest goal does it), reset it, then scout on.
            // Its eyes stay open on the way - the sighting check below runs first.
            if (sc.Errand != null)
            {
                if (sc.Errand == null || now - sc.ErrandSince > 180f) sc.Errand = null;
                else if (Vector3.Distance(here, sc.ErrandAt) < 2.5f) { FinishErrand(m, sc); return true; }
            }

            // Found you? Its own eyes, or it walked into you.
            if (target != null)
            {
                bool sees = m.m_SightModule != null && m.m_SightModule.m_VisibleBeings != null && m.m_SightModule.m_VisibleBeings.Contains(target);
                float dt = Vector3.Distance(here, target.transform.position);
                if (sees || dt < 6f)
                {
                    sc.FoundAt = now;
                    sc.Errand = null;
                    sc.RetreatUntil = now + _scoutRetreat.Value;
                    m.m_StartPosition = st.Home;
                    m.m_StartForward = (st.Home - here).normalized;
                    m.m_MoveStyle = Enums.AIMoveStyle.Run;
                    RememberSeen(g, target.transform.position);
                    Say("A scout spotted you at " + Mathf.RoundToInt(dt) + " m - it backs off");
                    Logger.LogInfo("scouts: '" + m.name + "' found you at " + dt.ToString("F0") + " m (" + (sees ? "saw you" : "walked into you") + ") - running home");
                    if (_scoutAlarmsCamp.Value) Alarm(g, target.transform.position, "scout's report", true);
                    if (_scoutWave.Value) ScoutWave(g);
                    return true;
                }
            }

            if (sc.Errand != null)
            {
                // Keep the trap as the destination; the rest goal re-paths on its own. A neighbour's
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
            return true;
        }

        private void ScoutWave(AIs.HumanAIGroup g)
        {
            try
            {
                float last;
                if (s_LastScoutWave.TryGetValue(g, out last) && Time.time - last < _scoutWaveCooldown.Value)
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
                s_LastScoutWave[g] = Time.time;
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
