// GHSmartNatives - the alarm: camps call each other, and traps around a camp are its tripwire.
//
// His words: "If we can get them to set an arrow trap around camp, they can even use it to trigger
// an alarm. It doesn't have to have an arrow in it. Triggering their arrow trap without an arrow
// is a call for arms." And from the list: "camps call each other".
//
// ONE ALARM, THREE CALLERS. Alarm(group, where, why) is the whole of it: the group learns where
// you are (its search memory), every member gets you as enemy (the game's own attack condition)
// and, if calling is on, every calm camp within CallRadius is alarmed the same way. It is rung by
//   - a camp entering Attack for any reason (postfix OnEnterAttackState) -> the neighbours
//   - a trap of theirs being stepped on (prefix BowTrap.OnEnterTrigger)  -> the owning camp
//   - the hunt's own notice, so a noticed camp calls its neighbours too
//
// THE TRAPS are the game's own Tribe_Bow_Trap (ItemID 618), the one the tribes leave in their
// villages, created with ItemsManager.CreateItem(id, im_register:false, ...) so they are never
// written into the save; a ring of them appears when a camp activates and is destroyed when it
// deactivates. With an arrow (the default) they shoot exactly as the game's do - BowTrap.OnEnterTrigger
// fires on the player and calls Shot(); the arrow is a Tribe_Arrow set through the trap's own
// private SetArrow/Arm. Without one the trip still rings the alarm, which is his point.
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
        private ConfigEntry<bool>  _callEnabled;
        private ConfigEntry<float> _callRadius;
        private ConfigEntry<bool>  _trapsEnabled;
        private ConfigEntry<int>   _trapsPerCamp;
        private ConfigEntry<float> _trapRing;
        private ConfigEntry<bool>  _trapsArmed;

        private void BindAlarmConfig()
        {
            _callEnabled = Config.Bind("Alarm", "CampsCallEachOther", true,
                "A camp that starts hunting you, or trips on its trap, calls every calm camp within " +
                "CallRadius into the hunt.");
            _callRadius = Config.Bind("Alarm", "CallRadiusMetres", 120f,
                new ConfigDescription("How far a call to arms carries, measured to the nearest member " +
                    "of the other camp.", new AcceptableValueRange<float>(20f, 400f)));
            _trapsEnabled = Config.Bind("Alarm", "TrapsAroundCamp", true,
                "A ring of the tribes' own bow traps appears around a camp when it wakes. Stepping " +
                "on one is a call to arms, arrow or no arrow.");
            _trapsPerCamp = Config.Bind("Alarm", "TrapsPerCamp", 3,
                new ConfigDescription("How many traps in the ring.", new AcceptableValueRange<int>(1, 8)));
            _trapRing = Config.Bind("Alarm", "TrapRingMetres", 18f,
                new ConfigDescription("How far from the camp's centre the ring sits.",
                    new AcceptableValueRange<float>(6f, 40f)));
            _trapsArmed = Config.Bind("Alarm", "TrapsHaveArrows", true,
                "The traps carry a tribe arrow and shoot, like the game's own. Off: they only ring " +
                "the alarm.");
        }

        // -----------------------------------------------------------------------------------------
        // The alarm itself
        // -----------------------------------------------------------------------------------------

        private static float s_LastCallAt = -100f;

        internal void Alarm(AIs.HumanAIGroup g, Vector3 where, string why, bool callNeighbours)
        {
            try
            {
                if (g == null || !g.m_Active || !Ours(g) || g.m_Members == null) return;
                RememberSeen(g, where);
                Being t = HuntTarget();
                int armed = 0;
                if (t != null && g.m_State != AIs.HumanAIGroup.State.Attack)
                {
                    for (int i = 0; i < g.m_Members.Count; i++)
                    {
                        AIs.HumanAI m = g.m_Members[i];
                        if (m == null || m.m_EnemyModule == null) continue;
                        m.m_EnemyModule.SetEnemy(t);
                        armed++;
                    }
                }
                Logger.LogInfo("alarm: '" + g.name + "' - " + why + (armed > 0 ? " - " + armed + " member(s) pointed at you" : ""));
                if (callNeighbours) CallNeighbours(g, where);
            }
            catch (Exception ex) { HuntLog("alarm failed: " + ex.Message); }
        }

        private void CallNeighbours(AIs.HumanAIGroup caller, Vector3 where)
        {
            if (!_callEnabled.Value || AIs.HumanAIGroup.s_AIGroups == null) return;
            int answered = 0;
            for (int i = 0; i < AIs.HumanAIGroup.s_AIGroups.Count; i++)
            {
                AIs.HumanAIGroup o = AIs.HumanAIGroup.s_AIGroups[i];
                if (o == null || o == caller || !o.m_Active || !Ours(o) || o.IsWave()) continue;
                if (o.m_State == AIs.HumanAIGroup.State.Attack) continue;
                if (o.m_Members == null || o.m_Members.Count == 0) continue;
                float d = ClosestMember(o, where);
                if (d > _callRadius.Value) continue;
                Alarm(o, where, "called by '" + caller.name + "' from " + Mathf.RoundToInt(d) + " m", false);
                answered++;
            }
            if (answered > 0 && Time.time - s_LastCallAt > 5f)
            {
                s_LastCallAt = Time.time;
                Say(answered + " more camp" + (answered == 1 ? "" : "s") + " answer the call");
            }
        }

        [HarmonyPatch(typeof(AIs.HumanAIGroup), "OnEnterAttackState")]
        private static class Patch_CallToArms
        {
            private static void Postfix(AIs.HumanAIGroup __instance)
            {
                if (s_Self == null) return;
                try
                {
                    if (!Ours(__instance) || __instance.IsWave()) return;
                    Being t = HuntTarget();
                    if (t == null) return;
                    RememberSeen(__instance, t.transform.position);
                    s_Self.CallNeighbours(__instance, t.transform.position);
                }
                catch (Exception ex) { s_Self.HuntLog("call to arms failed: " + ex.Message); }
            }
        }

        // -----------------------------------------------------------------------------------------
        // Traps
        // -----------------------------------------------------------------------------------------

        private static readonly Dictionary<BowTrap, AIs.HumanAIGroup> s_Traps = new Dictionary<BowTrap, AIs.HumanAIGroup>();
        private static int s_TrapLogged;

        [HarmonyPatch(typeof(AIs.HumanAIGroup), "Activate")]
        private static class Patch_TrapsOnWake
        {
            private static void Postfix(AIs.HumanAIGroup __instance)
            {
                if (s_Self == null) return;
                try { s_Self.PlaceTraps(__instance); }
                catch (Exception ex) { s_Self.HuntLog("traps failed: " + ex.Message); }
            }
        }

        private void PlaceTraps(AIs.HumanAIGroup g)
        {
            if (!_trapsEnabled.Value || g == null || !Ours(g) || g.IsWave() || g.IsPatrol()) return;
            ItemsManager im = ItemsManager.Get();
            if (im == null) return;

            // The camp's centre: its spawn points, or failing that the group object itself.
            Vector3 centre = g.transform.position; int n = 0;
            if (g.m_SimpleAiSpawners != null)
            {
                Vector3 sum = Vector3.zero;
                for (int i = 0; i < g.m_SimpleAiSpawners.Count; i++)
                    if (g.m_SimpleAiSpawners[i] != null) { sum += g.m_SimpleAiSpawners[i].transform.position; n++; }
                if (n > 0) centre = sum / n;
            }

            int placed = 0, want = _trapsPerCamp.Value;
            float start = UnityEngine.Random.Range(0f, 360f);
            for (int i = 0; i < want; i++)
            {
                float ang = start + i * (360f / want) + UnityEngine.Random.Range(-20f, 20f);
                Vector3 dir = Quaternion.Euler(0f, ang, 0f) * Vector3.forward;
                Vector3 at = centre + dir * _trapRing.Value;
                NavMeshHit hit;
                if (!NavMesh.SamplePosition(at, out hit, 6f, NavMesh.AllAreas)) continue;
                // Facing outward: the arrow flies at whoever walks in from outside the ring.
                Item item = im.CreateItem(Enums.ItemID.Tribe_Bow_Trap, false, hit.position, Quaternion.LookRotation(dir, Vector3.up), false);
                if (item == null) continue;
                BowTrap bt = item as BowTrap;
                if (bt == null) bt = item.GetComponent<BowTrap>();
                if (bt == null)
                {
                    HuntLog("Tribe_Bow_Trap created but carries no BowTrap component (" + item.GetType().Name + ") - removed");
                    UnityEngine.Object.Destroy(item.gameObject);
                    continue;
                }
                s_Traps[bt] = g;
                placed++;
                if (_trapsArmed.Value) ArmTrap(bt, im, hit.position);
            }
            if (placed > 0 && s_TrapLogged < 8)
            {
                s_TrapLogged++;
                Logger.LogInfo("traps: " + placed + " of " + want + " set around '" + g.name + "' at " + Mathf.RoundToInt(_trapRing.Value)
                    + " m (centre from " + n + " spawn point(s))" + (_trapsArmed.Value ? ", with arrows" : ", alarm only"));
            }
        }

        private static bool s_ArmFailedSaid;
        private void ArmTrap(BowTrap bt, ItemsManager im, Vector3 at)
        {
            try
            {
                Item arrow = im.CreateItem(Enums.ItemID.Tribe_Arrow, false, at + Vector3.up * 0.5f, Quaternion.identity, false);
                if (arrow == null) { if (!s_ArmFailedSaid) { s_ArmFailedSaid = true; HuntLog("traps: no Tribe_Arrow could be made - traps ring the alarm only"); } return; }
                Traverse tr = Traverse.Create(bt);
                tr.Method("SetArrow", new Type[] { typeof(Item) }).GetValue(arrow);
                tr.Method("Arm", new Type[] { typeof(bool) }).GetValue(false);
            }
            catch (Exception ex)
            {
                if (!s_ArmFailedSaid) { s_ArmFailedSaid = true; HuntLog("traps: arming failed (" + ex.Message + ") - traps ring the alarm only"); }
            }
        }

        [HarmonyPatch(typeof(BowTrap), "OnEnterTrigger")]
        private static class Patch_TrapTripped
        {
            private static void Prefix(BowTrap __instance, GameObject obj)
            {
                if (s_Self == null) return;
                try
                {
                    AIs.HumanAIGroup g;
                    if (!s_Traps.TryGetValue(__instance, out g)) return;
                    if (obj == null || !GameObjectExtension.IsPlayer(obj)) return;
                    if (g == null) { s_Traps.Remove(__instance); return; }
                    s_Self.Say("You tripped a native trap - the camp is alarmed");
                    s_Self.Alarm(g, obj.transform.position, "trap tripped", true);
                }
                catch (Exception ex) { s_Self.HuntLog("trap trip failed: " + ex.Message); }
            }
        }

        private void RemoveTraps(AIs.HumanAIGroup g)
        {
            List<BowTrap> gone = new List<BowTrap>();
            foreach (KeyValuePair<BowTrap, AIs.HumanAIGroup> kv in s_Traps)
                if (kv.Key == null || g == null || kv.Value == g) gone.Add(kv.Key);
            for (int i = 0; i < gone.Count; i++)
            {
                try { if (gone[i] != null) UnityEngine.Object.Destroy(gone[i].gameObject); } catch (Exception) { }
                s_Traps.Remove(gone[i]);
            }
        }

        [HarmonyPatch(typeof(AIs.HumanAIGroup), "Deactivate")]
        private static class Patch_TrapsOnSleep
        {
            private static void Postfix(AIs.HumanAIGroup __instance)
            {
                if (s_Self == null) return;
                try
                {
                    s_Self.RemoveTraps(__instance); s_Attack.Remove(__instance); s_Seen.Remove(__instance);
                    s_LastScoutWave.Remove(__instance);
                    List<AIs.HumanAI> ex = new List<AIs.HumanAI>();
                    foreach (AIs.HumanAI k in s_Scouts.Keys) if (k == null || k.m_Group == __instance) ex.Add(k);
                    for (int i = 0; i < ex.Count; i++) s_Scouts.Remove(ex[i]);
                }
                catch (Exception) { }
            }
        }
    }
}
