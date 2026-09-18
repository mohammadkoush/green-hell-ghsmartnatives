// GHSmartNatives - hunt and roam.
//
// HUNT. The game's camp natives attack when one of them SEES the player (SightModule), hears them
// (HearingModule) or has them inside m_EnemySenseRange - a few metres. Then the whole group goes to
// Attack and every member paths to the player with GoalHumanMoveToEnemy. That last part is exactly
// what is wanted and it is not touched. What changes is the trigger: a camp that has the player
// within HuntRadius of any member gets the player as every member's enemy, which is the condition
// ShouldSetAttackState already checks, so the game's own state machine takes it from there.
//
// While in Attack, ShouldSetCalmState says "calm down" when nobody sees, senses or holds an enemy.
// The postfix on it says no while the player is still within KeepHuntingRadius, and re-arms any
// member who let go. Beyond that radius the game calms them as it always did.
//
// Two per-native numbers are raised so the hunt is not undone by the game's own leash:
//   AI.m_DistanceToLoseEnemy     - CanSetEnemy refuses an enemy farther than this (non-attack states)
//                                  and UpdateState only shares an enemy inside it
//   EnemyModule.m_TimeToLooseEnemy - seconds without a sighting before the enemy is dropped
// Set, not chased: the original is remembered per native and put back when the setting is turned
// off or the plugin unloads.
//
// ROAM. GoalHumanRest.UpdateAction (read from the IL): if the native is farther than
// m_SamplePosRange from HumanAI.m_StartPosition, it stops crouching, calculates a path to
// m_StartPosition and walks there, then idles and faces m_StartForward. So moving m_StartPosition
// is the whole of walking, with the game's own animation and pathing. The point is sampled onto
// the NavMesh so nobody is sent into a rock.
//
// His rule, verbatim: "Pathfinding is really important. They need to walk around looking for the
// player, finding him. Don't make them stop and sit down; they go and attack him instantly." So:
//   - a calm native is ALWAYS walking: the moment it reaches its spot it is given the next one
//     (a timer is only the unstick, for a spot the path never reaches)
//   - with the player inside SearchRadius the next spot is a step TOWARD the player, with a little
//     sideways jitter so a camp fans out instead of walking in a file
//   - otherwise the next spot is a random one around the camp, inside RoamRadius of home
//   - the instant any member is within NoticeRadius the hunt patch above fires - no sitting first
// The camp's original spot is remembered per native and put back when roam is turned off.
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
        // ---------- config: Hunt ----------
        private ConfigEntry<bool>  _huntEnabled;
        private ConfigEntry<float> _huntRadius;
        private ConfigEntry<float> _keepRadius;
        private ConfigEntry<float> _giveUpSecs;

        // ---------- config: Roam ----------
        private ConfigEntry<bool>  _roamEnabled;
        private ConfigEntry<float> _roamRadius;
        private ConfigEntry<float> _roamEveryMin;
        private ConfigEntry<float> _roamEveryMax;
        private ConfigEntry<float> _searchRadius;
        private ConfigEntry<float> _stepMetres;

        private void BindHuntConfig()
        {
            _huntEnabled = Config.Bind("Hunt", "Enabled", true,
                "A camp that has you within reach notices you and comes for you, instead of waiting " +
                "for you to walk into it.");
            _huntRadius = Config.Bind("Hunt", "NoticeRadiusMetres", 45f,
                new ConfigDescription("How close you can get to any member of a calm camp before " +
                    "they notice you and attack.", new AcceptableValueRange<float>(5f, 200f)));
            _keepRadius = Config.Bind("Hunt", "KeepHuntingRadiusMetres", 80f,
                new ConfigDescription("While you are within this distance of any member, a hunting " +
                    "group does not calm down and anyone who lost you is pointed at you again.",
                    new AcceptableValueRange<float>(10f, 300f)));
            _giveUpSecs = Config.Bind("Hunt", "GiveUpAfterSeconds", 60f,
                new ConfigDescription("Seconds a native keeps hunting after losing sight of you " +
                    "(the game's own EnemyModule.m_TimeToLooseEnemy, raised to this).",
                    new AcceptableValueRange<float>(5f, 600f)));

            _huntEnabled.SettingChanged += delegate { if (!_huntEnabled.Value) RestoreSenses(); };
        }

        private void BindRoamConfig()
        {
            _roamEnabled = Config.Bind("Roam", "Enabled", true,
                "Natives in a calm camp never sit: they walk, and when you are within SearchRadius " +
                "they walk toward you, looking for you.");
            _roamRadius = Config.Bind("Roam", "RadiusMetres", 12f,
                new ConfigDescription("With nobody to look for, how far from its own spot a native wanders.",
                    new AcceptableValueRange<float>(3f, 40f)));
            _searchRadius = Config.Bind("Roam", "SearchRadiusMetres", 150f,
                new ConfigDescription("With you inside this distance of a calm native, its next spot is a " +
                    "step toward you.", new AcceptableValueRange<float>(20f, 500f)));
            _stepMetres = Config.Bind("Roam", "StepMetres", 12f,
                new ConfigDescription("How long each searching step is.", new AcceptableValueRange<float>(4f, 40f)));
            _roamEveryMin = Config.Bind("Roam", "UnstickSecondsMin", 8f,
                new ConfigDescription("A native that has not reached its spot in this long gets a new one " +
                    "(no sooner than this)...", new AcceptableValueRange<float>(3f, 120f)));
            _roamEveryMax = Config.Bind("Roam", "UnstickSecondsMax", 20f,
                new ConfigDescription("...and no later than this.",
                    new AcceptableValueRange<float>(5f, 240f)));

            _roamEnabled.SettingChanged += delegate { if (!_roamEnabled.Value) RestoreRoam(); };
        }

        // -----------------------------------------------------------------------------------------
        // Notice: the group's own attack check, made to say yes when you are in reach
        // -----------------------------------------------------------------------------------------

        private static readonly Dictionary<AIs.HumanAIGroup, float> s_NoticedAt = new Dictionary<AIs.HumanAIGroup, float>();

        [HarmonyPatch(typeof(AIs.HumanAIGroup), "ShouldSetAttackState")]
        private static class Patch_Notice
        {
            private static void Postfix(AIs.HumanAIGroup __instance, ref bool __result)
            {
                if (__result) return;
                try
                {
                    if (s_Self == null || !s_Self._huntEnabled.Value) return;
                    if (!Ours(__instance) || !__instance.m_Active) return;
                    if (__instance.m_State == AIs.HumanAIGroup.State.Attack) return;
                    if (__instance.m_Members == null || __instance.m_Members.Count == 0) return;

                    Player p = HuntablePlayer();
                    if (p == null) return;
                    float d = ClosestMember(__instance, p.transform.position);
                    if (d > s_Self._huntRadius.Value) return;

                    int armed = 0;
                    for (int i = 0; i < __instance.m_Members.Count; i++)
                    {
                        AIs.HumanAI m = __instance.m_Members[i];
                        if (m == null || m.m_EnemyModule == null) continue;
                        m.m_EnemyModule.SetEnemy(p);
                        armed++;
                    }
                    if (armed == 0) return;

                    __result = true;
                    s_NoticedAt[__instance] = Time.time;
                    s_Self.Say("Natives noticed you - " + armed + " coming from " + Mathf.RoundToInt(d) + " m");
                    s_Self.Logger.LogInfo("hunt: group '" + __instance.name + "' (" + armed + ") noticed you at "
                        + d.ToString("F0") + " m - attack state");
                }
                catch (Exception ex) { s_Self.HuntLog("notice failed: " + ex.Message); }
            }
        }

        [HarmonyPatch(typeof(AIs.HumanAIGroup), "ShouldSetCalmState")]
        private static class Patch_KeepHunting
        {
            private static void Postfix(AIs.HumanAIGroup __instance, ref bool __result)
            {
                if (!__result) return;
                try
                {
                    if (s_Self == null || !s_Self._huntEnabled.Value) return;
                    if (!Ours(__instance) || !__instance.m_Active) return;
                    if (__instance.m_State != AIs.HumanAIGroup.State.Attack) return;
                    if (__instance.m_Members == null) return;

                    Player p = HuntablePlayer();
                    if (p == null) return;
                    float d = ClosestMember(__instance, p.transform.position);
                    if (d > s_Self._keepRadius.Value)
                    {
                        // The game calms them; say so once per hunt, because "they just stopped" is
                        // the kind of thing that otherwise reads as a bug.
                        if (s_NoticedAt.ContainsKey(__instance))
                        {
                            s_NoticedAt.Remove(__instance);
                            s_Self.Say("Natives gave up - you are " + Mathf.RoundToInt(d) + " m out");
                        }
                        return;
                    }

                    for (int i = 0; i < __instance.m_Members.Count; i++)
                    {
                        AIs.HumanAI m = __instance.m_Members[i];
                        if (m == null || m.m_EnemyModule == null) continue;
                        if (m.m_EnemyModule.m_Enemy == null) m.m_EnemyModule.SetEnemy(p);
                    }
                    __result = false;
                }
                catch (Exception ex) { s_Self.HuntLog("keep-hunting failed: " + ex.Message); }
            }
        }

        // -----------------------------------------------------------------------------------------
        // Per-native leash: set once per native, put back on request
        // -----------------------------------------------------------------------------------------

        private class SenseOriginal { public float DistToLose; public float TimeToLose; public bool HasTime; }
        private readonly Dictionary<AIs.HumanAI, SenseOriginal> _senseOrig = new Dictionary<AIs.HumanAI, SenseOriginal>();

        private void ApplySensesTo(AIs.HumanAIGroup g)
        {
            if (!_huntEnabled.Value || g.m_Members == null) return;
            float wantDist = _keepRadius.Value;
            float wantTime = _giveUpSecs.Value;
            for (int i = 0; i < g.m_Members.Count; i++)
            {
                AIs.HumanAI m = g.m_Members[i];
                if (m == null) continue;
                SenseOriginal o;
                if (!_senseOrig.TryGetValue(m, out o))
                {
                    o = new SenseOriginal();
                    o.DistToLose = m.m_DistanceToLoseEnemy;
                    o.HasTime = (m.m_EnemyModule != null);
                    if (o.HasTime) o.TimeToLose = m.m_EnemyModule.m_TimeToLooseEnemy;
                    _senseOrig[m] = o;
                    HuntLog("native '" + m.name + "': distance to lose enemy " + o.DistToLose.ToString("F0")
                        + " -> " + Mathf.Max(o.DistToLose, wantDist).ToString("F0") + " m, time to lose "
                        + (o.HasTime ? o.TimeToLose.ToString("F0") : "?") + " -> " + Mathf.Max(o.TimeToLose, wantTime).ToString("F0") + " s");
                }
                // Set, not chased: only when the value is below what is wanted (the game may reset it
                // on spawn; a compare per frame costs nothing and a write per frame would fight it).
                float d = Mathf.Max(o.DistToLose, wantDist);
                if (m.m_DistanceToLoseEnemy < d) m.m_DistanceToLoseEnemy = d;
                if (m.m_EnemyModule != null)
                {
                    float t = Mathf.Max(o.TimeToLose, wantTime);
                    if (m.m_EnemyModule.m_TimeToLooseEnemy < t) m.m_EnemyModule.m_TimeToLooseEnemy = t;
                }
            }
        }

        private void RestoreSenses()
        {
            foreach (KeyValuePair<AIs.HumanAI, SenseOriginal> kv in _senseOrig)
            {
                try
                {
                    if (kv.Key == null) continue;
                    kv.Key.m_DistanceToLoseEnemy = kv.Value.DistToLose;
                    if (kv.Value.HasTime && kv.Key.m_EnemyModule != null) kv.Key.m_EnemyModule.m_TimeToLooseEnemy = kv.Value.TimeToLose;
                }
                catch (Exception) { }
            }
            _senseOrig.Clear();
        }

        // -----------------------------------------------------------------------------------------
        // Roam
        // -----------------------------------------------------------------------------------------

        private class RoamState { public Vector3 Home; public Vector3 Forward; public float NextAt; public int Walks; }
        private readonly Dictionary<AIs.HumanAI, RoamState> _roam = new Dictionary<AIs.HumanAI, RoamState>();
        private float _roamSweepAt;

        private void RoamTick(AIs.HumanAIGroup g)
        {
            if (!_roamEnabled.Value) return;
            if (g.m_State != AIs.HumanAIGroup.State.Calm) return;
            if (g.IsPatrol()) return;                    // patrols have a path; leave it to them
            if (g.m_Members == null) return;

            float now = Time.time;
            Player p = HuntablePlayer();
            for (int i = 0; i < g.m_Members.Count; i++)
            {
                AIs.HumanAI m = g.m_Members[i];
                if (m == null) continue;
                if (m.GetState() != AIs.HumanAI.State.Rest) continue;

                RoamState st;
                if (!_roam.TryGetValue(m, out st))
                {
                    st = new RoamState();
                    st.Home = m.m_StartPosition;
                    st.Forward = m.m_StartForward;
                    st.NextAt = now;                      // the first spot comes at once - no sitting
                    _roam[m] = st;
                }

                Vector3 here = m.transform.position;
                bool arrived = Vector3.Distance(here, m.m_StartPosition) < 3f;
                if (!arrived && now < st.NextAt) continue;            // still walking, not overdue
                st.NextAt = now + UnityEngine.Random.Range(_roamEveryMin.Value, Mathf.Max(_roamEveryMin.Value, _roamEveryMax.Value));

                Vector3 want;
                bool searching = false;
                float dp = (p != null) ? Vector3.Distance(here, p.transform.position) : float.MaxValue;
                if (p != null && dp <= _searchRadius.Value)
                {
                    // A step toward the player, turned a little to one side so the camp fans out.
                    Vector3 dir = p.transform.position - here; dir.y = 0f;
                    if (dir.sqrMagnitude < 0.01f) dir = Vector3.forward; else dir.Normalize();
                    dir = Quaternion.Euler(0f, UnityEngine.Random.Range(-35f, 35f), 0f) * dir;
                    want = here + dir * Mathf.Min(_stepMetres.Value, dp);
                    searching = true;
                }
                else
                {
                    Vector2 r = UnityEngine.Random.insideUnitCircle * _roamRadius.Value;
                    want = st.Home + new Vector3(r.x, 0f, r.y);
                    // Closer than this and the rest goal does not bother to get up; try the far side.
                    if (Vector3.Distance(want, here) < 4f) want = st.Home - new Vector3(r.x, 0f, r.y);
                }

                NavMeshHit hit;
                if (!NavMesh.SamplePosition(want, out hit, 4f, NavMesh.AllAreas))
                {
                    if (s_RoamLogged < 6) { s_RoamLogged++; Logger.LogInfo("roam: no walkable ground near the spot for '" + m.name + "' - next one on the unstick timer"); }
                    continue;
                }

                Vector3 fwd = hit.position - here; fwd.y = 0f;
                m.m_StartPosition = hit.position;
                if (fwd.sqrMagnitude > 0.01f) m.m_StartForward = fwd.normalized;
                st.Walks++;
                if (s_RoamLogged < 6)
                {
                    s_RoamLogged++;
                    Logger.LogInfo("roam: '" + m.name + "' " + (searching ? "searches - a step toward you (" + Mathf.RoundToInt(dp) + " m away)" : "wanders " + Vector3.Distance(hit.position, st.Home).ToString("F0") + " m from home")
                        + (s_RoamLogged == 6 ? " - further roam lines suppressed" : ""));
                }
            }

            // Dead natives leave the table, every 30 s. A destroyed key compares equal to null.
            if (now - _roamSweepAt > 30f)
            {
                _roamSweepAt = now;
                List<AIs.HumanAI> gone = new List<AIs.HumanAI>();
                foreach (AIs.HumanAI k in _roam.Keys) if (k == null) gone.Add(k);
                foreach (AIs.HumanAI k in _senseOrig.Keys) if (k == null && !gone.Contains(k)) gone.Add(k);
                for (int i = 0; i < gone.Count; i++) { _roam.Remove(gone[i]); _senseOrig.Remove(gone[i]); }
            }
        }
        private static int s_RoamLogged;

        private void RestoreRoam()
        {
            foreach (KeyValuePair<AIs.HumanAI, RoamState> kv in _roam)
            {
                try
                {
                    if (kv.Key == null) continue;
                    kv.Key.m_StartPosition = kv.Value.Home;
                    kv.Key.m_StartForward = kv.Value.Forward;
                }
                catch (Exception) { }
            }
            _roam.Clear();
        }

        // -----------------------------------------------------------------------------------------
        // The group tick: after the game's own state update, every frame the group is active
        // -----------------------------------------------------------------------------------------

        [HarmonyPatch(typeof(AIs.HumanAIGroup), "UpdateState")]
        private static class Patch_GroupTick
        {
            private static void Postfix(AIs.HumanAIGroup __instance)
            {
                if (s_Self == null) return;
                try
                {
                    if (!Ours(__instance) || !__instance.m_Active) return;
                    s_Self.ApplySensesTo(__instance);
                    s_Self.RoamTick(__instance);
                }
                catch (Exception ex) { s_Self.HuntLog("group tick failed: " + ex.Message); }
            }
        }

        private const int HuntLogCap = 24;
        private static int s_HuntLogged;
        internal void HuntLog(string msg)
        {
            if (s_HuntLogged >= HuntLogCap) return;
            s_HuntLogged++;
            Logger.LogInfo("hunt: " + msg + (s_HuntLogged == HuntLogCap ? "  (further hunt lines suppressed this session)" : ""));
        }

        /// <summary>The live line at the top of the panel: what the groups are doing right now.</summary>
        private string StatusText()
        {
            try
            {
                DifficultySettingsPreset pre = DifficultySettings.ActivePreset;
                if (pre != null && !pre.m_Tribes)
                    return "Tribes are OFF in your difficulty preset - no natives can spawn, whatever is set here.";

                int active = 0, hunting = 0, roaming = 0;
                float nearest = float.MaxValue;
                Player p = Player.Get();
                if (AIs.HumanAIGroup.s_AIGroups != null)
                {
                    for (int i = 0; i < AIs.HumanAIGroup.s_AIGroups.Count; i++)
                    {
                        AIs.HumanAIGroup g = AIs.HumanAIGroup.s_AIGroups[i];
                        if (g == null || !g.m_Active || g.m_Members == null || g.m_Members.Count == 0) continue;
                        active++;
                        if (g.m_State == AIs.HumanAIGroup.State.Attack) hunting++;
                        else if (g.m_State == AIs.HumanAIGroup.State.Calm) roaming++;
                        if (p != null) nearest = Mathf.Min(nearest, ClosestMember(g, p.transform.position));
                    }
                }
                string s = active + " group(s) awake near you";
                if (active > 0) s += ": " + hunting + " hunting, " + roaming + " calm";
                if (nearest < float.MaxValue) s += ", nearest native " + Mathf.RoundToInt(nearest) + " m";
                AIs.EnemyAISpawnManager mgr = AIs.EnemyAISpawnManager.Get();
                if (mgr != null)
                {
                    s += "\nNext group " + Mmss(mgr.m_TimeToNextSpawnGroup) + ", next wave " + Mmss(mgr.m_TimeToNextSpawnWave);
                    if (mgr.m_ActiveGroup != null) s += " (group timer frozen while one is active)";
                }
                return s;
            }
            catch (Exception ex) { return "status unavailable: " + ex.Message; }
        }

        private static string Mmss(float secs)
        {
            if (secs <= 0f) return "due now";
            int s = Mathf.RoundToInt(secs);
            return (s / 60) + "m " + (s % 60).ToString("00") + "s";
        }
    }
}
