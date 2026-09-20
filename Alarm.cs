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
        private ConfigEntry<float> _trapsForget;
        private ConfigEntry<float> _trapTrip;
        private ConfigEntry<float> _trapRearm;
        private ConfigEntry<string> _trapKind;
        private ConfigEntry<bool>  _spikesHidden;
        private ConfigEntry<int>   _trapsMax;
        private ConfigEntry<bool>  _tripScout;
        private ConfigEntry<float> _trapLife;

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
            _trapsForget = Config.Bind("Alarm", "TrapsVanishBeyondMetres", 150f,
                new ConfigDescription("A camp's traps stay after the camp is wiped or asleep, until you are " +
                    "this far from them. His test: 'I thought I saw a trap, but I could not find it after " +
                    "the fight' - they used to go with the camp.", new AcceptableValueRange<float>(30f, 500f)));
            _trapKind = Config.Bind("Alarm", "TrapKind", "Spikes",
                new ConfigDescription("Which of the tribes' traps the ring is made of: Spikes (no arrow, his " +
                    "choice) or Bow.", new AcceptableValueList<string>("Spikes", "Bow")));
            _spikesHidden = Config.Bind("Alarm", "SpikesHiddenUnderLeaves", false,
                "The game hides its spike traps under leaves. Off (his rule): a trap he can see is a " +
                "trap he can avoid.");
            _trapRearm = Config.Bind("Alarm", "RearmSeconds", 10f,
                new ConfigDescription("How often native traps are checked and, if unarmed, armed again.",
                    new AcceptableValueRange<float>(2f, 120f)));
            _trapTrip = Config.Bind("Alarm", "TrapTripMetres", 1.2f,
                new ConfigDescription("Standing this close to a native trap trips it, whether or not the " +
                    "game's own trigger fires.", new AcceptableValueRange<float>(0.5f, 4f)));
            // HIS RULES, 2026-09-19: "There should be a maximum of six traps at all times. If the game
            // wants to spawn three new ones, three old ones should disappear - the three furthest
            // away from the player. A trap not visited by the player is a trap set away from the
            // player: a bad placement." And: "a timeout for the trap to disappear on its own -
            // traps around camp will never disappear otherwise, and that makes traps everywhere."
            _tripScout = Config.Bind("Alarm", "TripSendsScout", true,
                "A tripped trap sends the camp's scout to look and reset it, instead of raising the " +
                "camp at once. If the scout sees or hears you it runs home and a wave comes. Off: " +
                "the trip alarms the camp directly.");
            _trapsMax = Config.Bind("Alarm", "MaxTraps", 6,
                new ConfigDescription("Never more native traps than this in the world at once. Room for " +
                    "new ones is made by removing the ones farthest from you.", new AcceptableValueRange<int>(1, 30)));
            _trapLife = Config.Bind("Alarm", "TrapLifeMinutes", 20f,
                new ConfigDescription("A native trap vanishes on its own after this long, wherever it is.",
                    new AcceptableValueRange<float>(1f, 240f)));
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
                if (o == null || (caller != null && o == caller) || !o.m_Active || !Ours(o) || o.IsWave()) continue;
                if (o.m_State == AIs.HumanAIGroup.State.Attack) continue;
                if (o.m_Members == null || o.m_Members.Count == 0) continue;
                float d = ClosestMember(o, where);
                if (d > _callRadius.Value) continue;
                Alarm(o, where, "called by '" + (caller != null ? caller.name : "a wiped camp's trap") + "' from " + Mathf.RoundToInt(d) + " m", false);
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

        // THE TRAP IS AN ITEM: the tribes' spike trap (Spikes, his choice - no arrow for Pickup
        // Doctor to want) or their bow trap (BowTrap). Both are ITrapTriggerOwners with a public
        // OnEnterTrigger(GameObject), a private m_Armed and a private Arm(bool).
        private static readonly Dictionary<Item, AIs.HumanAIGroup> s_Traps = new Dictionary<Item, AIs.HumanAIGroup>();
        private static readonly Dictionary<Item, float> s_TrapSetAt = new Dictionary<Item, float>();

        private static bool IsSpikes(Item t) { return t is Spikes; }

        private static bool TrapArmed(Item t)
        {
            try { return Traverse.Create(t).Field("m_Armed").GetValue<bool>(); } catch (Exception) { return false; }
        }

        private static void TrapEnter(Item t, GameObject who)
        {
            Spikes sp = t as Spikes;
            if (sp != null) { sp.OnEnterTrigger(who); return; }
            BowTrap bt = t as BowTrap;
            if (bt != null) bt.OnEnterTrigger(who);
        }

        /// <summary>Spikes hide under leaves by design (m_Mask). His rule: a trap he can see is a trap he can avoid.</summary>
        private void UnmaskSpikes(Spikes sp)
        {
            try
            {
                if (sp.m_MaskObjects != null) for (int i = 0; i < sp.m_MaskObjects.Count; i++) if (sp.m_MaskObjects[i] != null) sp.m_MaskObjects[i].SetActive(false);
                Traverse.Create(sp).Field("m_Mask").SetValue(false);
            }
            catch (Exception) { }
        }

        /// <summary>Room for n more: the farthest from him go first, because a trap he never met was set in the wrong place.</summary>
        private void MakeRoomFor(int n)
        {
            Player p = Player.Get();
            if (p == null) return;
            int max = Mathf.Max(1, _trapsMax.Value);
            while (s_Traps.Count > 0 && s_Traps.Count + n > max)
            {
                Item far = null; float farD = -1f;
                foreach (KeyValuePair<Item, AIs.HumanAIGroup> kv in s_Traps)
                {
                    if (kv.Key == null) { far = kv.Key; break; }
                    float d = Vector3.Distance(kv.Key.transform.position, p.transform.position);
                    if (d > farD) { farD = d; far = kv.Key; }
                }
                if (far == null && farD < 0f) break;
                DropTrap(far, "room for new ones - it was " + Mathf.RoundToInt(farD) + " m from you, the farthest");
            }
        }

        private void DropTrap(Item t, string why)
        {
            try { if (t != null) UnityEngine.Object.Destroy(t.gameObject); } catch (Exception) { }
            s_Traps.Remove(t); s_TrapSetAt.Remove(t); s_TrippedAt.Remove(t);
            if (s_TrapLogged < 12) { s_TrapLogged++; Logger.LogInfo("traps: one removed - " + why); }
        }
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
            MakeRoomFor(want);
            float start = UnityEngine.Random.Range(0f, 360f);
            for (int i = 0; i < want; i++)
            {
                float ang = start + i * (360f / want) + UnityEngine.Random.Range(-20f, 20f);
                Vector3 dir = Quaternion.Euler(0f, ang, 0f) * Vector3.forward;
                Vector3 at = centre + dir * _trapRing.Value;
                NavMeshHit hit;
                if (!NavMesh.SamplePosition(at, out hit, 6f, NavMesh.AllAreas)) continue;
                // Facing outward (a bow trap's arrow flies at whoever walks in from outside the ring).
                bool spikes = _trapKind.Value != "Bow";
                Item item = im.CreateItem(spikes ? Enums.ItemID.tribe_spike_trap : Enums.ItemID.Tribe_Bow_Trap, false, hit.position, Quaternion.LookRotation(dir, Vector3.up), false);
                if (item == null) continue;
                bool ok = spikes ? (item is Spikes || item.GetComponent<Spikes>() != null) : (item is BowTrap || item.GetComponent<BowTrap>() != null);
                if (!ok)
                {
                    HuntLog((spikes ? "tribe_spike_trap" : "Tribe_Bow_Trap") + " created but carries no trap component (" + item.GetType().Name + ") - removed");
                    UnityEngine.Object.Destroy(item.gameObject);
                    continue;
                }
                s_Traps[item] = g;
                s_TrapSetAt[item] = Time.time;
                placed++;
                if (spikes && !_spikesHidden.Value) UnmaskSpikes(item as Spikes ?? item.GetComponent<Spikes>());
                if (_trapsArmed.Value) ArmTrap(item, im, hit.position);    // and the re-arm tick looks again in a few seconds
            }
            if (placed > 0 && s_TrapLogged < 8)
            {
                s_TrapLogged++;
                Logger.LogInfo("traps: " + placed + " of " + want + " set around '" + g.name + "' at " + Mathf.RoundToInt(_trapRing.Value)
                    + " m (centre from " + n + " spawn point(s)), " + (_trapKind.Value != "Bow" ? "spikes" : "bow traps") + (_trapsArmed.Value ? ", armed" : ", alarm only"));
            }
        }

        private static bool s_ArmFailedSaid;
        // THE WAY THE GAME ARMS ONE (BowTrap's IL): the arrow goes INTO the trap's arrow slot
        // (ItemSlot.InsertItem -> OnInsertItem -> SetArrow: parented, positioned, collisions
        // ignored), then Arm(). The first build only called SetArrow and left the arrow lying
        // half a metre above the trap as a loose item - which is why "every trap placed by the
        // natives does not get armed": a loose Tribe_Arrow is on his pickup list, and Pickup
        // Doctor took it. In the slot it is nobody's to take.
        private bool ArmTrap(Item t, ItemsManager im, Vector3 at)
        {
            try
            {
                Traverse tr = Traverse.Create(t);
                if (t is Spikes)
                {
                    // Spikes: no arrow. Arm(false) sets the armed body and m_Armed.
                    tr.Method("Arm", new Type[] { typeof(bool) }).GetValue(false);
                    if (!_spikesHidden.Value) UnmaskSpikes((Spikes)t);
                    return tr.Field("m_Armed").GetValue<bool>();
                }
                BowTrap bt = (BowTrap)t;
                ItemSlot slot = tr.Field("m_ArrowSlot").GetValue<ItemSlot>();
                Item arrow = tr.Field("m_Arrow").GetValue<Item>();
                if (arrow == null)
                {
                    arrow = im.CreateItem(Enums.ItemID.Tribe_Arrow, false, at + Vector3.up * 0.5f, Quaternion.identity, false);
                    if (arrow == null) { if (!s_ArmFailedSaid) { s_ArmFailedSaid = true; HuntLog("traps: no Tribe_Arrow could be made - traps ring the alarm only"); } return false; }
                    if (slot != null) slot.InsertItem(arrow);
                    else tr.Method("SetArrow", new Type[] { typeof(Item) }).GetValue(arrow);
                }
                tr.Method("Arm", new Type[] { typeof(bool) }).GetValue(false);
                return tr.Field("m_Armed").GetValue<bool>();
            }
            catch (Exception ex)
            {
                if (!s_ArmFailedSaid) { s_ArmFailedSaid = true; HuntLog("traps: arming failed (" + ex.Message + ") - traps ring the alarm only"); }
                return false;
            }
        }

        // HIS RULE: "add a timer where the traps get reset - every trap placed by the natives does
        // not get armed; give the trap time to re-arm." Every RearmSeconds each native trap is
        // looked at; one that is not armed is armed again, the arrow made afresh if it is gone.
        // The first look is a few seconds after placement, after the game's own Start has run.
        private float _rearmAt;
        private static int s_RearmLogged;

        private void TrapRearmTick()
        {
            if (Time.time - _rearmAt < _trapRearm.Value || s_Traps.Count == 0) return;
            _rearmAt = Time.time;
            ItemsManager im = ItemsManager.Get();
            if (im == null) return;
            int armed = 0, fixedUp = 0;
            foreach (KeyValuePair<Item, AIs.HumanAIGroup> kv in s_Traps)
            {
                Item t = kv.Key;
                if (t == null) continue;
                float setAt;
                if (s_TrapSetAt.TryGetValue(t, out setAt) && Time.time - setAt < 3f) continue;
                if (TrapArmed(t)) { armed++; continue; }
                if (!_trapsArmed.Value) continue;
                if (ArmTrap(t, im, t.transform.position)) fixedUp++;
            }
            if (fixedUp > 0 && s_RearmLogged < 10)
            {
                s_RearmLogged++;
                Logger.LogInfo("traps: " + fixedUp + " re-armed (" + armed + " were already armed)");
            }
        }

        [HarmonyPatch(typeof(BowTrap), "OnEnterTrigger")]
        private static class Patch_TrapTripped
        {
            private static void Prefix(BowTrap __instance, GameObject obj) { Tripped(__instance, obj); }
        }

        [HarmonyPatch(typeof(Spikes), "OnEnterTrigger")]
        private static class Patch_SpikesTripped
        {
            private static void Prefix(Spikes __instance, GameObject obj) { Tripped(__instance, obj); }
        }

        private static void Tripped(Item __instance, GameObject obj)
        {
            {
                if (s_Self == null) return;
                try
                {
                    AIs.HumanAIGroup g;
                    if (!s_Traps.TryGetValue(__instance, out g)) return;
                    if (obj == null || (!GameObjectExtension.IsPlayer(obj) && obj.GetComponent<Player>() == null)) return;
                    s_TripHandled = true;
                    float last;
                    if (s_TrippedAt.TryGetValue(__instance, out last) && Time.time - last < 20f && Time.time - last > 0.5f) return;
                    s_TrippedAt[__instance] = Time.time;
                    s_Self.TripResponse(g, __instance, obj.transform.position, "trap tripped");
                }
                catch (Exception ex) { s_Self.HuntLog("trap trip failed: " + ex.Message); }
            }
        }

        // TRIPPED BY DISTANCE AS WELL. His test: "Can't trigger the trap." No 'trap tripped' line in
        // the log. The game's TrapTrigger needs its collider entered; whether that happens on a
        // trap made outside the item registry I cannot see from here, so the alarm no longer waits
        // for it: standing within TrapTripMetres of a native trap trips it. The arrow, if any, is
        // still the game's to shoot through its own trigger.
        /// <summary>What a trip brings: a scout to look (his rule), or, with no calm camp to send one, the alarm.</summary>
        private void TripResponse(AIs.HumanAIGroup g, Item trap, Vector3 at, string why)
        {
            if (_tripScout.Value && TripSendsScout(g, trap, at)) return;
            Say("You tripped a native trap - the camp is alarmed");
            if (g != null && g.m_Active) Alarm(g, at, why, true);
            else CallNeighbours(g, at);
        }

        private float _trapTripAt;
        private static readonly Dictionary<Item, float> s_TrippedAt = new Dictionary<Item, float>();
        private static bool s_TripHandled;

        private void TrapTripByDistance()
        {
            if (Time.time - _trapTripAt < 0.2f || s_Traps.Count == 0) return;
            _trapTripAt = Time.time;
            Player p = Player.Get();
            if (p == null) return;
            foreach (KeyValuePair<Item, AIs.HumanAIGroup> kv in s_Traps)
            {
                Item t = kv.Key;
                if (t == null) continue;
                float last;
                if (s_TrippedAt.TryGetValue(t, out last) && Time.time - last < 20f) continue;
                if (Vector3.Distance(t.transform.position, p.transform.position) > _trapTrip.Value) continue;
                s_TrippedAt[t] = Time.time;
                // HIS RULE: "do the trigger trap - work your way around it to find a way to it being
                // triggered." So the game's own entry is called with the player, exactly what its
                // TrapTrigger would pass: BowTrap.OnEnterTrigger -> Shot -> the animator, the arrow
                // slot, the sound, the hit. The alarm prefix below sees that call like any other.
                bool fired = false;
                try { TrapEnter(t, p.gameObject); fired = true; }
                catch (Exception ex) { HuntLog("trap fire failed: " + ex.Message); }
                if (!s_TripHandled)
                {
                    // The prefix did not take it (IsPlayer said no to the Player object): here instead.
                    TripResponse(kv.Value, t, p.transform.position, "trap tripped (by distance" + (fired ? ", fired" : "") + ")");
                }
                s_TripHandled = false;
                return;
            }
        }

        [HarmonyPatch(typeof(TrapTrigger), "OnTriggerEnter")]
        private static class Patch_TrapTriggerEvidence
        {
            private static void Postfix(TrapTrigger __instance, Collider other)
            {
                try
                {
                    Item bt = __instance.GetComponentInParent<Item>();
                    if (bt == null || !s_Traps.ContainsKey(bt) || s_TrapLogged >= 8) return;
                    s_TrapLogged++;
                    s_Self.Logger.LogInfo("traps: trigger entered by '" + (other != null ? other.gameObject.name : "?") + "' player="
                        + (other != null && GameObjectExtension.IsPlayer(other.gameObject)));
                }
                catch (Exception) { }
            }
        }

        private float _trapSweepAt;

        /// <summary>Called from Update: traps far behind him go, whoever's camp they were.</summary>
        private void TrapSweep()
        {
            if (Time.time - _trapSweepAt < 5f || s_Traps.Count == 0) return;
            _trapSweepAt = Time.time;
            Player p = Player.Get();
            if (p == null) return;
            List<Item> gone = new List<Item>(); List<string> why = new List<string>();
            foreach (KeyValuePair<Item, AIs.HumanAIGroup> kv in s_Traps)
            {
                if (kv.Key == null) { gone.Add(kv.Key); why.Add("gone from the world"); continue; }
                float setAt;
                if (s_TrapSetAt.TryGetValue(kv.Key, out setAt) && Time.time - setAt > _trapLife.Value * 60f)
                { gone.Add(kv.Key); why.Add("its " + Mathf.RoundToInt(_trapLife.Value) + " minutes are up"); continue; }
                if (Vector3.Distance(kv.Key.transform.position, p.transform.position) > _trapsForget.Value)
                { gone.Add(kv.Key); why.Add("left " + Mathf.RoundToInt(_trapsForget.Value) + " m behind"); }
            }
            for (int i = 0; i < gone.Count; i++) DropTrap(gone[i], why[i]);
        }

        // THEIRS, NOT HIS. His words: "the trap should not be removed by the player or interacted
        // with." A native trap offers no actions to the crosshair (no take, no arm, no deconstruct)
        // and cannot be triggered by hand; walking into it is the only thing it answers to, and
        // that path (OnEnterTrigger) does not go through CanTrigger.
        [HarmonyPatch(typeof(BowTrap), "CanTrigger")]
        private static class Patch_NativeTrapCannotBeHandled
        {
            private static bool Prefix(BowTrap __instance, ref bool __result)
            {
                if (!s_Traps.ContainsKey(__instance)) return true;
                __result = false;
                return false;
            }
        }

        [HarmonyPatch(typeof(Spikes), "CanTrigger")]
        private static class Patch_NativeSpikesCannotBeHandled
        {
            private static bool Prefix(Spikes __instance, ref bool __result)
            {
                if (!s_Traps.ContainsKey(__instance)) return true;
                __result = false;
                return false;
            }
        }

        [HarmonyPatch(typeof(BowTrap), "GetActions")]
        private static class Patch_NativeTrapOffersNothing
        {
            private static bool Prefix(BowTrap __instance, List<TriggerAction.TYPE> actions)
            {
                if (!s_Traps.ContainsKey(__instance)) return true;
                if (actions != null) actions.Clear();
                return false;
            }
        }

        [HarmonyPatch(typeof(Spikes), "GetActions")]
        private static class Patch_NativeSpikesOfferNothing
        {
            private static bool Prefix(Spikes __instance, List<TriggerAction.TYPE> actions)
            {
                if (!s_Traps.ContainsKey(__instance)) return true;
                if (actions != null) actions.Clear();
                return false;
            }
        }

        private void RemoveTraps(AIs.HumanAIGroup g)
        {
            List<Item> gone = new List<Item>();
            foreach (KeyValuePair<Item, AIs.HumanAIGroup> kv in s_Traps)
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
                    s_Attack.Remove(__instance); s_Seen.Remove(__instance);      // the traps stay - see TrapSweep
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
