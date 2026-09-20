// GHSmartNatives - stealth: what the natives hear and see of him, and what he hears of himself.
//
// His rules, 2026-09-19:
//   "Any audio the player makes while moving - its volume should be based on the level of noise
//    the player creates."
//   "I want crouching noise to be two metres less than what it is right now."
//   "Staying still, crouching, reduces the natives' distance they can see me by half."
//
// Read from the IL and the AI scripts:
//   PlayerAudioModule.PlayFootstepSound -> PlayRandomSound(clips, 1f, false, type) with the Noise
//     type Sneak when ducked, Walk, Run, Swim. The volume is a constant 1 - so a sneaking step
//     sounds exactly as loud to HIM as a running one, whatever the natives hear of it.
//   HearingModule.OnNoise: the natives' range per noise type is AIParams.m_HearingSneakRange (5 m),
//     Walk (6), Run (9), Swim (6), Action (6); within 1.5x is a "low noise".
//   SightModule.IsBeingInSight: distance against AIParams.m_SightRange (10 m).
// AIParams is one shared object per AI kind, so a change reaches every native of that kind at
// once; the originals are kept per object and put back on disable and on unload. Set, not
// chased: a value is written only when it differs from what is wanted.
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
        private ConfigEntry<bool>  _stealthEnabled;
        private ConfigEntry<float> _sneakMinus;
        private ConfigEntry<float> _stillSight;
        private ConfigEntry<float> _volSneak;
        private ConfigEntry<float> _volWalk;
        private ConfigEntry<float> _volRun;
        private ConfigEntry<float> _senseWalk;
        private ConfigEntry<float> _senseRun;
        private ConfigEntry<float> _senseCrouch;
        private ConfigEntry<float> _nightMinus;

        private void BindStealthConfig()
        {
            _stealthEnabled = Config.Bind("Stealth", "Enabled", true,
                "Crouching is heard from less far, standing still while crouched halves how far natives " +
                "see you, and your own steps sound as loud as the noise they make.");
            _sneakMinus = Config.Bind("Stealth", "CrouchHeardMinusMetres", 2f,
                new ConfigDescription("Taken off the distance natives hear a crouched step (the game's is 5 m).",
                    new AcceptableValueRange<float>(0f, 5f)));
            _stillSight = Config.Bind("Stealth", "StillCrouchSightFactor", 0.5f,
                new ConfigDescription("Natives' sight range is multiplied by this while you are crouched AND still " +
                    "(the game's is 10 m).", new AcceptableValueRange<float>(0.1f, 1f)));
            // "They can still sense me behind walls." Two things do that, and one is the game's:
            // EnemySenseRange, 7 m in any direction through anything, no eyes involved. It is a
            // number in the same shared params, so it gets the same treatment - set, restored.
            // (The other is Hunt's keep-hunting radius: a camp already on him stays on him.)
            // BY WHAT HE IS DOING. His words, 2026-09-20: "Natives sense me two metres from behind
            // walls. That's it. And especially if I'm making walking noise. And 4 m when I'm running
            // noise, but not when I'm moving in crouch position." The game's sense range knows
            // nothing of his move style, so the shared number is set each tick from his controller.
            _senseWalk = Config.Bind("Stealth", "SenseWalkingMetres", 2f,
                new ConfigDescription("How close a native senses you through anything while you walk or stand " +
                    "(the game's is 7 m at all times).", new AcceptableValueRange<float>(0f, 12f)));
            _senseRun = Config.Bind("Stealth", "SenseRunningMetres", 4f,
                new ConfigDescription("...while you run.", new AcceptableValueRange<float>(0f, 12f)));
            _senseCrouch = Config.Bind("Stealth", "SenseCrouchedMetres", 0f,
                new ConfigDescription("...while you are crouched. 0 = not at all.", new AcceptableValueRange<float>(0f, 12f)));
            // NIGHT. "Natives at night, they lose one metre on everything, sight and sound." The
            // game's night is MainLevel.IsNight(): before 5 or after 22.
            _nightMinus = Config.Bind("Stealth", "NightMinusMetres", 1f,
                new ConfigDescription("At night every native sees and hears this much less far.",
                    new AcceptableValueRange<float>(0f, 5f)));
            _volSneak = Config.Bind("Stealth", "StepVolumeCrouched", 0.45f,
                new ConfigDescription("How loud your own crouched steps are to you.", new AcceptableValueRange<float>(0f, 2f)));
            _volWalk = Config.Bind("Stealth", "StepVolumeWalking", 0.85f,
                new ConfigDescription("...walking.", new AcceptableValueRange<float>(0f, 2f)));
            _volRun = Config.Bind("Stealth", "StepVolumeRunning", 1.25f,
                new ConfigDescription("...running.", new AcceptableValueRange<float>(0f, 2f)));

            _stealthEnabled.SettingChanged += delegate { if (!_stealthEnabled.Value) RestoreParams(); };
        }

        // -----------------------------------------------------------------------------------------
        // His own steps
        // -----------------------------------------------------------------------------------------

        [HarmonyPatch(typeof(PlayerAudioModule), "PlayRandomSound", new Type[] { typeof(List<AudioClip>), typeof(float), typeof(bool), typeof(Noise.Type) })]
        private static class Patch_StepVolume
        {
            private static void Prefix(ref float volume, Noise.Type noise_type)
            {
                if (s_Self == null || !s_Self._stealthEnabled.Value) return;
                switch (noise_type)
                {
                    case Noise.Type.Sneak: volume *= s_Self._volSneak.Value; break;
                    case Noise.Type.Walk:  volume *= s_Self._volWalk.Value; break;
                    case Noise.Type.Run:   volume *= s_Self._volRun.Value; break;
                    default: break;
                }
                volume = Mathf.Clamp(volume, 0f, 2f);
            }
        }

        // -----------------------------------------------------------------------------------------
        // What the natives hear and see
        // -----------------------------------------------------------------------------------------

        private class ParamsOriginal { public float Sneak, Walk, Run, Swim, Action; public float Sight; public float Sense; }
        private readonly Dictionary<AIs.AIParams, ParamsOriginal> _paramsOrig = new Dictionary<AIs.AIParams, ParamsOriginal>();
        private bool _stillCrouched;
        private Vector3 _lastPlayerPos;
        private float _movedAt;
        private int _moveKind;               // 0 walk or stand, 1 run, 2 crouched
        private static int s_MoveLogged;
        private bool _night;

        /// <summary>Crouched and not moving for a moment: half the sight.</summary>
        private void StealthTick()
        {
            if (!_stealthEnabled.Value) return;
            try
            {
                Player p = Player.Get();
                FPPController fpp = FPPController.Get();
                if (p == null || fpp == null) return;
                Vector3 pos = p.transform.position;
                if ((pos - _lastPlayerPos).sqrMagnitude > 0.0025f) { _movedAt = Time.time; _lastPlayerPos = pos; }
                bool still = fpp.IsDuck() && Time.time - _movedAt > 0.6f;
                if (still != _stillCrouched)
                {
                    _stillCrouched = still;
                    HuntLog("stealth: " + (still ? "crouched and still - natives see half as far" : "moving or up - natives see their full range"));
                }
                int kind = fpp.IsDuck() ? 2 : (fpp.IsRunning() ? 1 : 0);
                if (kind != _moveKind)
                {
                    _moveKind = kind;
                    // Its own small budget: the first 1.9.0 log spent eleven of the hunt log's
                    // twenty-four lines on walk/run/crouch.
                    if (s_MoveLogged < 6)
                    {
                        s_MoveLogged++;
                        Logger.LogInfo("hunt: stealth: you " + (kind == 2 ? "crouch" : kind == 1 ? "run" : "walk") + " - sensed through walls within "
                            + (kind == 2 ? _senseCrouch.Value : kind == 1 ? _senseRun.Value : _senseWalk.Value).ToString("F0") + " m"
                            + (s_MoveLogged == 6 ? "  (further move lines suppressed)" : ""));
                    }
                }
                bool night = false;
                try { MainLevel lvl = MainLevel.Instance; night = lvl != null && lvl.IsNight(); } catch (Exception) { }
                if (night != _night)
                {
                    _night = night;
                    HuntLog("stealth: " + (night ? "night - natives see and hear " + _nightMinus.Value.ToString("F0") + " m less" : "day - their full ranges"));
                }
            }
            catch (Exception) { }
        }

        /// <summary>Per group tick: every member's shared params carry the wanted numbers.</summary>
        private void ApplyStealthTo(AIs.HumanAIGroup g)
        {
            if (!_stealthEnabled.Value || g.m_Members == null) return;
            for (int i = 0; i < g.m_Members.Count; i++)
            {
                AIs.HumanAI m = g.m_Members[i];
                if (m == null || m.m_Params == null) continue;
                AIs.AIParams prm = m.m_Params;
                ParamsOriginal o;
                if (!_paramsOrig.TryGetValue(prm, out o))
                {
                    o = new ParamsOriginal(); o.Sneak = prm.m_HearingSneakRange; o.Walk = prm.m_HearingWalkRange; o.Run = prm.m_HearingRunRange;
                    o.Swim = prm.m_HearingSwimRange; o.Action = prm.m_HearingActionRange; o.Sight = prm.m_SightRange; o.Sense = prm.m_EnemySenseRange;
                    _paramsOrig[prm] = o;
                    HuntLog("stealth: '" + m.name + "' kind hears a crouched step at " + o.Sneak.ToString("F1") + " m, sees " + o.Sight.ToString("F1") + " m and senses " + o.Sense.ToString("F1") + " m by the game");
                }
                float dark = _night ? _nightMinus.Value : 0f;
                float wantSneak = Mathf.Max(0.5f, o.Sneak - _sneakMinus.Value - dark);
                float wantSight = Mathf.Max(0.5f, (_stillCrouched ? o.Sight * _stillSight.Value : o.Sight) - dark);
                if (Mathf.Abs(prm.m_HearingSneakRange - wantSneak) > 0.01f) prm.m_HearingSneakRange = wantSneak;
                if (Mathf.Abs(prm.m_SightRange - wantSight) > 0.01f) prm.m_SightRange = wantSight;
                float wantWalk = Mathf.Max(0.5f, o.Walk - dark), wantRun = Mathf.Max(0.5f, o.Run - dark);
                float wantSwim = Mathf.Max(0.5f, o.Swim - dark), wantAction = Mathf.Max(0.5f, o.Action - dark);
                if (Mathf.Abs(prm.m_HearingWalkRange - wantWalk) > 0.01f) prm.m_HearingWalkRange = wantWalk;
                if (Mathf.Abs(prm.m_HearingRunRange - wantRun) > 0.01f) prm.m_HearingRunRange = wantRun;
                if (Mathf.Abs(prm.m_HearingSwimRange - wantSwim) > 0.01f) prm.m_HearingSwimRange = wantSwim;
                if (Mathf.Abs(prm.m_HearingActionRange - wantAction) > 0.01f) prm.m_HearingActionRange = wantAction;
                float wantSense = _moveKind == 2 ? _senseCrouch.Value : (_moveKind == 1 ? _senseRun.Value : _senseWalk.Value);
                if (Mathf.Abs(prm.m_EnemySenseRange - wantSense) > 0.01f) prm.m_EnemySenseRange = wantSense;
            }
        }

        private void RestoreParams()
        {
            foreach (KeyValuePair<AIs.AIParams, ParamsOriginal> kv in _paramsOrig)
            {
                try
                {
                    if (kv.Key != null)
                    {
                        kv.Key.m_HearingSneakRange = kv.Value.Sneak; kv.Key.m_HearingWalkRange = kv.Value.Walk; kv.Key.m_HearingRunRange = kv.Value.Run;
                        kv.Key.m_HearingSwimRange = kv.Value.Swim; kv.Key.m_HearingActionRange = kv.Value.Action;
                        kv.Key.m_SightRange = kv.Value.Sight; kv.Key.m_EnemySenseRange = kv.Value.Sense;
                    }
                }
                catch (Exception) { }
            }
            _paramsOrig.Clear();
        }
    }
}
