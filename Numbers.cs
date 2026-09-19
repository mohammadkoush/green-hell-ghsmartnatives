// GHSmartNatives - how many come, how soon, and one on demand.
//
// Moved here from Pickup Doctor's Savages tab (which multiplied the group size and scaled the spawn
// cooldowns), then reshaped to his rule: "they should randomly spawn in random numbers, no more than
// 5, no less than 2" and "if a wave is four or five, one of them must be a boss".
//
// WHERE THE GAME DECIDES A COUNT (all read from the IL):
//   HumanAIGroup.SetupSpawnersCount   on Activate: wanted = m_FromBalance ? CalculateMaxGroupMemeberCount()
//                                     : EnemyAISpawnManager.GetCurrentGroupMembersCount(); that many
//                                     spawners are switched on, capped by how many spawners the camp has
//   HumanAIGroup.UpdateMemberCount    the same two numbers, then ReduceMemberCount(n) - it only REDUCES
//   HumanAIPatrol.GetAiToSpawnCount   Random.Range over the difficulty, capped by m_AiToSpawn.Count
//   EnemyAISpawnManager.UpdateWaves   SpawnWave(Random.Range(1, current+1), false, camp)
//   HumanAIWave.TrySpawnWave          per member: a random name from m_AINames, or m_ThugPrefabName when
//                                     ShouldSpawnThug() (a roll against how many waves went without one)
//
// ONE ROLL PER GROUP, HELD. CalculateMax... and GetCurrentGroupMembersCount are read at activation
// AND again by UpdateMemberCount, which only ever reduces. A fresh roll on every read would walk
// every group down to the floor. So the roll is made once per activation and handed back from both
// reads while that group is the one asking; a static "who is asking" is set by prefixes on the two
// group methods and cleared by their postfixes. The game's own numbers are returned to any other
// caller.
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
        // ---------- config: Numbers ----------
        private ConfigEntry<bool>  _numbersEnabled;
        private ConfigEntry<int>   _membersMin;
        private ConfigEntry<int>   _membersMax;
        private ConfigEntry<int>   _bossFrom;
        private ConfigEntry<float> _cooldown;
        private ConfigEntry<KeyboardShortcut> _waveKey;

        private void BindNumbersConfig()
        {
            _numbersEnabled = Config.Bind("Numbers", "Enabled", true,
                "Camp groups, patrols and waves come in a random number between the two below, " +
                "instead of the game's own ramp. Off = the game's numbers, at once - nothing is " +
                "written into the save.");
            _membersMin = Config.Bind("Numbers", "MembersMin", 2,
                new ConfigDescription("No fewer than this.", new AcceptableValueRange<int>(1, 12)));
            _membersMax = Config.Bind("Numbers", "MembersMax", 5,
                new ConfigDescription("No more than this. A camp with fewer spawn points than the roll " +
                    "fields what it has.", new AcceptableValueRange<int>(1, 12)));
            _bossFrom = Config.Bind("Numbers", "BossFromCount", 4,
                new ConfigDescription("A wave of at least this many always contains one Thug (the boss). " +
                    "0 = leave it to the game's own roll.", new AcceptableValueRange<int>(0, 12)));
            _cooldown = Config.Bind("Numbers", "SpawnCooldownMultiplier", 1f,
                new ConfigDescription("Scales the wait before the next group or wave spawns. LOWER means " +
                    "sooner: 0.5 = half the usual wait, 1 = unchanged. Applied once each time the game " +
                    "re-arms a cooldown, never continuously.", new AcceptableValueRange<float>(0.1f, 1f)));
            _waveKey = Config.Bind("Numbers", "SpawnWaveKey", new KeyboardShortcut(KeyCode.Keypad7),
                "Spawn a wave at your position, right now - for testing. Numpad 7 by default; the " +
                "same key it had in Pickup Doctor.");
        }

        private static bool NumbersOn()
        {
            return s_Self != null && s_Self._numbersEnabled != null && s_Self._numbersEnabled.Value;
        }

        private int Roll()
        {
            int lo = Mathf.Min(_membersMin.Value, _membersMax.Value);
            int hi = Mathf.Max(_membersMin.Value, _membersMax.Value);
            return UnityEngine.Random.Range(lo, hi + 1);
        }

        // -----------------------------------------------------------------------------------------
        // Camp groups: one roll per activation
        // -----------------------------------------------------------------------------------------

        private static readonly Dictionary<AIs.HumanAIGroup, int> s_GroupRoll = new Dictionary<AIs.HumanAIGroup, int>();
        private static AIs.HumanAIGroup s_Asking;      // the group inside SetupSpawnersCount / UpdateMemberCount

        private static int RollFor(AIs.HumanAIGroup g)
        {
            int n;
            if (!s_GroupRoll.TryGetValue(g, out n))
            {
                n = s_Self.Roll();
                s_GroupRoll[g] = n;
                int spawners = (g.m_SimpleAiSpawners != null) ? g.m_SimpleAiSpawners.Count : -1;
                s_Self.NumbersLog("group '" + g.name + "' rolled " + n
                    + (spawners >= 0 && spawners < n ? " but has only " + spawners + " spawn point(s)" : ""));
            }
            return n;
        }

        [HarmonyPatch(typeof(AIs.HumanAIGroup), "SetupSpawnersCount")]
        private static class Patch_AskingOnSetup
        {
            private static void Prefix(AIs.HumanAIGroup __instance)
            {
                if (NumbersOn() && Ours(__instance) && !__instance.IsPatrol() && !__instance.IsWave()) s_Asking = __instance;
            }
            private static void Postfix() { s_Asking = null; }
        }

        [HarmonyPatch(typeof(AIs.HumanAIGroup), "UpdateMemberCount")]
        private static class Patch_AskingOnUpdate
        {
            private static void Prefix(AIs.HumanAIGroup __instance)
            {
                if (NumbersOn() && Ours(__instance) && !__instance.IsPatrol() && !__instance.IsWave()) s_Asking = __instance;
            }
            private static void Postfix() { s_Asking = null; }
        }

        [HarmonyPatch(typeof(AIs.HumanAIGroup), "CalculateMaxGroupMemeberCount")]
        private static class Patch_GroupMax
        {
            private static void Postfix(AIs.HumanAIGroup __instance, ref int __result)
            {
                try
                {
                    if (s_Asking == null || s_Asking != __instance) return;
                    __result = RollFor(__instance);
                }
                catch (Exception ex) { s_Self.NumbersLog("group max failed, leaving the game's: " + ex.Message); }
            }
        }

        [HarmonyPatch(typeof(AIs.EnemyAISpawnManager), "GetCurrentGroupMembersCount")]
        private static class Patch_CurrentCount
        {
            private static void Postfix(ref int __result)
            {
                try
                {
                    if (s_Asking == null) return;
                    __result = RollFor(s_Asking);
                }
                catch (Exception ex) { s_Self.NumbersLog("current count failed, leaving the game's: " + ex.Message); }
            }
        }

        // The roll is for one activation; the next one rolls again.
        [HarmonyPatch(typeof(AIs.HumanAIGroup), "Deactivate")]
        private static class Patch_ForgetRoll
        {
            private static void Postfix(AIs.HumanAIGroup __instance)
            {
                try { s_GroupRoll.Remove(__instance); s_NoticedAt.Remove(__instance); } catch (Exception) { }
            }
        }

        // -----------------------------------------------------------------------------------------
        // Patrols
        // -----------------------------------------------------------------------------------------

        [HarmonyPatch(typeof(AIs.HumanAIPatrol), "GetAiToSpawnCount")]
        private static class Patch_PatrolCount
        {
            private static void Postfix(AIs.HumanAIPatrol __instance, ref int __result)
            {
                try
                {
                    if (!NumbersOn() || !Ours(__instance)) return;
                    int n = s_Self.Roll();
                    int cap = (__instance.m_AiToSpawn != null) ? __instance.m_AiToSpawn.Count : n;
                    int was = __result;
                    __result = Mathf.Min(n, cap);
                    s_Self.NumbersLog("patrol '" + __instance.name + "': " + was + " -> " + __result
                        + (n > cap ? " (rolled " + n + ", capped by its " + cap + " spawn slot(s))" : ""));
                }
                catch (Exception ex) { s_Self.NumbersLog("patrol count failed, leaving the game's: " + ex.Message); }
            }
        }

        // -----------------------------------------------------------------------------------------
        // Waves: the count, and the boss
        // -----------------------------------------------------------------------------------------

        [HarmonyPatch(typeof(AIs.EnemyAISpawnManager), "SpawnWave")]
        private static class Patch_WaveCount
        {
            private static void Prefix(ref int count)
            {
                try
                {
                    if (!NumbersOn()) return;
                    if (s_BossEscort) { count = 1; return; }
                    int was = count;
                    count = s_Self.Roll();
                    s_Self.NumbersLog("wave: " + was + " -> " + count);
                }
                catch (Exception ex) { s_Self.NumbersLog("wave count failed, leaving the game's: " + ex.Message); }
            }
        }

        private static AIs.HumanAIWave s_BossWave;     // the wave being spawned that must hold a boss
        private static bool s_BossGiven;
        private static bool s_BossEscort;              // a wave of one, spawned to be the Thug of a camp's own wave
        private static AIs.HumanAIWave s_EscortWave;   // that wave, once the game has made it

        [HarmonyPatch(typeof(AIs.EnemyAISpawnManager), "SpawnWave")]
        private static class Patch_RememberEscort
        {
            private static void Postfix(AIs.HumanAIWave __result)
            {
                if (s_BossEscort && __result != null) s_EscortWave = __result;
            }
        }

        // THE CAMP'S OWN WAVE. His test: "at least four to five waves have passed with no thugs" -
        // and the log has not one 'numbers: wave' line. Read from UpdateWaves: when a camp group is
        // active the game does not SpawnWave at all - it sends the camp itself with
        // HumanAIGroup.StartWave(firecamp), and the Thug roll lives only in HumanAIWave. So when a
        // camp of BossFromCount or more starts its wave without a Thug among them, a wave of ONE is
        // asked of the game at the same firecamp, flagged so the count stays one and the one is
        // the Thug. Everything else about it is the game's.
        [HarmonyPatch(typeof(AIs.HumanAIGroup), "StartWave")]
        private static class Patch_CampWaveBoss
        {
            private static void Postfix(AIs.HumanAIGroup __instance, FirecampGroup group)
            {
                try
                {
                    if (!NumbersOn() || s_Self._bossFrom.Value <= 0) return;
                    if (__instance == null || __instance.IsWave() || !Ours(__instance)) return;
                    if (__instance.m_Members == null || __instance.m_Members.Count < s_Self._bossFrom.Value) return;
                    for (int i = 0; i < __instance.m_Members.Count; i++) if (IsBoss(__instance.m_Members[i])) return;
                    AIs.EnemyAISpawnManager mgr = AIs.EnemyAISpawnManager.Get();
                    if (mgr == null) return;
                    s_BossEscort = true;
                    AIs.HumanAIWave w = null;
                    try { w = mgr.SpawnWave(1, false, group); }
                    finally { s_BossEscort = false; }
                    s_Self.NumbersLog("camp '" + __instance.name + "' (" + __instance.m_Members.Count + ") sends its wave - a Thug "
                        + (w != null ? "is sent with it" : "could not be sent (the game declined)"));
                    if (w != null) s_Self.Say("A wave of " + __instance.m_Members.Count + " - with a boss");
                }
                catch (Exception ex) { s_Self.NumbersLog("camp wave boss failed: " + ex.Message); }
            }
        }

        [HarmonyPatch(typeof(AIs.HumanAIWave), "TrySpawnWave")]
        private static class Patch_BossWave
        {
            private static void Prefix(AIs.HumanAIWave __instance)
            {
                s_BossWave = null; s_BossGiven = false;
                try
                {
                    if (!NumbersOn() || s_Self._bossFrom.Value <= 0) return;
                    if (__instance == null) return;
                    if (__instance.m_Count < s_Self._bossFrom.Value && s_EscortWave != __instance) return;
                    s_BossWave = __instance;
                }
                catch (Exception) { }
            }
            private static void Postfix(AIs.HumanAIWave __instance)
            {
                try
                {
                    if (s_BossWave == __instance && s_BossGiven)
                        s_Self.Say("A wave of " + __instance.m_Count + " - with a boss");
                }
                catch (Exception) { }
                s_BossWave = null;
            }
        }

        [HarmonyPatch(typeof(AIs.HumanAIWave), "ShouldSpawnThug")]
        private static class Patch_Boss
        {
            private static void Postfix(AIs.HumanAIWave __instance, ref bool __result)
            {
                try
                {
                    if (s_BossWave == null || s_BossWave != __instance) return;
                    if (__result) { s_BossGiven = true; return; }        // the game's own roll gave one
                    if (s_BossGiven) return;                             // one boss, not a wave of them
                    // m_ThugPrefabName is private; read it the Harmony way.
                    string thug = Traverse.Create(__instance).Field("m_ThugPrefabName").GetValue<string>();
                    if (string.IsNullOrEmpty(thug))
                    {
                        s_Self.NumbersLog("wave of " + __instance.m_Count + " wanted a boss but the wave has no thug prefab name - none forced");
                        s_BossGiven = true;                              // do not say it again this wave
                        return;
                    }
                    __result = true;
                    s_BossGiven = true;
                    s_Self.NumbersLog("wave of " + __instance.m_Count + ": one boss (thug) forced in"
                        + (GreenHellGame.Instance != null && GreenHellGame.Instance.m_GameMode != Enums.GameMode.Story
                           ? " - the game itself only rolls thugs in Story mode (mode now: " + GreenHellGame.Instance.m_GameMode + ")" : ""));
                }
                catch (Exception ex) { s_Self.NumbersLog("boss failed, leaving the game's roll: " + ex.Message); }
            }
        }

        // -----------------------------------------------------------------------------------------
        // Sooner: scale a cooldown on the frame the game re-arms it
        // -----------------------------------------------------------------------------------------

        private static float s_LastGroupCd = -1f;
        private static float s_LastWaveCd  = -1f;

        private void NumbersTick()
        {
            AIs.EnemyAISpawnManager mgr = AIs.EnemyAISpawnManager.Get();
            if (mgr == null) return;

            float mult = _cooldown.Value;
            if (mult < 1f)
            {
                mgr.m_TimeToNextSpawnGroup = ScaleCooldown(mgr.m_TimeToNextSpawnGroup, ref s_LastGroupCd, mult);
                mgr.m_TimeToNextSpawnWave  = ScaleCooldown(mgr.m_TimeToNextSpawnWave,  ref s_LastWaveCd,  mult);
            }
            if (_waveKey.Value.IsDown()) SpawnWaveNow();
        }

        /// <summary>A countdown only ever ticks down; a value that went UP is a fresh cooldown, and that is the one moment to scale it.</summary>
        private static float ScaleCooldown(float current, ref float last, float mult)
        {
            if (current > last + 0.001f) current *= mult;
            last = current;
            return current;
        }

        // -----------------------------------------------------------------------------------------
        // One on demand - for testing; he said it goes later
        // -----------------------------------------------------------------------------------------

        private void SpawnWaveNow()
        {
            try
            {
                AIs.EnemyAISpawnManager mgr = AIs.EnemyAISpawnManager.Get();
                if (mgr == null) { Say("No level - no wave"); return; }
                DifficultySettingsPreset pre = DifficultySettings.ActivePreset;
                if (pre != null && !pre.m_Tribes) { Say("Tribes are OFF in your difficulty preset - no wave"); return; }

                // group:null - verified in SpawnWave's IL: a null FirecampGroup makes it spawn at
                // Player.Get().transform.position. hallucination:false - a real attack.
                int count = NumbersOn() ? Roll() : Mathf.Max(_membersMin.Value, 1);
                AIs.HumanAIWave wave = mgr.SpawnWave(count, false, null);
                if (wave == null) { Say("The game declined to spawn a wave just now - try again away from camp"); return; }
                Say("Wave called: " + wave.m_Count + " native(s) at your position");
            }
            catch (Exception ex) { NumbersLog("could not spawn a wave: " + ex.Message); }
        }

        private const int NumbersLogCap = 30;
        private static int s_NumbersLogged;
        internal void NumbersLog(string msg)
        {
            if (s_NumbersLogged >= NumbersLogCap) return;
            s_NumbersLogged++;
            Logger.LogInfo("numbers: " + msg + (s_NumbersLogged == NumbersLogCap ? "  (further numbers lines suppressed this session)" : ""));
        }
    }
}
