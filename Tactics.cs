// GHSmartNatives - tactics: archers keep their distance, the boss waits for the surround.
//
// His words: "Archers stay back at a distance while foot soldiers march forward. Boss gets into
// the fight when you are surrounded."
//
// HOW THE GAME PICKS WHAT A NATIVE DOES (from the IL): GoalsModule.OnUpdate deactivates the active
// goal when its ShouldPerform() turns false and calls SetupActiveGoal to choose another. So the
// cleanest lever is a postfix on ShouldPerform of the two goals that close the distance:
//   GoalHumanMoveToEnemy.ShouldPerform  true while farther than m_AttackRange  -> we say NO for a
//                                        holder closer than its keep distance
//   GoalHumanAttack.ShouldPerform        -> we say NO for a holder unless the enemy is on top of it
// and, to actively back off, the game's own GoalHumanMoveAwayFromEnemy: it keeps performing while
// the enemy is inside its m_MinDistToEnemy, so that field is set to the keep distance and the goal
// activated when the enemy comes closer than that. The Hunter's script already carries that goal
// (the only human script that does: "HumanMoveAwayFromEnemy", "HumanThrowerMoveToEnemy" beside
// "HumanThrowStone"), which is why hunters are the archers here: the game built them to throw
// from 6-15 m; this makes them hold that line instead of drifting into the melee.
//
// The Thug (AIID 36) holds the same way until the group has the player surrounded: a configurable
// number of other members within 6 m of the player, or a member lost, or a maximum wait - then it
// is released for the rest of that attack and comes in.
//
// Language level is C# 5 (stock Framework csc.exe) - no ?., no $"", no ??=.

using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace GHSmartNatives
{
    public partial class GHSmartNativesPlugin
    {
        private ConfigEntry<bool>  _tacticsEnabled;
        private ConfigEntry<float> _archerKeep;
        private ConfigEntry<bool>  _bossHolds;
        private ConfigEntry<float> _bossKeep;
        private ConfigEntry<int>   _surroundedCount;
        private ConfigEntry<float> _bossWaitMax;

        private void BindTacticsConfig()
        {
            _tacticsEnabled = Config.Bind("Tactics", "Enabled", true,
                "Archers (hunters) keep their distance while the others close in; the Thug waits " +
                "until you are surrounded.");
            _archerKeep = Config.Bind("Tactics", "ArcherKeepMetres", 10f,
                new ConfigDescription("A hunter backs off when you come closer than this and never " +
                    "walks in past it. The game's hunters throw from 6 to 15 m.",
                    new AcceptableValueRange<float>(4f, 25f)));
            _bossHolds = Config.Bind("Tactics", "BossWaitsForSurround", true,
                "The Thug hangs back until the others have you surrounded.");
            _bossKeep = Config.Bind("Tactics", "BossKeepMetres", 14f,
                new ConfigDescription("How far the Thug stays while waiting.", new AcceptableValueRange<float>(5f, 30f)));
            _surroundedCount = Config.Bind("Tactics", "SurroundedCount", 2,
                new ConfigDescription("This many other natives within 6 m of you releases the Thug.",
                    new AcceptableValueRange<int>(1, 5)));
            _bossWaitMax = Config.Bind("Tactics", "BossWaitMaxSeconds", 45f,
                new ConfigDescription("The Thug comes in anyway after this long, or as soon as the group " +
                    "loses a member.", new AcceptableValueRange<float>(5f, 180f)));
        }

        // A plain FieldInfo: the ref-returning FieldRef delegate is beyond C# 5's csc. A few dozen
        // reflected reads a frame, only for humans in Attack, is nothing.
        private static readonly System.Reflection.FieldInfo s_GoalAIField = AccessTools.Field(typeof(AIs.AIGoal), "m_AI");
        private static AIs.AI s_GoalAI(AIs.AIGoal g) { return (s_GoalAIField != null) ? s_GoalAIField.GetValue(g) as AIs.AI : null; }

        private class AttackMemo { public float StartedAt; public int MembersAtStart; public bool BossReleased; }
        private static readonly Dictionary<AIs.HumanAIGroup, AttackMemo> s_Attack = new Dictionary<AIs.HumanAIGroup, AttackMemo>();

        private static bool IsArcher(AIs.AI ai) { return ai is AIs.HunterAI; }
        private static bool IsBoss(AIs.AI ai)   { return ai != null && ai.m_ID == AIs.AI.AIID.Thug; }

        /// <summary>Is this native one that keeps its distance right now, and at what distance.</summary>
        private static bool Holds(AIs.AI ai, out float keep)
        {
            keep = 0f;
            if (s_Self == null || !s_Self._tacticsEnabled.Value) return false;
            AIs.HumanAI h = ai as AIs.HumanAI;
            if (h == null || h.m_Group == null || !Ours(h.m_Group)) return false;
            if (IsArcher(ai)) { keep = s_Self._archerKeep.Value; return true; }
            if (IsBoss(ai) && s_Self._bossHolds.Value)
            {
                AttackMemo m;
                if (!s_Attack.TryGetValue(h.m_Group, out m) || m.BossReleased) return false;
                keep = s_Self._bossKeep.Value;
                return true;
            }
            return false;
        }

        [HarmonyPatch(typeof(AIs.GoalHumanMoveToEnemy), "ShouldPerform")]
        private static class Patch_HoldTheLine
        {
            private static void Postfix(AIs.AIGoal __instance, ref bool __result)
            {
                if (!__result) return;
                try
                {
                    AIs.AI ai = s_GoalAI(__instance);
                    if (ai == null || ai.m_EnemyModule == null || ai.m_EnemyModule.m_Enemy == null) return;
                    float keep;
                    if (!Holds(ai, out keep)) return;
                    float d = Vector3.Distance(ai.transform.position, ai.m_EnemyModule.m_Enemy.transform.position);
                    if (d < keep) __result = false;
                }
                catch (Exception ex) { s_Self.HuntLog("hold-the-line failed: " + ex.Message); }
            }
        }

        [HarmonyPatch(typeof(AIs.GoalHumanAttack), "ShouldPerform")]
        private static class Patch_NoMeleeFromHolders
        {
            private static void Postfix(AIs.AIGoal __instance, ref bool __result)
            {
                if (!__result) return;
                try
                {
                    AIs.AI ai = s_GoalAI(__instance);
                    if (ai == null || ai.m_EnemyModule == null || ai.m_EnemyModule.m_Enemy == null) return;
                    float keep;
                    if (!Holds(ai, out keep)) return;
                    // Self-defence stays: an enemy on top of a holder gets hit.
                    float d = Vector3.Distance(ai.transform.position, ai.m_EnemyModule.m_Enemy.transform.position);
                    if (d > 3f) __result = false;
                }
                catch (Exception ex) { s_Self.HuntLog("no-melee failed: " + ex.Message); }
            }
        }

        // The back-off goal per native, found once. Null when the script has none (then the
        // ShouldPerform refusals above still hold it in place; it just does not step back).
        private readonly Dictionary<AIs.AI, AIs.AIGoal> _backOffGoal = new Dictionary<AIs.AI, AIs.AIGoal>();

        private AIs.AIGoal BackOffGoal(AIs.AI ai)
        {
            AIs.AIGoal g;
            if (_backOffGoal.TryGetValue(ai, out g)) return g;
            g = null;
            try
            {
                if (ai.m_GoalsModule != null)
                {
                    List<AIs.AIGoal> goals = Traverse.Create(ai.m_GoalsModule).Field("m_Goals").GetValue<List<AIs.AIGoal>>();
                    if (goals != null)
                        for (int i = 0; i < goals.Count; i++)
                            if (goals[i] != null && goals[i].m_Type == AIs.AIGoalType.HumanMoveAwayFromEnemy) { g = goals[i]; break; }
                }
                HuntLog("'" + ai.name + "' " + (g != null ? "has" : "has NO") + " back-off goal (HumanMoveAwayFromEnemy)");
            }
            catch (Exception ex) { HuntLog("back-off goal lookup failed: " + ex.Message); }
            _backOffGoal[ai] = g;
            return g;
        }

        /// <summary>Per group, in Attack: memo, the surround check, and the holders' stepping back.</summary>
        private void TacticsTick(AIs.HumanAIGroup g)
        {
            if (!_tacticsEnabled.Value) return;
            if (g.m_State != AIs.HumanAIGroup.State.Attack) { s_Attack.Remove(g); return; }
            if (g.m_Members == null) return;

            float now = Time.time;
            AttackMemo memo;
            if (!s_Attack.TryGetValue(g, out memo))
            {
                memo = new AttackMemo();
                memo.StartedAt = now;
                memo.MembersAtStart = g.m_Members.Count;
                s_Attack[g] = memo;
            }

            Being enemy = null;
            for (int i = 0; i < g.m_Members.Count && enemy == null; i++)
            {
                AIs.HumanAI m = g.m_Members[i];
                if (m != null && m.m_EnemyModule != null) enemy = m.m_EnemyModule.m_Enemy;
            }
            if (enemy == null) return;

            if (!memo.BossReleased && _bossHolds.Value && HasBoss(g))
            {
                int close = 0;
                for (int i = 0; i < g.m_Members.Count; i++)
                {
                    AIs.HumanAI m = g.m_Members[i];
                    if (m == null || IsBoss(m) || IsArcher(m)) continue;
                    if (Vector3.Distance(m.transform.position, enemy.transform.position) <= 6f) close++;
                }
                string why = null;
                if (close >= _surroundedCount.Value) why = "you are surrounded (" + close + " within 6 m)";
                else if (g.m_Members.Count < memo.MembersAtStart) why = "the group lost a member";
                else if (now - memo.StartedAt > _bossWaitMax.Value) why = "it waited " + Mathf.RoundToInt(now - memo.StartedAt) + " s";
                if (why != null)
                {
                    memo.BossReleased = true;
                    Say("The boss joins the fight - " + why);
                }
            }

            for (int i = 0; i < g.m_Members.Count; i++)
            {
                AIs.HumanAI m = g.m_Members[i];
                if (m == null || m.m_EnemyModule == null || m.m_EnemyModule.m_Enemy == null || m.m_GoalsModule == null) continue;
                float keep;
                if (!Holds(m, out keep)) continue;
                float d = Vector3.Distance(m.transform.position, m.m_EnemyModule.m_Enemy.transform.position);
                if (d >= keep - 1.5f) continue;
                if (m.m_GoalsModule.GetActiveGoalType() == AIs.AIGoalType.HumanMoveAwayFromEnemy) continue;
                AIs.AIGoal back = BackOffGoal(m);
                if (back == null) continue;
                try
                {
                    Traverse.Create(back).Field("m_MinDistToEnemy").SetValue(keep);
                    m.m_GoalsModule.ActivateGoal(AIs.AIGoalType.HumanMoveAwayFromEnemy);
                }
                catch (Exception ex) { HuntLog("back off failed: " + ex.Message); }
            }
        }

        private static bool HasBoss(AIs.HumanAIGroup g)
        {
            for (int i = 0; i < g.m_Members.Count; i++) if (g.m_Members[i] != null && IsBoss(g.m_Members[i])) return true;
            return false;
        }
    }
}
