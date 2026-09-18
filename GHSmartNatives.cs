// GHSmartNatives - natives that hunt, roam and come in numbers.
//
// The game's tribes sit in their camps until the player walks into them. Three things change here:
//
//   HUNT     a camp that has the player within reach notices them and comes - the game's own attack
//            state, started early and kept up while the player stays in range.
//   ROAM     a calm camp walks around instead of sitting: each native's rest spot drifts around the
//            camp, and the game's own rest goal walks them to it.
//   NUMBERS  a group, a patrol or a wave spawns a random count between a floor and a ceiling
//            (his numbers: no fewer than 2, no more than 5), instead of the game's ramp.
//
// Plus the on-demand wave and the spawn-cooldown scaling that used to live in Pickup Doctor's
// Savages tab, moved here because they are about natives and nothing else.
//
// Everything was read out of Assembly-CSharp with Mono.Cecil before it was written:
//
//   HumanAIGroup.State  None=0 Calm=1 Upset=2 StartWave=3 Attack=4;  HumanAI.State Rest=0 ... Attack=5
//   HumanAIGroup.UpdateState: ShouldSetAttackState -> SetState(Attack), else ShouldSetUpsetState,
//     else ShouldSetCalmState. Attack = every member SetState(Attack) -> GoalHumanMoveToEnemy.
//   ShouldSetAttackState: true when any member's EnemyModule.m_Enemy is a Player not ignored by AI.
//     So giving every member the player as enemy IS the notice; the game does the rest.
//   ShouldSetCalmState (in Attack): true only when no member sees, senses or holds an enemy.
//   EnemyModule.CanSetEnemy: a HumanAI in Attack state keeps its enemy without a sight check.
//   GoalHumanRest.UpdateAction: when the native is farther than m_SamplePosRange from
//     HumanAI.m_StartPosition it stops crouching, paths there and walks (HumanMoveTo). So moving
//     m_StartPosition is the whole of "roam" - the game's own goal does the walking.
//   HumanAIGroup.SetupSpawnersCount (on Activate): wanted = m_FromBalance ? CalculateMax... :
//     EnemyAISpawnManager.GetCurrentGroupMembersCount(); that many spawners are switched on.
//   HumanAIGroup.UpdateMemberCount: the same two numbers, then ReduceMemberCount - REDUCE only.
//   HumanAIPatrol.GetAiToSpawnCount: Random.Range over the difficulty, capped by m_AiToSpawn.Count.
//   EnemyAISpawnManager.UpdateWaves: SpawnWave(Random.Range(1, current+1), false, camp).
//
// Single player. Language level is C# 5 (stock Framework csc.exe) - no ?., no $"", no ??=.

using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace GHSmartNatives
{
    [BepInPlugin(Guid, Name, Version)]
    public partial class GHSmartNativesPlugin : BaseUnityPlugin
    {
        public const string Guid    = "com.mohammadkoush.ghsmartnatives";
        public const string Name    = "GHSmartNatives";
        public const string Version = "1.0.0";

        private static GHSmartNativesPlugin s_Self;
        private Harmony _harmony;

        // ---------- config: General ----------
        private ConfigEntry<KeyboardShortcut> _key;
        private ConfigEntry<bool>   _storyGroupsToo;
        private ConfigEntry<bool>   _notices;
        private ConfigEntry<string> _windowPos;

        private void Awake()
        {
            s_Self = this;

            _key = Config.Bind("General", "OpenKey", new KeyboardShortcut(KeyCode.J),
                "Opens the natives panel. J by default: the game binds no J, and every other mod " +
                "here was swept for it.");
            _storyGroupsToo = Config.Bind("General", "StoryGroupsToo", false,
                "Also apply hunt, roam and numbers to quest and challenge groups. Off: story content " +
                "stays as the game shipped it.");
            _notices = Config.Bind("General", "ShowNotices", true,
                "A short line at the top of the screen when natives notice you, give up, or a wave " +
                "is called. The log has every one regardless.");
            _windowPos = Config.Bind("General", "WindowPosition", "",
                "Where the panel was last dragged. Written automatically.");

            BindHuntConfig();
            BindRoamConfig();
            BindNumbersConfig();

            try
            {
                _harmony = new Harmony(Guid);
                // PatchAll(ASSEMBLY): PatchAll(Type) applies nothing for nested patch classes and
                // reads as working. The Pickup Doctor lesson, kept.
                _harmony.PatchAll(typeof(GHSmartNativesPlugin).Assembly);
                Logger.LogInfo(Name + " " + Version + " loaded - J opens the panel. Hunt "
                    + (_huntEnabled.Value ? "ON" : "off") + ", roam " + (_roamEnabled.Value ? "ON" : "off")
                    + ", numbers " + (_numbersEnabled.Value ? (_membersMin.Value + "-" + _membersMax.Value) : "off") + ".");
            }
            catch (Exception ex)
            {
                Logger.LogError("Harmony patching failed - nothing here can work: " + ex);
            }

            LoadWindowPos();
        }

        private void OnDestroy()
        {
            try { RestoreSenses(); RestoreRoam(); } catch (Exception) { }
            try { if (_harmony != null) _harmony.UnpatchSelf(); } catch (Exception) { }
        }

        // -----------------------------------------------------------------------------------------
        // Is the game playable - the gate Field Notes settled on, copied whole
        // -----------------------------------------------------------------------------------------
        private float _agreedAt = -1f;
        private bool _playable;

        private bool GameIsPlayable()
        {
            try
            {
                GreenHellGame game = GreenHellGame.Instance;
                if (game == null || game.m_LoadGameState != LoadGameState.None) return NotYet();
                LoadingScreen screen = LoadingScreen.Get();
                if (screen != null && screen.m_Active) return NotYet();
                MainLevel level = MainLevel.Instance;
                if (level == null || !level.m_LevelStarted) return NotYet();
                if (Player.Get() == null) return NotYet();
                if (_agreedAt < 0f) _agreedAt = Time.realtimeSinceStartup;
                if (Time.realtimeSinceStartup - _agreedAt < 1f) return false;
                if (!_playable) { _playable = true; Logger.LogInfo("game is playable - panel and ticks are live"); }
                return true;
            }
            catch (Exception) { return true; }
        }

        private bool NotYet()
        {
            _agreedAt = -1f;
            if (_playable) { _playable = false; if (_open) SetOpen(false); }
            return false;
        }

        private void Update()
        {
            try
            {
                if (!GameIsPlayable()) return;
                NumbersTick();
                if (_key.Value.IsDown()) SetOpen(!_open);
                if (_open && Input.GetKeyDown(KeyCode.Escape)) SetOpen(false);
            }
            catch (Exception) { }
        }

        // -----------------------------------------------------------------------------------------
        // Notices - one line at the top, briefly
        // -----------------------------------------------------------------------------------------
        private static string s_Notice = "";
        private static float  s_NoticeUntil;

        internal void Say(string text)
        {
            Logger.LogMessage(text);
            if (_notices == null || !_notices.Value) return;
            s_Notice = text;
            s_NoticeUntil = Time.realtimeSinceStartup + 4f;
        }

        // -----------------------------------------------------------------------------------------
        // Open / close - the GHAudioControl recipe, the same code because it is the one that works
        // -----------------------------------------------------------------------------------------
        private bool _open;
        private static bool  s_BlockMenu;
        private static float s_BlockMenuUntil;
        private bool _pausedByUs;
        private bool _inputBlocked;

        private void SetOpen(bool open)
        {
            if (open == _open) return;
            _open = open;
            try
            {
                MainLevel lvl = MainLevel.Instance;
                if (open)
                {
                    s_BlockMenu = true;
                    if (lvl != null) { lvl.Pause(true); _pausedByUs = true; }
                    Player pl = Player.Get();
                    if (pl != null && !_inputBlocked) { pl.BlockRotation(); pl.BlockMoves(); _inputBlocked = true; }
                    CursorManager cm = CursorManager.Get();
                    if (cm != null) { cm.SetCursorLockState(CursorLockMode.None); cm.ShowCursor(true, false); }
                }
                else
                {
                    s_BlockMenu = false;
                    s_BlockMenuUntil = Time.realtimeSinceStartup + 0.25f;
                    if (_pausedByUs && lvl != null) { lvl.Pause(false); _pausedByUs = false; }
                    Player pl = Player.Get();
                    if (pl != null && _inputBlocked) { pl.UnblockRotation(); pl.UnblockMoves(); }
                    _inputBlocked = false;
                    CursorManager cm = CursorManager.Get();
                    if (cm != null) { cm.ShowCursor(false, false); cm.SetCursorLockState(CursorLockMode.Locked); }
                    SaveWindowPos();
                }
            }
            catch (Exception ex) { Logger.LogWarning("open/close: " + ex.Message); }
        }

        [HarmonyPatch(typeof(MenuInGameManager), "ShowScreen")]
        private static class Patch_BlockGameMenu
        {
            private static bool Prefix()
            {
                return !(s_BlockMenu || Time.realtimeSinceStartup < s_BlockMenuUntil);
            }
        }

        // -----------------------------------------------------------------------------------------
        // The panel
        // -----------------------------------------------------------------------------------------
        private Rect _rect = new Rect(200f, 120f, 560f, 700f);
        private Vector2 _scroll;
        private bool _styled;
        private GUIStyle _title, _head, _row, _dim, _rowBtn, _rowBtnDim, _noticeStyle;
        private Texture2D _radioOn, _radioOff, _pixel, _cross;

        private void OnGUI()
        {
            try
            {
                BuildStyles();

                if (_playable && s_Notice.Length > 0 && Time.realtimeSinceStartup < s_NoticeUntil)
                {
                    Rect nr = new Rect(0f, Screen.height * 0.08f, Screen.width, 30f);
                    Rect sh = new Rect(1f, nr.y + 1f, nr.width, nr.height);
                    Color old = GUI.color;
                    GUI.color = new Color(0f, 0f, 0f, 0.8f); GUI.Label(sh, s_Notice, _noticeStyle);
                    GUI.color = old;                          GUI.Label(nr, s_Notice, _noticeStyle);
                }

                if (!_open || !_playable) return;
                _rect = GUI.Window(0x6A0D20, _rect, DrawWindow, "");
            }
            catch (Exception ex) { Logger.LogWarning("panel: " + ex.Message); }
        }

        private void DrawWindow(int id)
        {
            GUI.DrawTexture(new Rect(0f, 0f, _rect.width, _rect.height), _pixel);
            GUILayout.BeginVertical();
            GUILayout.Label(Name, _title);
            GUILayout.Label(StatusText(), _dim);
            GUILayout.Space(6f);

            _scroll = GUILayout.BeginScrollView(_scroll);

            GUILayout.Label("Hunt", _head);
            bool hunt = _huntEnabled.Value;
            if (Row("Natives hunt you when you come near", hunt) != hunt) _huntEnabled.Value = !hunt;
            if (_huntEnabled.Value)
            {
                _huntRadius.Value  = Slider("They notice you within", _huntRadius.Value, 5f, 200f, " m", 0);
                _keepRadius.Value  = Slider("They keep hunting within", _keepRadius.Value, 10f, 300f, " m", 0);
                _giveUpSecs.Value  = Slider("Give up after losing you for", _giveUpSecs.Value, 5f, 600f, " s", 0);
            }

            GUILayout.Space(8f);
            GUILayout.Label("Roam", _head);
            bool roam = _roamEnabled.Value;
            if (Row("Camp natives never sit - they walk, and look for you", roam) != roam) _roamEnabled.Value = !roam;
            if (_roamEnabled.Value)
            {
                _searchRadius.Value = Slider("They search for you within", _searchRadius.Value, 20f, 500f, " m", 0);
                _stepMetres.Value   = Slider("Each searching step", _stepMetres.Value, 4f, 40f, " m", 0);
                _roamRadius.Value   = Slider("With nobody to look for, wander this far from camp", _roamRadius.Value, 3f, 40f, " m", 0);
                _roamEveryMin.Value = Slider("A spot never reached is replaced after", _roamEveryMin.Value, 3f, 120f, " s", 0);
                _roamEveryMax.Value = Slider("...and no later than", _roamEveryMax.Value, 5f, 240f, " s", 0);
                if (_roamEveryMax.Value < _roamEveryMin.Value) _roamEveryMax.Value = _roamEveryMin.Value;
            }

            GUILayout.Space(8f);
            GUILayout.Label("Numbers", _head);
            bool num = _numbersEnabled.Value;
            if (Row("Groups, patrols and waves come in a random number", num) != num) _numbersEnabled.Value = !num;
            if (_numbersEnabled.Value)
            {
                _membersMin.Value = Mathf.RoundToInt(Slider("No fewer than", _membersMin.Value, 1f, 12f, "", 0));
                _membersMax.Value = Mathf.RoundToInt(Slider("No more than", _membersMax.Value, 1f, 12f, "", 0));
                if (_membersMax.Value < _membersMin.Value) _membersMax.Value = _membersMin.Value;
            }
            _cooldown.Value = Slider("How soon the next group or wave comes (1 = the game's own wait)",
                                     _cooldown.Value, 0.1f, 1f, " x", 2);
            bool story = _storyGroupsToo.Value;
            if (Row("Story groups too (quest and challenge camps)", story) != story) _storyGroupsToo.Value = !story;
            if (ActionRow("Spawn a wave at my position now   (" + _waveKey.Value + ")")) SpawnWaveNow();

            GUILayout.Space(8f);
            bool note = _notices.Value;
            if (Row("Show notices on screen", note) != note) _notices.Value = !note;

            GUILayout.EndScrollView();

            GUILayout.Space(6f);
            if (ActionRow("Close   (" + _key.Value.MainKey + " or Esc)")) SetOpen(false);
            GUILayout.EndVertical();
            GUI.DragWindow(new Rect(0f, 0f, _rect.width, 40f));
        }

        private float Slider(string label, float value, float lo, float hi, string unit, int decimals)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("        " + label, _row, GUILayout.ExpandWidth(true));
            GUILayout.Label(value.ToString("F" + decimals) + unit, _row, GUILayout.Width(70f));
            GUILayout.EndHorizontal();
            float v = GUILayout.HorizontalSlider(value, lo, hi);
            GUILayout.Space(4f);
            if (decimals == 0) v = Mathf.Round(v);
            return v;
        }

        private bool ActionRow(string label)
        {
            GUIContent c = new GUIContent("    " + label);
            bool clicked = GUILayout.Button(c, _rowBtn, GUILayout.Height(32f));
            if (Event.current.type == EventType.Repaint)
            {
                Rect r = GUILayoutUtility.GetLastRect();
                Rect rr = new Rect(r.x + 8f, r.y + (r.height - 22f) * 0.5f, 22f, 22f);
                GUI.DrawTexture(rr, _cross, ScaleMode.ScaleToFit, true);
            }
            return clicked;
        }

        /// <summary>One row: radio on the left, name, a real button underneath. Returns the new on/off.</summary>
        private bool Row(string label, bool on)
        {
            GUIContent c = new GUIContent("    " + label);
            bool clicked = GUILayout.Button(c, on ? _rowBtn : _rowBtnDim, GUILayout.Height(32f));
            if (Event.current.type == EventType.Repaint)
            {
                Rect r = GUILayoutUtility.GetLastRect();
                Rect rr = new Rect(r.x + 8f, r.y + (r.height - 22f) * 0.5f, 22f, 22f);
                GUI.DrawTexture(rr, on ? _radioOn : _radioOff, ScaleMode.ScaleToFit, true);
            }
            if (clicked)
            {
                Logger.LogInfo("panel: " + label + " -> " + (on ? "OFF" : "ON"));
                return !on;
            }
            return on;
        }

        private void BuildStyles()
        {
            if (_styled) return;
            _styled = true;

            _pixel = new Texture2D(1, 1, TextureFormat.ARGB32, false);
            _pixel.SetPixel(0, 0, new Color(0.06f, 0.07f, 0.10f, 0.92f));
            _pixel.Apply();
            _radioOn  = Radio(true);
            _radioOff = Radio(false);
            _cross    = Cross();

            _title = new GUIStyle(GUI.skin.label);
            _title.fontSize = 18; _title.fontStyle = FontStyle.Bold;
            _title.normal.textColor = new Color(0.96f, 0.97f, 1f);

            _head = new GUIStyle(GUI.skin.label);
            _head.fontSize = 16; _head.fontStyle = FontStyle.Bold;
            _head.normal.textColor = new Color(0.55f, 0.75f, 1f);

            _row = new GUIStyle(GUI.skin.label);
            _row.fontSize = 15; _row.alignment = TextAnchor.MiddleLeft;
            _row.normal.textColor = new Color(0.92f, 0.93f, 0.95f);

            _dim = new GUIStyle(_row);
            _dim.normal.textColor = new Color(0.70f, 0.85f, 0.70f);
            _dim.wordWrap = true;

            _rowBtn = new GUIStyle(GUI.skin.button);
            _rowBtn.fontSize = 15; _rowBtn.alignment = TextAnchor.MiddleLeft;
            _rowBtn.normal.background = null; _rowBtn.active.background = null;
            _rowBtn.focused.background = null;
            _rowBtn.hover.background = HoverTex();
            _rowBtn.normal.textColor = _row.normal.textColor;
            _rowBtn.hover.textColor = Color.white; _rowBtn.active.textColor = Color.white;
            _rowBtn.padding = new RectOffset(38, 6, 3, 3);
            _rowBtn.wordWrap = true;
            _rowBtnDim = new GUIStyle(_rowBtn);
            _rowBtnDim.normal.textColor = new Color(0.55f, 0.57f, 0.62f);

            _noticeStyle = new GUIStyle(GUI.skin.label);
            _noticeStyle.fontSize = 18; _noticeStyle.fontStyle = FontStyle.Bold;
            _noticeStyle.alignment = TextAnchor.MiddleCenter;
            _noticeStyle.normal.textColor = new Color(1f, 0.85f, 0.6f);
        }

        private static Texture2D HoverTex()
        {
            Texture2D t = new Texture2D(1, 1, TextureFormat.ARGB32, false);
            t.SetPixel(0, 0, new Color(1f, 1f, 1f, 0.08f));
            t.Apply();
            return t;
        }

        private static Texture2D Cross()
        {
            const int S = 32;
            Texture2D t = new Texture2D(S, S, TextureFormat.ARGB32, false);
            Color ink = new Color(0.85f, 0.87f, 0.92f, 1f);
            for (int y = 0; y < S; y++)
                for (int x = 0; x < S; x++)
                {
                    bool d1 = Mathf.Abs(x - y) <= 2 && x >= 8 && x <= 23;
                    bool d2 = Mathf.Abs(x - (S - 1 - y)) <= 2 && x >= 8 && x <= 23;
                    t.SetPixel(x, y, (d1 || d2) ? ink : new Color(0f, 0f, 0f, 0f));
                }
            t.Apply();
            return t;
        }

        private static Texture2D Radio(bool on)
        {
            const int S = 32;
            Texture2D t = new Texture2D(S, S, TextureFormat.ARGB32, false);
            Color ring = new Color(0.85f, 0.87f, 0.92f, 1f);
            Color dot  = new Color(0.55f, 0.85f, 1f, 1f);
            float c = (S - 1) * 0.5f;
            for (int y = 0; y < S; y++)
                for (int x = 0; x < S; x++)
                {
                    float d = Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c));
                    Color px = new Color(0f, 0f, 0f, 0f);
                    if (d >= 11.5f && d <= 14f) px = ring;
                    else if (on && d <= 7f) px = dot;
                    t.SetPixel(x, y, px);
                }
            t.Apply();
            return t;
        }

        private void LoadWindowPos()
        {
            try
            {
                string[] p = (_windowPos.Value ?? "").Split(',');
                if (p.Length == 2)
                {
                    float x, y;
                    if (float.TryParse(p[0], out x) && float.TryParse(p[1], out y)) { _rect.x = x; _rect.y = y; }
                }
            }
            catch (Exception) { }
        }

        private void SaveWindowPos()
        {
            string v = Mathf.RoundToInt(_rect.x) + "," + Mathf.RoundToInt(_rect.y);
            if (v != _windowPos.Value) _windowPos.Value = v;
        }

        /// <summary>Is this a group the settings are allowed to touch.</summary>
        private static bool Ours(AIs.HumanAIGroup g)
        {
            if (g == null) return false;
            if (s_Self != null && !s_Self._storyGroupsToo.Value && (g.m_QuestCampGroup || g.m_ChallengeGroup)) return false;
            return true;
        }

        private static Player HuntablePlayer()
        {
            Player p = Player.Get();
            if (p == null || p.IsDead() || p.IsIgnoredByAI()) return null;
            return p;
        }

        private static float ClosestMember(AIs.HumanAIGroup g, Vector3 pos)
        {
            float best = float.MaxValue;
            if (g == null || g.m_Members == null) return best;
            for (int i = 0; i < g.m_Members.Count; i++)
            {
                AIs.HumanAI m = g.m_Members[i];
                if (m == null) continue;
                float d = Vector3.Distance(m.transform.position, pos);
                if (d < best) best = d;
            }
            return best;
        }
    }
}
