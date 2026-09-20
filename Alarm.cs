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
// THE TRAPS are the game's own tribe_spike_trap (619; Tribe_Bow_Trap 618 by choice), made with
// ItemsManager.CreateItem and flagged m_CantSave so they never enter the save. SET BY SCOUTS ONLY.
// His rule, 2026-09-20: "Make sure a trap is not set up but by a scout. No automatic trap placing
// randomly. A scout must place that trap, to prevent traps being set up inside a camp the player
// makes. A wandering scout checks on traps and places random traps to bring the number back up
// to six." The ring that used to appear when a camp woke is gone; a scout on its walk picks a spot,
// walks there, and sets one (PlaceTrapAt, from Scouts.cs) - never within PlayerCampClearMetres of
// anything he built. A trap that fires stays sprung until a scout comes to it.
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
        private ConfigEntry<float> _trapClear;
        private ConfigEntry<float> _trapEvery;
        private ConfigEntry<float> _trapGap;
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
            // KEY RENAMED (TrapsAroundCamp -> ScoutsSetTraps) so the cfg takes the new meaning.
            _trapsEnabled = Config.Bind("Alarm", "ScoutsSetTraps", true,
                "Scouts set the tribes' own traps where they walk, one at a time, up to MaxTraps. " +
                "Nothing else places a trap. Stepping on one hurts and brings a scout.");
            _trapEvery = Config.Bind("Alarm", "ScoutSetsTrapEverySeconds", 60f,
                new ConfigDescription("A scout sets at most one trap per this many seconds.",
                    new AcceptableValueRange<float>(10f, 600f)));
            _trapClear = Config.Bind("Alarm", "PlayerCampClearMetres", 15f,
                new ConfigDescription("No native trap is set within this distance of anything you built. " +
                    "His question: 'how did a trap get set up inside my camp?'",
                    new AcceptableValueRange<float>(0f, 60f)));
            _trapGap = Config.Bind("Alarm", "TrapSpacingMetres", 10f,
                new ConfigDescription("No two native traps closer than this.",
                    new AcceptableValueRange<float>(2f, 40f)));
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
                new ConfigDescription("How often a freshly placed trap that has not yet taken its arming is tried " +
                    "again. A trap that fired stays sprung until the scout resets it.",
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
                    s_Self.BossForCampAttack(__instance);
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
        private static readonly HashSet<Item> s_ArmedOnce = new HashSet<Item>();

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
            s_Traps.Remove(t); s_TrapSetAt.Remove(t); s_TrippedAt.Remove(t); s_ArmedOnce.Remove(t);
            if (why.StartsWith("room")) { s_RoomRemoved++; return; }        // summed up by the caller
            if (s_TrapLogged < 40) { s_TrapLogged++; Logger.LogInfo("traps: one removed - " + why); }
        }
        private static int s_TrapLogged;
        private static int s_RoomRemoved;

        /// <summary>Anything he built within PlayerCampClearMetres: his camp, not theirs to trap.</summary>
        private bool NearPlayerBuild(Vector3 at)
        {
            float r = _trapClear.Value;
            if (r <= 0f) return false;
            List<Construction> all = Construction.s_AllConstructions;
            if (all == null) return false;
            float r2 = r * r;
            for (int i = 0; i < all.Count; i++)
            {
                Construction c = all[i];
                if (c == null || c.IsSceneObject() || c.m_BadTribeConstruction || s_Traps.ContainsKey(c)) continue;
                Vector3 d = c.transform.position - at; d.y = 0f;
                if (d.sqrMagnitude < r2) return true;
            }
            return false;
        }

        private bool NearOtherTrap(Vector3 at)
        {
            float r2 = _trapGap.Value * _trapGap.Value;
            foreach (KeyValuePair<Item, AIs.HumanAIGroup> kv in s_Traps)
            {
                if (kv.Key == null) continue;
                Vector3 d = kv.Key.transform.position - at; d.y = 0f;
                if (d.sqrMagnitude < r2) return true;
            }
            return false;
        }

        /// <summary>Is this a spot a scout may trap? The same test at choosing and at setting.</summary>
        internal bool TrapSpotOk(Vector3 at, out string why)
        {
            why = null;
            if (s_Traps.Count >= Mathf.Max(1, _trapsMax.Value)) { why = "the world has its " + _trapsMax.Value; return false; }
            if (NearPlayerBuild(at)) { why = "too close to something you built"; return false; }
            if (NearOtherTrap(at)) { why = "too close to another trap"; return false; }
            Player p = Player.Get();
            if (p != null && Vector3.Distance(p.transform.position, at) < 6f) { why = "you are standing there"; return false; }
            return true;
        }

        /// <summary>One trap, set by a scout standing at the spot. The only way a trap is made.</summary>
        internal bool PlaceTrapAt(AIs.HumanAIGroup g, Vector3 at, AIs.HumanAI by)
        {
            if (!_trapsEnabled.Value) return false;
            ItemsManager im = ItemsManager.Get();
            if (im == null) return false;
            string why;
            if (!TrapSpotOk(at, out why))
            {
                if (s_TrapLogged < 40) { s_TrapLogged++; Logger.LogInfo("traps: '" + (by != null ? by.name : "?") + "' set no trap - " + why); }
                return false;
            }
            NavMeshHit hit;
            if (!NavMesh.SamplePosition(at, out hit, 4f, NavMesh.AllAreas)) return false;
            Vector3 dir = (by != null) ? by.transform.forward : Vector3.forward; dir.y = 0f;
            if (dir.sqrMagnitude < 0.01f) dir = Vector3.forward;
            bool spikes = _trapKind.Value != "Bow";
            Item item = im.CreateItem(spikes ? Enums.ItemID.tribe_spike_trap : Enums.ItemID.Tribe_Bow_Trap, false, hit.position, Quaternion.LookRotation(dir.normalized, Vector3.up), false);
            if (item == null) return false;
            bool ok = spikes ? (item is Spikes || item.GetComponent<Spikes>() != null) : (item is BowTrap || item.GetComponent<BowTrap>() != null);
            if (!ok)
            {
                HuntLog((spikes ? "tribe_spike_trap" : "Tribe_Bow_Trap") + " created but carries no trap component (" + item.GetType().Name + ") - removed");
                UnityEngine.Object.Destroy(item.gameObject);
                return false;
            }
            s_Traps[item] = g;
            s_TrapSetAt[item] = Time.time;
            // NEVER INTO THE SAVE. His report, 2026-09-20: "I don't think we're keeping only six
            // traps at most. I keep breaking traps and I keep finding more." That session's log
            // placed six, so the extras were not placed - they were LOADED. Item.CanSave (IL):
            // an item is saved unless m_CantSave, and Spikes.Save writes SpikesArmed and
            // SpikesMask, so every trap standing at save time came back on load: untracked,
            // uncounted, unswept, and handleable again. m_CantSave is the game's own flag for
            // items that must not persist (charcoal stands, forge inserts); the same here.
            item.m_CantSave = true;
            if (spikes && !_spikesHidden.Value) UnmaskSpikes(item as Spikes ?? item.GetComponent<Spikes>());
            if (_trapsArmed.Value) ArmTrap(item, im, hit.position);    // and the re-arm tick looks again in a few seconds
            if (s_TrapLogged < 40)
            {
                s_TrapLogged++;
                Player p = Player.Get();
                Logger.LogInfo("traps: '" + (by != null ? by.name : "?") + "' of '" + (g != null ? g.name : "no camp") + "' set a "
                    + (spikes ? "spike trap" : "bow trap") + (p != null ? " " + Mathf.RoundToInt(Vector3.Distance(p.transform.position, hit.position)) + " m from you" : "")
                    + " - " + s_Traps.Count + " of " + _trapsMax.Value + " in the world");
            }
            return true;
        }

        /// <summary>A scout passing a sprung trap resets it - "a wandering scout checks on traps".</summary>
        internal void ResetTrapsNear(AIs.HumanAI m)
        {
            if (!_trapsArmed.Value) return;
            Vector3 here = m.transform.position;
            foreach (KeyValuePair<Item, AIs.HumanAIGroup> kv in s_Traps)
            {
                Item t = kv.Key;
                if (t == null || TrapArmed(t)) continue;
                if (Vector3.Distance(t.transform.position, here) > 4f) continue;
                if (ArmTrap(t, ItemsManager.Get(), t.transform.position))
                {
                    s_TrippedAt.Remove(t);
                    Logger.LogInfo("scouts: '" + m.name + "' reset a sprung trap it passed");
                }
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
                    bool ok = tr.Field("m_Armed").GetValue<bool>();
                    if (ok) s_ArmedOnce.Add(t);
                    return ok;
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
                bool okBow = tr.Field("m_Armed").GetValue<bool>();
                if (okBow) s_ArmedOnce.Add(t);
                return okBow;
            }
            catch (Exception ex)
            {
                if (!s_ArmFailedSaid) { s_ArmFailedSaid = true; HuntLog("traps: arming failed (" + ex.Message + ") - traps ring the alarm only"); }
                return false;
            }
        }

        // HIS RULE: "add a timer where the traps get reset - every trap placed by the natives does
        // not get armed; give the trap time to re-arm." AND: "the re-arming should only happen
        // once, otherwise the scout will not work - a trap would re-arm itself before a scout can
        // arrive." So the timer only sees to the FIRST arming: a trap that was never armed since
        // placement (the game's own Start may have undone the first try) is armed again every
        // RearmSeconds until it takes. A trap that fired stays sprung until the scout resets it.
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
                if (s_ArmedOnce.Contains(t)) { armed++; continue; }        // was armed once; if sprung, the scout's job
                if (TrapArmed(t)) { s_ArmedOnce.Add(t); armed++; continue; }
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
                    if (obj == null || (!GameObjectExtension.IsPlayer(obj) && obj.GetComponentInParent<Player>() == null)) return;
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
            // 35 of these in one session's log, one every 20 s while he stood by a sprung trap
            // mid-fight; the camp was already on him. Said once a minute, with the reason.
            if (Time.time - s_TripSaidAt > 60f)
            {
                s_TripSaidAt = Time.time;
                Say(g != null && g.m_Active && g.m_State == AIs.HumanAIGroup.State.Attack
                    ? "You tripped a native trap - its camp is already fighting you"
                    : "You tripped a native trap - no calm camp is left to send a scout");
            }
            if (g != null && g.m_Active) Alarm(g, at, why, true);
            else CallNeighbours(g, at);
        }

        private float _trapTripAt;
        private static float s_TripSaidAt = -100f;
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
                // HIS TEST: stood on the spikes, no trip. The spikes' area is a box wider than the
                // 1.2 m from its centre; the box itself (m_DamageCollider, expanded a little) is
                // the measure now, the radius only for a trap without one.
                bool inside;
                Spikes spk = t as Spikes;
                if (spk != null && spk.m_DamageCollider != null)
                {
                    Bounds b = spk.m_DamageCollider.bounds; b.Expand(new Vector3(0.6f, 1.5f, 0.6f));
                    inside = b.Contains(p.transform.position);
                }
                else inside = Vector3.Distance(t.transform.position, p.transform.position) <= _trapTrip.Value;
                if (!inside) continue;
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
                    if (bt == null || !s_Traps.ContainsKey(bt) || other == null || s_TrigLogged >= 12) return;
                    string n = other.gameObject.name;
                    if (n.StartsWith("Sensor") || n.StartsWith("Anthill")) return;     // scene volumes, not him
                    s_TrigLogged++;
                    s_Self.Logger.LogInfo("traps: trigger entered by '" + n + "' player=" + GameObjectExtension.IsPlayer(other.gameObject)
                        + " playerAbove=" + (other.GetComponentInParent<Player>() != null));
                }
                catch (Exception) { }
            }
        }

        private static int s_TrigLogged;
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

        // STRAYS FROM OLDER SAVES. Traps set before the m_CantSave fix are already in his save and
        // come back with every load, outside this table. So the world is looked over: every tribal
        // spike or bow trap that is not the level's own (CJObject.IsSceneObject - the story villages'
        // traps are placed with the scene) and not in the table is taken in as ours with no camp -
        // counted against the cap, swept by distance and life, unhandleable, and never saved again.
        // Once at each load (the moment the game turns playable) and every minute after, so a trap
        // that arrives any other way is caught too.
        private float _strayAt = -999f;
        internal void StraysDue() { _strayAt = -999f; }

        private void AdoptStrayTraps()
        {
            if (Time.time - _strayAt < 60f) return;
            _strayAt = Time.time;
            if (!_trapsEnabled.Value) return;
            try
            {
                List<Item> found = new List<Item>();
                Spikes[] sp = UnityEngine.Object.FindObjectsOfType<Spikes>();
                for (int i = 0; i < sp.Length; i++) found.Add(sp[i]);
                BowTrap[] bt = UnityEngine.Object.FindObjectsOfType<BowTrap>();
                for (int i = 0; i < bt.Length; i++) found.Add(bt[i]);
                int adopted = 0;
                for (int i = 0; i < found.Count; i++)
                {
                    Item t = found[i];
                    if (t == null || s_Traps.ContainsKey(t)) continue;
                    Enums.ItemID id = t.GetInfoID();
                    if (id != Enums.ItemID.tribe_spike_trap && id != Enums.ItemID.Tribe_Bow_Trap) continue;
                    if (t.IsSceneObject()) continue;
                    t.m_CantSave = true;
                    s_Traps[t] = null;
                    // A stray is old by definition and it must not hold the cap against the scouts:
                    // the first 1.9.0 log took in twenty from his save and no scout ever got to set
                    // one ("the world has its 6"). It gets five minutes, not twenty.
                    s_TrapSetAt[t] = Time.time - Mathf.Max(0f, _trapLife.Value * 60f - 300f);
                    s_ArmedOnce.Add(t);        // whatever state it loaded in is the state it keeps
                    Spikes s = t as Spikes;
                    if (s != null && !_spikesHidden.Value) UnmaskSpikes(s);
                    adopted++;
                }
                if (adopted > 0)
                {
                    s_RoomRemoved = 0;
                    MakeRoomFor(0);
                    Logger.LogInfo("traps: " + adopted + " stray native trap(s) from an older save taken in - counted, swept (five minutes), never saved again"
                        + (s_RoomRemoved > 0 ? "; " + s_RoomRemoved + " farthest removed for the cap" : ""));
                }
            }
            catch (Exception ex) { HuntLog("stray traps: " + ex.Message); }
        }

        // BREAKABLE. It was made to refuse weapon hits on 2026-09-20 and he took that back the
        // same day: "if you're gonna change the ability for me to break the trap, then I need to
        // not take damage from it. Just strip it." So a native trap breaks like any construction;
        // the sweep sees it gone and a scout sets the next one.

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
