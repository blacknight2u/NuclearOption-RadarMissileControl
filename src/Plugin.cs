using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using Mirage;
using Mirage.Serialization;
using NuclearOption.Networking;
using UnityEngine;

namespace Blacknight2u.SARHControl
{
    internal enum LegacyMode
    {
        Vanilla = 0,
        Nerfed = 1,
        [Obsolete("Legacy configuration name; use Nerfed.")]
        Nerf50 = Nerfed,
        [Obsolete("Legacy configuration name; use Nerfed.")]
        Nerf80 = Nerfed,
        // Kept only so v0.5.0 configuration files can be migrated.
        Disabled = 2
    }

    public enum ActivePreset
    {
        Vanilla = 0,
        Preset1 = 1,
        Preset2 = 2
    }

    [Flags]
    internal enum RadarSeekerKind : byte
    {
        None = 0,
        Sarh = 1,
        Arh = 2
    }

    [NetworkMessage]
    internal struct SarhHelloMessage
    {
        internal byte ProtocolVersion;
    }

    [NetworkMessage]
    internal struct SarhStatusMessage
    {
        internal byte ProtocolVersion;
        internal byte ActivePreset;
    }

    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    [BepInProcess("NuclearOption.exe")]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "blacknight2u.sarhcontrol";
        public const string PluginName = "Radar Missile Control";
        public const string PluginVersion = "0.5.3";

        private const byte ProtocolVersion = 6;
        private const float SettingsWindowWidth = 520f;
        private const int SettingsWindowId = 5393747;
        private sealed class PresetConfig
        {
            internal ConfigEntry<bool> AffectSarh;
            internal ConfigEntry<bool> AffectArh;
            internal ConfigEntry<bool> AiToPlayer;
            internal ConfigEntry<bool> AiToAi;
            internal ConfigEntry<bool> PlayerToAi;
            internal ConfigEntry<bool> PlayerToPlayer;
            internal ConfigEntry<float> TrackingPercent;
            internal ConfigEntry<float> LockPercent;
            internal ConfigEntry<float> JamPercent;
            internal ConfigEntry<float> ThrustPercent;
            internal ConfigEntry<float> SpeedPercent;
            internal ConfigEntry<float> AimErrorPercent;
        }

        private static ConfigEntry<int> configurationVersion;
        private static ConfigEntry<ActivePreset> activePreset;
        private static ConfigEntry<KeyboardShortcut> toggleShortcut;
        private static ConfigEntry<KeyboardShortcut> panelShortcut;
        private static PresetConfig preset1;
        private static PresetConfig preset2;
        private readonly HashSet<INetworkPlayer> moddedClients = new HashSet<INetworkPlayer>();
        private Harmony harmony;
        private MessageHandler clientHandler;
        private MessageHandler serverHandler;
        private NetworkClient registeredClient;
        private byte? synchronizedActivePreset;
        private GUIStyle noticeStyle;
        private GUIStyle sectionStyle;
        private GUIStyle hintStyle;
        private string noticeText;
        private float noticeUntil;
        private Rect settingsWindowRect = new Rect(24f, 72f, SettingsWindowWidth, 680f);
        private Vector2 settingsScroll;
        private bool settingsVisible;
        private bool cursorStateCaptured;
        private CursorLockMode previousCursorLockMode;
        private bool previousCursorVisible;
        private bool applyingSettings;

        private int editingPreset = 1;
        private bool draftAffectSarh;
        private bool draftAffectArh;
        private bool draftAiToPlayer;
        private bool draftAiToAi;
        private bool draftPlayerToAi;
        private bool draftPlayerToPlayer;
        private float draftTrackingPercent;
        private float draftLockPercent;
        private float draftJamPercent;
        private float draftThrustPercent;
        private float draftSpeedPercent;
        private float draftAimErrorPercent;

        internal static ActivePreset CurrentActivePreset => activePreset?.Value ?? ActivePreset.Vanilla;
        private static PresetConfig CurrentPreset => CurrentActivePreset == ActivePreset.Preset2 ? preset2 : preset1;
        internal static bool PresetActive => CurrentActivePreset != ActivePreset.Vanilla;
        internal static float TrackingMultiplier => ReadPercent(CurrentPreset?.TrackingPercent, 50f);
        internal static float LockMultiplier => ReadPercent(CurrentPreset?.LockPercent, 50f);
        internal static float JamMultiplier => ReadPercent(CurrentPreset?.JamPercent, 50f);
        internal static float ThrustMultiplier => ReadPercent(CurrentPreset?.ThrustPercent, 50f);
        internal static float SpeedMultiplier => ReadPercent(CurrentPreset?.SpeedPercent, 50f);
        internal static float AimErrorFraction => Mathf.Clamp((CurrentPreset?.AimErrorPercent.Value ?? 4f) / 100f, 0f, 0.1f);

        internal static bool IsAuthoritativeHost
        {
            get
            {
                NetworkManagerNuclearOption manager = NetworkManagerNuclearOption.i;
                return manager != null && manager.Server != null && manager.Server.Active;
            }
        }

        private void Awake()
        {
            configurationVersion = Config.Bind("General", "Configuration version", 0, "Internal migration version. Do not edit.");
            if (configurationVersion.Value < 1)
            {
                MigrateLegacyConfiguration();
            }
            else
            {
                BindCurrentConfiguration();
            }
            panelShortcut = Config.Bind(
                "General",
                "Host settings shortcut",
                new KeyboardShortcut(KeyCode.F7),
                "Host-only shortcut that opens or closes the in-game Radar Missile Control panel."
            );

            SubscribeSettings();
            LoadDraftFromConfig();

            if (!SarhController.ReflectionReady)
            {
                Logger.LogError("Required Nuclear Option missile fields were not found. No patches were applied.");
                return;
            }

            SarhNetworkProtocol.RegisterSerializers();
            harmony = new Harmony(PluginGuid);
            harmony.PatchAll(typeof(Plugin).Assembly);
            Logger.LogInfo($"{PluginName} {PluginVersion} loaded. Active={CurrentActivePreset}.");
        }

        private void BindCurrentConfiguration()
        {
            activePreset = Config.Bind("General", "Active preset", ActivePreset.Vanilla,
                "F8 cycles Vanilla, Preset 1, and Preset 2. Only the host/server controls the active preset.");
            toggleShortcut = Config.Bind("General", "Cycle shortcut", new KeyboardShortcut(KeyCode.F8),
                "Host-only shortcut that cycles Vanilla, Preset 1, and Preset 2.");
            preset1 = BindPreset("Preset 1", true, true, true, false, false, false,
                50f, 50f, 50f, 50f, 50f, 4f);
            preset2 = BindPreset("Preset 2", true, true, true, false, false, false,
                0f, 0f, 0f, 0f, 0f, 0f);
            RemoveUnusedPresetSettings();
        }

        private void MigrateLegacyConfiguration()
        {
            ConfigEntry<LegacyMode> legacyHostMode = Config.Bind("General", "Host mode", LegacyMode.Nerfed, "Legacy setting.");
            Config.Bind("General", "Mode", legacyHostMode.Value, "Legacy setting.");
            ConfigEntry<KeyboardShortcut> legacyHostToggle = Config.Bind("General", "Host toggle shortcut", new KeyboardShortcut(KeyCode.F8), "Legacy setting.");
            Config.Bind("General", "Toggle shortcut", legacyHostToggle.Value, "Legacy setting.");
            ConfigEntry<bool> oldSarh = Config.Bind("Missile types", "Affect SARH", true, "Legacy setting.");
            ConfigEntry<bool> oldArh = Config.Bind("Missile types", "Affect ARH", true, "Legacy setting.");
            ConfigEntry<bool> oldAiPlayer = Config.Bind("Engagements", "AI to Player", true, "Legacy setting.");
            ConfigEntry<bool> oldAiAi = Config.Bind("Engagements", "AI to AI", false, "Legacy setting.");
            ConfigEntry<bool> oldPlayerAi = Config.Bind("Engagements", "Player to AI", false, "Legacy setting.");
            ConfigEntry<bool> oldPlayerPlayer = Config.Bind("Engagements", "Player to Player", false, "Legacy setting.");
            ConfigEntry<float> oldTracking = BindPercent("Nerfed mode", "Tracking strength", 50f, "Legacy setting.");
            ConfigEntry<float> oldLock = BindPercent("Nerfed mode", "Lock persistence", 50f, "Legacy setting.");
            ConfigEntry<float> oldJam = BindPercent("Nerfed mode", "Jam tolerance", 50f, "Legacy setting.");
            ConfigEntry<float> oldThrust = BindPercent("Nerfed mode", "Motor thrust", 50f, "Legacy setting.");
            ConfigEntry<float> oldSpeed = BindPercent("Nerfed mode", "Top speed", 50f, "Legacy setting.");
            ConfigEntry<float> oldAim = BindAimError("Nerfed mode", 4f);

            LegacyMode oldMode = legacyHostMode.Value;
            activePreset = Config.Bind("General", "Active preset",
                oldMode == LegacyMode.Vanilla ? ActivePreset.Vanilla :
                oldMode == LegacyMode.Disabled ? ActivePreset.Preset2 : ActivePreset.Preset1,
                "F8 cycles Vanilla, Preset 1, and Preset 2.");
            toggleShortcut = Config.Bind("General", "Cycle shortcut", legacyHostToggle.Value,
                "Host-only shortcut that cycles Vanilla, Preset 1, and Preset 2.");
            preset1 = BindPreset("Preset 1",
                true, true, true, false, false, false, 50f, 50f, 50f, 50f, 50f, 4f);
            preset2 = BindPreset("Preset 2",
                true, true, true, false, false, false, 0f, 0f, 0f, 0f, 0f, 0f);

            SetPreset(preset1,
                oldSarh.Value, oldArh.Value, oldAiPlayer.Value, oldAiAi.Value, oldPlayerAi.Value, oldPlayerPlayer.Value,
                oldTracking.Value, oldLock.Value, oldJam.Value, oldThrust.Value, oldSpeed.Value, oldAim.Value);
            SetPresetScope(preset2,
                oldSarh.Value, oldArh.Value, oldAiPlayer.Value, oldAiAi.Value, oldPlayerAi.Value, oldPlayerPlayer.Value);

            configurationVersion.Value = 1;
            RemoveLegacyConfiguration();
            Config.Save();
            Logger.LogInfo("Migrated v0.5.0 settings into Preset 1; Preset 2 keeps the same scope and uses editable defaults.");
        }

        private PresetConfig BindPreset(string section,
            bool sarh, bool arh, bool aiPlayer, bool aiAi, bool playerAi, bool playerPlayer,
            float tracking, float lockPersistence, float jam, float thrust, float speed, float aimError)
        {
            PresetConfig preset = new PresetConfig
            {
                AffectSarh = Config.Bind(section, "Affect SARH", sarh, "Affect semi-active radar-homing missiles."),
                AffectArh = Config.Bind(section, "Affect ARH", arh, "Affect active radar-homing missiles."),
                AiToPlayer = Config.Bind(section, "AI to Player", aiPlayer, "Affect AI-launched missiles targeting a player."),
                AiToAi = Config.Bind(section, "AI to AI", aiAi, "Affect AI-launched missiles targeting AI."),
                PlayerToAi = Config.Bind(section, "Player to AI", playerAi, "Affect player-launched missiles targeting AI."),
                PlayerToPlayer = Config.Bind(section, "Player to Player", playerPlayer, "Affect player-launched missiles targeting a player.")
            };
            preset.TrackingPercent = BindPercent(section, "Tracking strength", tracking, "Percent of vanilla tracking remaining.");
            preset.LockPercent = BindPercent(section, "Lock persistence", lockPersistence, "Percent of vanilla lock persistence remaining.");
            preset.JamPercent = BindPercent(section, "Jam tolerance", jam, "Percent of vanilla jam tolerance remaining.");
            preset.ThrustPercent = BindPercent(section, "Motor thrust", thrust, "Percent of vanilla motor thrust remaining.");
            preset.SpeedPercent = BindPercent(section, "Top speed", speed, "Percent of vanilla top-speed limit remaining.");
            preset.AimErrorPercent = BindAimError(section, aimError);
            return preset;
        }

        private ConfigEntry<float> BindPercent(string section, string key, float value, string description) =>
            Config.Bind(section, key, value, new ConfigDescription(description, new AcceptableValueRange<float>(0f, 100f)));

        private ConfigEntry<float> BindAimError(string section, float value) => Config.Bind(section,
            "Aim error percent of range", value, new ConfigDescription(
                "Moving aim error as a percentage of remaining range. Zero disables added error.",
                new AcceptableValueRange<float>(0f, 10f)));

        private static void SetPreset(PresetConfig preset,
            bool sarh, bool arh, bool aiPlayer, bool aiAi, bool playerAi, bool playerPlayer,
            float tracking, float lockPersistence, float jam, float thrust, float speed, float aimError)
        {
            SetPresetScope(preset, sarh, arh, aiPlayer, aiAi, playerAi, playerPlayer);
            preset.TrackingPercent.Value = tracking;
            preset.LockPercent.Value = lockPersistence;
            preset.JamPercent.Value = jam;
            preset.ThrustPercent.Value = thrust;
            preset.SpeedPercent.Value = speed;
            preset.AimErrorPercent.Value = aimError;
        }

        private static void SetPresetScope(PresetConfig preset,
            bool sarh, bool arh, bool aiPlayer, bool aiAi, bool playerAi, bool playerPlayer)
        {
            preset.AffectSarh.Value = sarh;
            preset.AffectArh.Value = arh;
            preset.AiToPlayer.Value = aiPlayer;
            preset.AiToAi.Value = aiAi;
            preset.PlayerToAi.Value = playerAi;
            preset.PlayerToPlayer.Value = playerPlayer;
        }

        private void RemoveLegacyConfiguration()
        {
            foreach (ConfigDefinition definition in new[]
            {
                new ConfigDefinition("General", "Host mode"), new ConfigDefinition("General", "Mode"),
                new ConfigDefinition("General", "Host toggle shortcut"), new ConfigDefinition("General", "Toggle shortcut"),
                new ConfigDefinition("Preset 1", "Effect"), new ConfigDefinition("Preset 2", "Effect"),
                new ConfigDefinition("Missile types", "Affect SARH"), new ConfigDefinition("Missile types", "Affect ARH"),
                new ConfigDefinition("Engagements", "AI to Player"), new ConfigDefinition("Engagements", "AI to AI"),
                new ConfigDefinition("Engagements", "Player to AI"), new ConfigDefinition("Engagements", "Player to Player"),
                new ConfigDefinition("Nerfed mode", "Tracking strength"), new ConfigDefinition("Nerfed mode", "Lock persistence"),
                new ConfigDefinition("Nerfed mode", "Jam tolerance"), new ConfigDefinition("Nerfed mode", "Motor thrust"),
                new ConfigDefinition("Nerfed mode", "Top speed"), new ConfigDefinition("Nerfed mode", "Aim error percent of range")
            }) Config.Remove(definition);
        }

        private void RemoveUnusedPresetSettings()
        {
            bool removedAny = false;
            foreach (ConfigDefinition definition in new[]
            {
                new ConfigDefinition("Preset 1", "Effect"), new ConfigDefinition("Preset 2", "Effect")
            }) removedAny |= Config.Remove(definition);
            if (removedAny) Config.Save();
        }

        private void Update()
        {
            EnsureNetworkRegistration();
            if (settingsVisible && !IsAuthoritativeHost)
            {
                SetSettingsVisible(false);
            }
            if (settingsVisible)
            {
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }

            if (panelShortcut != null && panelShortcut.Value.IsDown())
            {
                if (IsAuthoritativeHost)
                {
                    SetSettingsVisible(!settingsVisible);
                }
                else if (IsConnectedClient())
                {
                    ShowNotice("Only the host/server can open radar missile settings");
                }
                else
                {
                    ShowNotice("Start singleplayer or host a server to open radar missile settings");
                }
            }

            if (toggleShortcut == null || !toggleShortcut.Value.IsDown())
            {
                return;
            }

            if (IsAuthoritativeHost)
            {
                activePreset.Value = NextPreset(CurrentActivePreset);
                ShowNotice(BuildLocalStatusNotice());
            }
            else if (IsConnectedClient())
            {
                ShowNotice("Only the host/server can change the radar missile preset");
            }
            else
            {
                ShowNotice("Start singleplayer or host a server to change the radar missile preset");
            }
        }

        private static ActivePreset NextPreset(ActivePreset current)
        {
            switch (current)
            {
                case ActivePreset.Vanilla: return ActivePreset.Preset1;
                case ActivePreset.Preset1: return ActivePreset.Preset2;
                default: return ActivePreset.Vanilla;
            }
        }

        private static float ReadPercent(ConfigEntry<float> entry, float fallback)
        {
            return Mathf.Clamp(entry?.Value ?? fallback, 0f, 100f) * 0.01f;
        }

        private void OnGUI()
        {
            EnsureStyles();
            if (settingsVisible && IsAuthoritativeHost)
            {
                float windowHeight = Mathf.Min(700f, Mathf.Max(480f, Screen.height - 40f));
                settingsWindowRect.width = SettingsWindowWidth;
                settingsWindowRect.height = windowHeight;
                settingsWindowRect = GUILayout.Window(
                    SettingsWindowId,
                    settingsWindowRect,
                    DrawSettingsWindow,
                    "Radar Missile Control - Host Settings",
                    GUILayout.Width(SettingsWindowWidth),
                    GUILayout.Height(windowHeight)
                );
                settingsWindowRect.x = Mathf.Clamp(settingsWindowRect.x, 0f, Mathf.Max(0f, Screen.width - settingsWindowRect.width));
                settingsWindowRect.y = Mathf.Clamp(settingsWindowRect.y, 0f, Mathf.Max(0f, Screen.height - settingsWindowRect.height));
            }

            if (!string.IsNullOrEmpty(noticeText) && Time.unscaledTime < noticeUntil)
            {
                Vector2 size = noticeStyle.CalcSize(new GUIContent(noticeText));
                float width = Mathf.Min(size.x + 12f, Mathf.Max(120f, Screen.width - 32f));
                float x = (Screen.width - width) * 0.5f;
                Color oldColor = GUI.color;
                int oldDepth = GUI.depth;
                GUI.depth = -1000;
                GUI.color = Color.white;
                GUI.Label(new Rect(x, 14f, width, 24f), noticeText, noticeStyle);
                GUI.color = oldColor;
                GUI.depth = oldDepth;
            }
        }

        private void EnsureStyles()
        {
            if (noticeStyle != null)
            {
                return;
            }
            noticeStyle = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 14,
                fontStyle = FontStyle.Bold,
                wordWrap = false
            };
            noticeStyle.normal.textColor = Color.white;
            sectionStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 14,
                fontStyle = FontStyle.Bold
            };
            hintStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 11,
                wordWrap = true
            };
            hintStyle.normal.textColor = new Color(0.78f, 0.82f, 0.88f, 1f);
        }

        private void DrawSettingsWindow(int windowId)
        {
            settingsScroll = GUILayout.BeginScrollView(settingsScroll, false, true);
            GUILayout.Label("HOST / SERVER AUTHORITY", sectionStyle);
            GUILayout.Label($"ACTIVE: {ActivePresetLabel(CurrentActivePreset)}", sectionStyle);
            GUILayout.Label("F8 cycles VANILLA, PRESET 1, and PRESET 2. Edit one preset at a time below.", hintStyle);
            GUILayout.Space(8f);

            GUILayout.Label("Edit preset", sectionStyle);
            GUILayout.BeginHorizontal();
            DrawPresetTab(1, "PRESET 1");
            DrawPresetTab(2, "PRESET 2");
            GUILayout.EndHorizontal();
            GUILayout.Label("Changes are staged until Apply & Save. Apply or Revert before switching tabs.", hintStyle);

            GUILayout.Space(10f);
            GUILayout.Label("Affected missile types", sectionStyle);
            GUILayout.BeginHorizontal();
            draftAffectSarh = GUILayout.Toggle(draftAffectSarh, "SARH", GUILayout.Width(180f));
            draftAffectArh = GUILayout.Toggle(draftAffectArh, "ARH", GUILayout.Width(180f));
            GUILayout.EndHorizontal();

            GUILayout.Space(10f);
            GUILayout.Label("Affected engagements", sectionStyle);
            GUILayout.Label("Choose both who launched the missile and what it is targeting.", hintStyle);
            GUILayout.BeginHorizontal();
            GUILayout.Label("AI launcher", GUILayout.Width(130f));
            draftAiToPlayer = GUILayout.Toggle(draftAiToPlayer, "to Player", GUILayout.Width(150f));
            draftAiToAi = GUILayout.Toggle(draftAiToAi, "to AI", GUILayout.Width(150f));
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label("Player launcher", GUILayout.Width(130f));
            draftPlayerToPlayer = GUILayout.Toggle(draftPlayerToPlayer, "to Player", GUILayout.Width(150f));
            draftPlayerToAi = GUILayout.Toggle(draftPlayerToAi, "to AI", GUILayout.Width(150f));
            GUILayout.EndHorizontal();

            GUILayout.Space(10f);
            GUILayout.Label("Percent of vanilla remaining", sectionStyle);
            draftTrackingPercent = DrawSlider("Tracking strength", draftTrackingPercent, 0f, 100f, "%", 0);
            draftLockPercent = DrawSlider("Lock persistence", draftLockPercent, 0f, 100f, "%", 0);
            draftJamPercent = DrawSlider("Jam tolerance", draftJamPercent, 0f, 100f, "%", 0);
            draftThrustPercent = DrawSlider("Motor thrust", draftThrustPercent, 0f, 100f, "%", 0);
            draftSpeedPercent = DrawSlider("Top-speed limit", draftSpeedPercent, 0f, 100f, "%", 0);
            draftAimErrorPercent = DrawSlider("Aim error (% of range)", draftAimErrorPercent, 0f, 10f, "%", 1);
            GUILayout.Label("Both presets use these same settings. Warhead damage remains vanilla.", hintStyle);

            GUILayout.Space(12f);
            GUILayout.BeginHorizontal();
            bool oldEnabled = GUI.enabled;
            GUI.enabled = DraftDiffersFromConfig();
            if (GUILayout.Button("Apply & Save", GUILayout.Height(30f)))
            {
                ApplyDraft();
            }
            GUI.enabled = oldEnabled;
            if (GUILayout.Button("Revert", GUILayout.Height(30f)))
            {
                LoadDraftFromConfig();
            }
            if (GUILayout.Button("Defaults", GUILayout.Height(30f)))
            {
                LoadDefaultDraft();
            }
            if (GUILayout.Button("Close", GUILayout.Height(30f)))
            {
                SetSettingsVisible(false);
            }
            GUILayout.EndHorizontal();
            GUILayout.Label("F7 opens/closes this panel. F8 cycles VANILLA > PRESET 1 > PRESET 2.", hintStyle);
            GUILayout.EndScrollView();
            GUI.DragWindow(new Rect(0f, 0f, SettingsWindowWidth, 24f));
        }

        private void DrawPresetTab(int value, string label)
        {
            Color oldBackground = GUI.backgroundColor;
            if (editingPreset == value) GUI.backgroundColor = new Color(0.35f, 0.75f, 1f, 1f);
            if (GUILayout.Button(label, GUILayout.Height(30f)) && editingPreset != value)
            {
                if (DraftDiffersFromConfig()) ShowNotice("Apply or Revert before switching presets");
                else
                {
                    editingPreset = value;
                    LoadDraftFromConfig();
                }
            }
            GUI.backgroundColor = oldBackground;
        }

        private static float DrawSlider(string label, float value, float min, float max, string suffix, int decimals)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(190f));
            float next = GUILayout.HorizontalSlider(value, min, max, GUILayout.Width(220f));
            float scale = decimals == 0 ? 1f : Mathf.Pow(10f, decimals);
            next = Mathf.Round(next * scale) / scale;
            GUILayout.Label(next.ToString(decimals == 0 ? "0" : "0.0") + suffix, GUILayout.Width(60f));
            GUILayout.EndHorizontal();
            return next;
        }

        private void SetSettingsVisible(bool visible)
        {
            if (settingsVisible == visible)
            {
                return;
            }
            settingsVisible = visible;
            if (visible)
            {
                LoadDraftFromConfig();
                previousCursorLockMode = Cursor.lockState;
                previousCursorVisible = Cursor.visible;
                cursorStateCaptured = true;
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }
            else if (cursorStateCaptured)
            {
                Cursor.lockState = previousCursorLockMode;
                Cursor.visible = previousCursorVisible;
                cursorStateCaptured = false;
            }
        }

        private void LoadDraftFromConfig()
        {
            PresetConfig config = EditingConfig;
            draftAffectSarh = config.AffectSarh.Value;
            draftAffectArh = config.AffectArh.Value;
            draftAiToPlayer = config.AiToPlayer.Value;
            draftAiToAi = config.AiToAi.Value;
            draftPlayerToAi = config.PlayerToAi.Value;
            draftPlayerToPlayer = config.PlayerToPlayer.Value;
            draftTrackingPercent = config.TrackingPercent.Value;
            draftLockPercent = config.LockPercent.Value;
            draftJamPercent = config.JamPercent.Value;
            draftThrustPercent = config.ThrustPercent.Value;
            draftSpeedPercent = config.SpeedPercent.Value;
            draftAimErrorPercent = config.AimErrorPercent.Value;
        }

        private PresetConfig EditingConfig => editingPreset == 2 ? preset2 : preset1;

        private void LoadDefaultDraft()
        {
            draftAffectSarh = true;
            draftAffectArh = true;
            draftAiToPlayer = true;
            draftAiToAi = false;
            draftPlayerToAi = false;
            draftPlayerToPlayer = false;
            bool preset2Defaults = editingPreset == 2;
            draftTrackingPercent = preset2Defaults ? 0f : 50f;
            draftLockPercent = preset2Defaults ? 0f : 50f;
            draftJamPercent = preset2Defaults ? 0f : 50f;
            draftThrustPercent = preset2Defaults ? 0f : 50f;
            draftSpeedPercent = preset2Defaults ? 0f : 50f;
            draftAimErrorPercent = preset2Defaults ? 0f : 4f;
        }

        private bool DraftDiffersFromConfig()
        {
            PresetConfig config = EditingConfig;
            return draftAffectSarh != config.AffectSarh.Value ||
                   draftAffectArh != config.AffectArh.Value ||
                   draftAiToPlayer != config.AiToPlayer.Value ||
                   draftAiToAi != config.AiToAi.Value ||
                   draftPlayerToAi != config.PlayerToAi.Value ||
                   draftPlayerToPlayer != config.PlayerToPlayer.Value ||
                   !Mathf.Approximately(draftTrackingPercent, config.TrackingPercent.Value) ||
                   !Mathf.Approximately(draftLockPercent, config.LockPercent.Value) ||
                   !Mathf.Approximately(draftJamPercent, config.JamPercent.Value) ||
                   !Mathf.Approximately(draftThrustPercent, config.ThrustPercent.Value) ||
                   !Mathf.Approximately(draftSpeedPercent, config.SpeedPercent.Value) ||
                   !Mathf.Approximately(draftAimErrorPercent, config.AimErrorPercent.Value);
        }

        private void ApplyDraft()
        {
            applyingSettings = true;
            PresetConfig config = EditingConfig;
            try
            {
                config.AffectSarh.Value = draftAffectSarh;
                config.AffectArh.Value = draftAffectArh;
                config.AiToPlayer.Value = draftAiToPlayer;
                config.AiToAi.Value = draftAiToAi;
                config.PlayerToAi.Value = draftPlayerToAi;
                config.PlayerToPlayer.Value = draftPlayerToPlayer;
                config.TrackingPercent.Value = draftTrackingPercent;
                config.LockPercent.Value = draftLockPercent;
                config.JamPercent.Value = draftJamPercent;
                config.ThrustPercent.Value = draftThrustPercent;
                config.SpeedPercent.Value = draftSpeedPercent;
                config.AimErrorPercent.Value = draftAimErrorPercent;
            }
            finally
            {
                applyingSettings = false;
            }
            Config.Save();
            LoadDraftFromConfig();
            Logger.LogMessage($"Preset {editingPreset} saved.");
        }

        private static string ActivePresetLabel(ActivePreset value)
        {
            switch (value)
            {
                case ActivePreset.Preset1: return "PRESET 1";
                case ActivePreset.Preset2: return "PRESET 2";
                default: return "VANILLA";
            }
        }

        private static byte CurrentSeekerMask
        {
            get
            {
                return SeekerMask(CurrentPreset);
            }
        }

        private static byte CurrentEngagementMask
        {
            get
            {
                return EngagementMask(CurrentPreset);
            }
        }

        private static byte SeekerMask(PresetConfig config)
        {
            byte mask = 0;
            if (config?.AffectSarh.Value ?? true) mask |= (byte)RadarSeekerKind.Sarh;
            if (config?.AffectArh.Value ?? true) mask |= (byte)RadarSeekerKind.Arh;
            return mask;
        }

        private static byte EngagementMask(PresetConfig config)
        {
            byte mask = 0;
            if (config?.AiToPlayer.Value ?? true) mask |= 1;
            if (config?.AiToAi.Value ?? false) mask |= 2;
            if (config?.PlayerToAi.Value ?? false) mask |= 4;
            if (config?.PlayerToPlayer.Value ?? false) mask |= 8;
            return mask;
        }

        private static string BuildLocalStatusNotice()
        {
            return $"RADAR CONTROL: {ActivePresetLabel(CurrentActivePreset)}";
        }

        private static string BuildRemoteStatusNotice(SarhStatusMessage message)
        {
            ActivePreset selected = (ActivePreset)message.ActivePreset;
            return $"RADAR CONTROL: {ActivePresetLabel(selected)}";
        }

        private void ShowNotice(string text)
        {
            noticeText = text;
            noticeUntil = Time.unscaledTime + 3.5f;
            Logger.LogMessage(text);
        }

        private void SubscribeSettings()
        {
            Subscribe(activePreset);
            SubscribePreset(preset1);
            SubscribePreset(preset2);
        }

        private void UnsubscribeSettings()
        {
            Unsubscribe(activePreset);
            UnsubscribePreset(preset1);
            UnsubscribePreset(preset2);
        }

        private void SubscribePreset(PresetConfig config)
        {
            Subscribe(config.AffectSarh); Subscribe(config.AffectArh);
            Subscribe(config.AiToPlayer); Subscribe(config.AiToAi); Subscribe(config.PlayerToAi); Subscribe(config.PlayerToPlayer);
            Subscribe(config.TrackingPercent); Subscribe(config.LockPercent); Subscribe(config.JamPercent);
            Subscribe(config.ThrustPercent); Subscribe(config.SpeedPercent); Subscribe(config.AimErrorPercent);
        }

        private void UnsubscribePreset(PresetConfig config)
        {
            Unsubscribe(config?.AffectSarh); Unsubscribe(config?.AffectArh);
            Unsubscribe(config?.AiToPlayer); Unsubscribe(config?.AiToAi); Unsubscribe(config?.PlayerToAi); Unsubscribe(config?.PlayerToPlayer);
            Unsubscribe(config?.TrackingPercent); Unsubscribe(config?.LockPercent); Unsubscribe(config?.JamPercent);
            Unsubscribe(config?.ThrustPercent); Unsubscribe(config?.SpeedPercent); Unsubscribe(config?.AimErrorPercent);
        }

        private void Subscribe<T>(ConfigEntry<T> entry)
        {
            if (entry != null) entry.SettingChanged += OnSettingsChanged;
        }

        private void Unsubscribe<T>(ConfigEntry<T> entry)
        {
            if (entry != null) entry.SettingChanged -= OnSettingsChanged;
        }

        private void OnSettingsChanged(object sender, EventArgs args)
        {
            if (applyingSettings)
            {
                return;
            }
            Config.Save();
            if (settingsVisible && !ReferenceEquals(sender, activePreset))
            {
                LoadDraftFromConfig();
            }
            Logger.LogMessage($"Configured host radar missile settings changed: {BuildLocalStatusNotice()}.");
            if (IsAuthoritativeHost && ReferenceEquals(sender, activePreset))
            {
                BroadcastStatus();
            }
        }

        private static bool IsConnectedClient()
        {
            NetworkManagerNuclearOption manager = NetworkManagerNuclearOption.i;
            return manager != null && manager.Client != null && manager.Client.Active && !IsAuthoritativeHost;
        }

        private void EnsureNetworkRegistration()
        {
            NetworkManagerNuclearOption manager = NetworkManagerNuclearOption.i;
            if (manager == null)
            {
                return;
            }

            MessageHandler nextServerHandler = manager.Server?.MessageHandler;
            if (nextServerHandler != serverHandler)
            {
                UnregisterServerHandler();
                if (nextServerHandler != null)
                {
                    ((IMessageReceiver)nextServerHandler).RegisterHandler<SarhHelloMessage>(HandleHello, false);
                    serverHandler = nextServerHandler;
                }
            }

            MessageHandler nextClientHandler = manager.Client?.MessageHandler;
            if (nextClientHandler != clientHandler)
            {
                UnregisterClientHandler();
                if (nextClientHandler != null)
                {
                    ((IMessageReceiver)nextClientHandler).RegisterHandler<SarhStatusMessage>(HandleStatus, false);
                    clientHandler = nextClientHandler;
                    registeredClient = manager.Client;
                    registeredClient.Authenticated.AddListener(OnClientAuthenticated);
                    synchronizedActivePreset = null;
                }
            }
        }

        private void OnClientAuthenticated(INetworkPlayer player)
        {
            if (player == null || player.IsHost || IsAuthoritativeHost)
            {
                return;
            }
            synchronizedActivePreset = null;
            NetworkManagerNuclearOption.i.Client.Send(new SarhHelloMessage
            {
                ProtocolVersion = ProtocolVersion
            });
        }

        private void HandleHello(INetworkPlayer player, SarhHelloMessage message)
        {
            if (!IsAuthoritativeHost || player == null || message.ProtocolVersion != ProtocolVersion)
            {
                return;
            }
            moddedClients.Add(player);
            SendStatus(player);
            Logger.LogDebug($"Registered Radar Missile Control client {player}.");
        }

        private void HandleStatus(INetworkPlayer player, SarhStatusMessage message)
        {
            if (message.ProtocolVersion != ProtocolVersion ||
                message.ActivePreset > (byte)ActivePreset.Preset2)
            {
                return;
            }
            bool changed = synchronizedActivePreset.HasValue &&
                           synchronizedActivePreset.Value != message.ActivePreset;
            synchronizedActivePreset = message.ActivePreset;
            if (changed)
            {
                ShowNotice(BuildRemoteStatusNotice(message));
            }
        }

        private void BroadcastStatus()
        {
            moddedClients.RemoveWhere(player => player == null || !player.IsConnected || !player.IsAuthenticated);
            foreach (INetworkPlayer player in moddedClients)
            {
                SendStatus(player);
            }
        }

        private static void SendStatus(INetworkPlayer player)
        {
            if (player == null || !player.IsConnected || !player.IsAuthenticated)
            {
                return;
            }
            player.Send(new SarhStatusMessage
            {
                ProtocolVersion = ProtocolVersion,
                ActivePreset = (byte)CurrentActivePreset
            });
        }

        private void UnregisterClientHandler()
        {
            if (registeredClient != null)
            {
                registeredClient.Authenticated.RemoveListener(OnClientAuthenticated);
            }
            if (clientHandler != null)
            {
                ((IMessageReceiver)clientHandler).UnregisterHandler<SarhStatusMessage>();
            }
            registeredClient = null;
            clientHandler = null;
        }

        private void UnregisterServerHandler()
        {
            if (serverHandler != null)
            {
                ((IMessageReceiver)serverHandler).UnregisterHandler<SarhHelloMessage>();
            }
            serverHandler = null;
            moddedClients.Clear();
        }

        private void OnDestroy()
        {
            SetSettingsVisible(false);
            UnsubscribeSettings();
            UnregisterClientHandler();
            UnregisterServerHandler();
            harmony?.UnpatchSelf();
        }

        internal static bool IsHumanControlled(Unit unit)
        {
            Aircraft aircraft = unit as Aircraft;
            return aircraft != null && aircraft.Player != null;
        }

        internal static RadarSeekerKind GetSeekerKind(MissileSeeker seeker)
        {
            if (seeker is SARHSeeker) return RadarSeekerKind.Sarh;
            if (seeker is ARHSeeker) return RadarSeekerKind.Arh;
            return RadarSeekerKind.None;
        }

        internal static bool ShouldAffect(RadarSeekerKind kind, Unit owner, Unit target)
        {
            if (owner == null || target == null)
            {
                return false;
            }
            return ShouldAffect(kind, IsHumanControlled(owner), IsHumanControlled(target));
        }

        internal static bool ShouldAffect(RadarSeekerKind kind, bool ownerIsHuman, bool targetIsHuman)
        {
            if (kind == RadarSeekerKind.None || ((byte)kind & CurrentSeekerMask) == 0)
            {
                return false;
            }
            byte route = ownerIsHuman
                ? (byte)(targetIsHuman ? 8 : 4)
                : (byte)(targetIsHuman ? 1 : 2);
            return (CurrentEngagementMask & route) != 0;
        }

    }

    internal static class SarhNetworkProtocol
    {
        private static bool registered;

        internal static void RegisterSerializers()
        {
            if (registered)
            {
                return;
            }
            SetSerializer<SarhHelloMessage>(WriteHello, ReadHello);
            SetSerializer<SarhStatusMessage>(WriteStatus, ReadStatus);
            MessagePacker.RegisterMessage<SarhHelloMessage>();
            MessagePacker.RegisterMessage<SarhStatusMessage>();
            registered = true;
        }

        private static void SetSerializer<T>(Action<NetworkWriter, T> writer, Func<NetworkReader, T> reader)
        {
            PropertyInfo writeProperty = typeof(Writer<T>).GetProperty("Write", BindingFlags.Public | BindingFlags.Static);
            PropertyInfo readProperty = typeof(Reader<T>).GetProperty("Read", BindingFlags.Public | BindingFlags.Static);
            writeProperty.GetSetMethod(true).Invoke(null, new object[] { writer });
            readProperty.GetSetMethod(true).Invoke(null, new object[] { reader });
        }

        private static void WriteHello(NetworkWriter writer, SarhHelloMessage message)
        {
            writer.WriteByte(message.ProtocolVersion);
        }

        private static SarhHelloMessage ReadHello(NetworkReader reader)
        {
            return new SarhHelloMessage { ProtocolVersion = reader.ReadByte() };
        }

        private static void WriteStatus(NetworkWriter writer, SarhStatusMessage message)
        {
            writer.WriteByte(message.ProtocolVersion);
            writer.WriteByte(message.ActivePreset);
        }

        private static SarhStatusMessage ReadStatus(NetworkReader reader)
        {
            return new SarhStatusMessage
            {
                ProtocolVersion = reader.ReadByte(),
                ActivePreset = reader.ReadByte()
            };
        }
    }

    internal static class SarhController
    {
        private sealed class MissileState
        {
            internal MissileSeeker Seeker;
            internal RadarSeekerKind Kind;
            internal bool OwnerIsHuman;
            internal bool TargetIsHuman;
            internal FieldInfo PersistenceField;
            internal FieldInfo JamToleranceField;
            internal float OriginalLockPersistence;
            internal float OriginalJamTolerance;
            internal Array Motors;
            internal float[] OriginalThrust;
            internal float[] OriginalTopSpeed;
            internal bool NerfApplied;
        }

        private static readonly FieldInfo SeekerMissileField = AccessTools.Field(typeof(MissileSeeker), "missile");
        private static readonly FieldInfo SeekerTargetField = AccessTools.Field(typeof(MissileSeeker), "targetUnit");
        private static readonly FieldInfo SarhLockPersistenceField = AccessTools.Field(typeof(SARHSeeker), "lockPersistence");
        private static readonly FieldInfo SarhJamToleranceField = AccessTools.Field(typeof(SARHSeeker), "jamTolerance");
        private static readonly FieldInfo ArhLockPersistenceField = AccessTools.Field(typeof(ARHSeeker), "lockPerseverance");
        private static readonly FieldInfo ArhJamToleranceField = AccessTools.Field(typeof(ARHSeeker), "jamTolerance");
        private static readonly FieldInfo ArhLastActiveTrackAttemptField = AccessTools.Field(typeof(ARHSeeker), "lastActiveTrackAttempt");
        private static readonly FieldInfo MotorsField = AccessTools.Field(typeof(Missile), "motors");
        private static readonly Type MotorType = typeof(Missile).GetNestedType("Motor", BindingFlags.NonPublic);
        private static readonly FieldInfo MotorThrustField = AccessTools.Field(MotorType, "thrust");
        private static readonly FieldInfo MotorTopSpeedField = AccessTools.Field(MotorType, "topSpeed");
        private static readonly ConditionalWeakTable<Missile, MissileState> States = new ConditionalWeakTable<Missile, MissileState>();

        internal static bool ReflectionReady =>
            SeekerMissileField != null && SeekerTargetField != null &&
            SarhLockPersistenceField != null && SarhJamToleranceField != null &&
            ArhLockPersistenceField != null && ArhJamToleranceField != null &&
            ArhLastActiveTrackAttemptField != null &&
            MotorsField != null &&
            MotorType != null && MotorThrustField != null && MotorTopSpeedField != null;

        internal static void Register(MissileSeeker seeker, Unit target)
        {
            if (!Plugin.IsAuthoritativeHost)
            {
                return;
            }

            FieldInfo persistenceField;
            FieldInfo jamToleranceField;
            if (seeker is SARHSeeker)
            {
                persistenceField = SarhLockPersistenceField;
                jamToleranceField = SarhJamToleranceField;
            }
            else if (seeker is ARHSeeker)
            {
                persistenceField = ArhLockPersistenceField;
                jamToleranceField = ArhJamToleranceField;
            }
            else
            {
                return;
            }

            Missile missile = GetMissile(seeker);
            if (missile == null || States.TryGetValue(missile, out _))
            {
                return;
            }
            Array motors = MotorsField.GetValue(missile) as Array;
            int motorCount = motors?.Length ?? 0;
            float[] originalThrust = new float[motorCount];
            float[] originalTopSpeed = new float[motorCount];
            for (int i = 0; i < motorCount; i++)
            {
                object motor = motors.GetValue(i);
                if (motor != null)
                {
                    originalThrust[i] = (float)MotorThrustField.GetValue(motor);
                    originalTopSpeed[i] = (float)MotorTopSpeedField.GetValue(motor);
                }
            }
            States.Add(missile, new MissileState
            {
                Seeker = seeker,
                Kind = Plugin.GetSeekerKind(seeker),
                OwnerIsHuman = Plugin.IsHumanControlled(missile.owner),
                TargetIsHuman = Plugin.IsHumanControlled(target),
                PersistenceField = persistenceField,
                JamToleranceField = jamToleranceField,
                OriginalLockPersistence = (float)persistenceField.GetValue(seeker),
                OriginalJamTolerance = (float)jamToleranceField.GetValue(seeker),
                Motors = motors,
                OriginalThrust = originalThrust,
                OriginalTopSpeed = originalTopSpeed
            });
        }

        internal static void ApplyCurrentPreset(MissileSeeker seeker)
        {
            if (!Plugin.IsAuthoritativeHost)
            {
                return;
            }
            Missile missile = GetMissile(seeker);
            if (missile == null || !States.TryGetValue(missile, out MissileState state))
            {
                return;
            }

            Unit currentTarget = SeekerTargetField.GetValue(state.Seeker) as Unit;
            if (currentTarget != null)
            {
                state.TargetIsHuman = Plugin.IsHumanControlled(currentTarget);
            }

            if (!Plugin.PresetActive || !IsAffected(state))
            {
                if (state.NerfApplied)
                {
                    Restore(state);
                    state.NerfApplied = false;
                }
                return;
            }

            ApplyFlightNerf(state);
            state.NerfApplied = true;
        }

        internal static bool ShouldNerf(MissileSeeker seeker)
        {
            if (!Plugin.IsAuthoritativeHost || !Plugin.PresetActive)
            {
                return false;
            }
            Missile missile = GetMissile(seeker);
            return missile != null &&
                   States.TryGetValue(missile, out MissileState state) &&
                   IsAffected(state);
        }

        internal static bool IsFreshArhRadarScan(ARHSeeker seeker)
        {
            return ShouldNerf(seeker) &&
                   Time.timeSinceLevelLoad - (float)ArhLastActiveTrackAttemptField.GetValue(seeker) >= 0.5f;
        }

        internal static void ApplyAimError(Missile missile, ref GlobalPosition aimPoint)
        {
            if (!Plugin.IsAuthoritativeHost ||
                !Plugin.PresetActive ||
                missile == null ||
                !States.TryGetValue(missile, out MissileState state) ||
                !IsAffected(state))
            {
                return;
            }

            Vector3 toAimpoint = aimPoint - missile.GlobalPosition();
            float remainingRange = toAimpoint.magnitude;
            if (remainingRange < 1f)
            {
                return;
            }

            float errorFraction = Plugin.AimErrorFraction;
            if (errorFraction <= 0f)
            {
                return;
            }
            float errorRadius = Mathf.Clamp(remainingRange * errorFraction, 25f, 400f);
            float phase = Time.timeSinceLevelLoad * 2.2f + missile.GetInstanceID() * 0.017f;
            Vector3 errorDirection =
                (missile.transform.right * Mathf.Sin(phase) +
                 missile.transform.up * Mathf.Cos(phase * 0.73f)).normalized;
            aimPoint += errorDirection * errorRadius;
        }

        private static void ApplyFlightNerf(MissileState state)
        {
            state.PersistenceField.SetValue(state.Seeker, state.OriginalLockPersistence * Plugin.LockMultiplier);
            state.JamToleranceField.SetValue(state.Seeker, state.OriginalJamTolerance * Plugin.JamMultiplier);
            int motorCount = state.Motors?.Length ?? 0;
            for (int i = 0; i < motorCount; i++)
            {
                object motor = state.Motors.GetValue(i);
                if (motor != null)
                {
                    MotorThrustField.SetValue(motor, state.OriginalThrust[i] * Plugin.ThrustMultiplier);
                    MotorTopSpeedField.SetValue(motor, state.OriginalTopSpeed[i] * Plugin.SpeedMultiplier);
                }
            }
        }

        private static bool IsAffected(MissileState state)
        {
            return state != null && Plugin.ShouldAffect(state.Kind, state.OwnerIsHuman, state.TargetIsHuman);
        }

        private static void Restore(MissileState state)
        {
            state.PersistenceField.SetValue(state.Seeker, state.OriginalLockPersistence);
            state.JamToleranceField.SetValue(state.Seeker, state.OriginalJamTolerance);
            int motorCount = state.Motors?.Length ?? 0;
            for (int i = 0; i < motorCount; i++)
            {
                object motor = state.Motors.GetValue(i);
                if (motor != null)
                {
                    MotorThrustField.SetValue(motor, state.OriginalThrust[i]);
                    MotorTopSpeedField.SetValue(motor, state.OriginalTopSpeed[i]);
                }
            }
        }

        private static Missile GetMissile(MissileSeeker seeker)
        {
            return seeker == null ? null : SeekerMissileField.GetValue(seeker) as Missile;
        }
    }

    [HarmonyPatch(typeof(SARHSeeker), nameof(SARHSeeker.Initialize))]
    internal static class SarhInitializePatch
    {
        private static void Postfix(SARHSeeker __instance, Unit target)
        {
            SarhController.Register(__instance, target);
            SarhController.ApplyCurrentPreset(__instance);
        }
    }

    [HarmonyPatch(typeof(SARHSeeker), nameof(SARHSeeker.Seek))]
    internal static class SarhSeekPatch
    {
        private static void Prefix(SARHSeeker __instance)
        {
            SarhController.ApplyCurrentPreset(__instance);
        }
    }

    [HarmonyPatch(typeof(ARHSeeker), nameof(ARHSeeker.Initialize))]
    internal static class ArhInitializePatch
    {
        private static void Postfix(ARHSeeker __instance, Unit target)
        {
            SarhController.Register(__instance, target);
            SarhController.ApplyCurrentPreset(__instance);
        }
    }

    [HarmonyPatch(typeof(ARHSeeker), nameof(ARHSeeker.Seek))]
    internal static class ArhSeekPatch
    {
        private static void Prefix(ARHSeeker __instance)
        {
            SarhController.ApplyCurrentPreset(__instance);
        }
    }

    [HarmonyPatch(typeof(SARHSeeker), "GetTrackingStrength")]
    internal static class SarhTrackingStrengthPatch
    {
        private static void Postfix(SARHSeeker __instance, ref float __result)
        {
            if (SarhController.ShouldNerf(__instance))
            {
                __result *= Plugin.TrackingMultiplier;
            }
        }
    }

    [HarmonyPatch(typeof(ARHSeeker), "GetRadarReturn")]
    internal static class ArhTrackingStrengthPatch
    {
        private static void Prefix(ARHSeeker __instance, out bool __state)
        {
            __state = SarhController.IsFreshArhRadarScan(__instance);
        }

        private static void Postfix(bool __state, ref float __result)
        {
            if (__state && __result > 0f)
            {
                __result *= Plugin.TrackingMultiplier;
            }
        }
    }

    [HarmonyPatch(typeof(Missile), nameof(Missile.SetAimpoint))]
    internal static class RadarMissileAimpointPatch
    {
        private static void Prefix(Missile __instance, ref GlobalPosition aimPoint)
        {
            SarhController.ApplyAimError(__instance, ref aimPoint);
        }
    }

}
