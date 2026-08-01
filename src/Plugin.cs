using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Mirage;
using Mirage.Serialization;
using NuclearOption.Networking;
using UnityEngine;

namespace Blacknight2u.SARHControl
{
    public enum SarhMode
    {
        Vanilla = 0,
        Nerfed = 1,
        [Obsolete("Legacy configuration name; use Nerfed.")]
        Nerf50 = Nerfed,
        [Obsolete("Legacy configuration name; use Nerfed.")]
        Nerf80 = Nerfed,
        Disabled = 2
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
        internal byte Mode;
        internal byte SeekerMask;
        internal byte EngagementMask;
        internal byte TrackingPercent;
        internal byte LockPercent;
        internal byte JamPercent;
        internal byte ThrustPercent;
        internal byte SpeedPercent;
        internal byte AimErrorTenths;
    }

    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    [BepInProcess("NuclearOption.exe")]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "blacknight2u.sarhcontrol";
        public const string PluginName = "Radar Missile Control";
        public const string PluginVersion = "0.5.0";

        private const byte ProtocolVersion = 4;
        private const float NoticeWidth = 620f;
        private const float SettingsWindowWidth = 520f;
        private const int SettingsWindowId = 5393747;
        private static ConfigEntry<SarhMode> mode;
        private static ConfigEntry<KeyboardShortcut> toggleShortcut;
        private static ConfigEntry<KeyboardShortcut> panelShortcut;
        private static ConfigEntry<bool> affectSarh;
        private static ConfigEntry<bool> affectArh;
        private static ConfigEntry<bool> affectAiToPlayer;
        private static ConfigEntry<bool> affectAiToAi;
        private static ConfigEntry<bool> affectPlayerToAi;
        private static ConfigEntry<bool> affectPlayerToPlayer;
        private static ConfigEntry<float> trackingPercent;
        private static ConfigEntry<float> lockPercent;
        private static ConfigEntry<float> jamPercent;
        private static ConfigEntry<float> thrustPercent;
        private static ConfigEntry<float> speedPercent;
        private static ConfigEntry<float> aimErrorPercent;
        private readonly HashSet<INetworkPlayer> moddedClients = new HashSet<INetworkPlayer>();
        private Harmony harmony;
        private MessageHandler clientHandler;
        private MessageHandler serverHandler;
        private NetworkClient registeredClient;
        private string synchronizedStatusSignature;
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

        private SarhMode draftMode;
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

        internal static ManualLogSource Log { get; private set; }
        internal static SarhMode CurrentMode => mode?.Value ?? SarhMode.Vanilla;
        internal static float TrackingMultiplier => ReadPercent(trackingPercent, 50f);
        internal static float LockMultiplier => ReadPercent(lockPercent, 50f);
        internal static float JamMultiplier => ReadPercent(jamPercent, 50f);
        internal static float ThrustMultiplier => ReadPercent(thrustPercent, 50f);
        internal static float SpeedMultiplier => ReadPercent(speedPercent, 50f);
        internal static float AimErrorFraction => Mathf.Clamp((aimErrorPercent?.Value ?? 4f) / 100f, 0f, 0.1f);

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
            Log = Logger;
            mode = Config.Bind(
                "General",
                "Host mode",
                SarhMode.Nerfed,
                "Used only by the host or dedicated server. Nerfed uses the configurable guidance, accuracy, thrust, and speed values. Warhead damage remains vanilla."
            );
            toggleShortcut = Config.Bind(
                "General",
                "Host toggle shortcut",
                new KeyboardShortcut(KeyCode.F8),
                "Host-only shortcut that cycles Nerfed, Disabled, and Vanilla/off. Clients cannot change the server mode."
            );
            panelShortcut = Config.Bind(
                "General",
                "Host settings shortcut",
                new KeyboardShortcut(KeyCode.F7),
                "Host-only shortcut that opens or closes the in-game Radar Missile Control panel."
            );

            affectSarh = Config.Bind("Missile types", "Affect SARH", true, "Apply the selected mode to semi-active radar-homing missiles.");
            affectArh = Config.Bind("Missile types", "Affect ARH", true, "Apply the selected mode to active radar-homing missiles.");

            affectAiToPlayer = Config.Bind("Engagements", "AI to Player", true, "Affect AI-launched missiles targeting a human-controlled aircraft.");
            affectAiToAi = Config.Bind("Engagements", "AI to AI", false, "Affect AI-launched missiles targeting AI units.");
            affectPlayerToAi = Config.Bind("Engagements", "Player to AI", false, "Affect player-launched missiles targeting AI units.");
            affectPlayerToPlayer = Config.Bind("Engagements", "Player to Player", false, "Affect player-launched missiles targeting another human-controlled aircraft.");

            trackingPercent = BindPercent("Tracking strength", 50f, "Radar tracking strength remaining in Nerfed mode.");
            lockPercent = BindPercent("Lock persistence", 50f, "Seeker lock persistence remaining in Nerfed mode.");
            jamPercent = BindPercent("Jam tolerance", 50f, "Seeker resistance to jamming remaining in Nerfed mode.");
            thrustPercent = BindPercent("Motor thrust", 50f, "Missile motor thrust remaining in Nerfed mode.");
            speedPercent = BindPercent("Top speed", 50f, "Each missile motor's top-speed limit remaining in Nerfed mode.");
            aimErrorPercent = Config.Bind(
                "Nerfed mode",
                "Aim error percent of range",
                4f,
                new ConfigDescription(
                    "Moving aim error as a percentage of remaining range, clamped to 25-400 meters. Zero disables added aim error.",
                    new AcceptableValueRange<float>(0f, 10f))
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
            Logger.LogInfo($"{PluginName} {PluginVersion} loaded. HostMode={CurrentMode}.");
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
                mode.Value = NextMode(CurrentMode);
                Config.Save();
                draftMode = CurrentMode;
                ShowNotice(BuildLocalStatusNotice());
            }
            else if (IsConnectedClient())
            {
                ShowNotice("Only the host/server can change radar missile mode");
            }
            else
            {
                ShowNotice("Start singleplayer or host a server to change radar missile mode");
            }
        }

        private static SarhMode NextMode(SarhMode current)
        {
            switch (current)
            {
                case SarhMode.Nerfed: return SarhMode.Disabled;
                case SarhMode.Disabled: return SarhMode.Vanilla;
                default: return SarhMode.Nerfed;
            }
        }

        private ConfigEntry<float> BindPercent(string key, float defaultValue, string description)
        {
            return Config.Bind(
                "Nerfed mode",
                key,
                defaultValue,
                new ConfigDescription(description, new AcceptableValueRange<float>(0f, 100f))
            );
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
                float width = Mathf.Min(NoticeWidth, Mathf.Max(280f, Screen.width - 32f));
                float x = Mathf.Max(10f, Screen.width - width - 16f);
                Color oldColor = GUI.color;
                int oldDepth = GUI.depth;
                GUI.depth = -1000;
                GUI.color = Color.white;
                GUI.Box(new Rect(x, 14f, width, 42f), noticeText, noticeStyle);
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
            noticeStyle = new GUIStyle(GUI.skin.box)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 14,
                fontStyle = FontStyle.Bold,
                wordWrap = true
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
            GUILayout.Label("Changes below are staged. Press Apply & Save to change live gameplay and notify modded clients.", hintStyle);
            GUILayout.Space(8f);

            GUILayout.Label("Mode", sectionStyle);
            GUILayout.BeginHorizontal();
            DrawModeButton(SarhMode.Vanilla, "OFF (VANILLA)");
            DrawModeButton(SarhMode.Nerfed, "NERFED");
            DrawModeButton(SarhMode.Disabled, "DISABLED");
            GUILayout.EndHorizontal();

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
            GUILayout.Label("Nerfed mode - percent of vanilla remaining", sectionStyle);
            draftTrackingPercent = DrawSlider("Tracking strength", draftTrackingPercent, 0f, 100f, "%", 0);
            draftLockPercent = DrawSlider("Lock persistence", draftLockPercent, 0f, 100f, "%", 0);
            draftJamPercent = DrawSlider("Jam tolerance", draftJamPercent, 0f, 100f, "%", 0);
            draftThrustPercent = DrawSlider("Motor thrust", draftThrustPercent, 0f, 100f, "%", 0);
            draftSpeedPercent = DrawSlider("Top-speed limit", draftSpeedPercent, 0f, 100f, "%", 0);
            draftAimErrorPercent = DrawSlider("Aim error (% of range)", draftAimErrorPercent, 0f, 10f, "%", 1);
            GUILayout.Label("Aim error is clamped to 25-400 m. Warhead damage remains 100% vanilla in Nerfed mode.", hintStyle);

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
            GUILayout.Label("F7 opens/closes this panel. F8 still cycles the three modes.", hintStyle);
            GUILayout.EndScrollView();
            GUI.DragWindow(new Rect(0f, 0f, SettingsWindowWidth, 24f));
        }

        private void DrawModeButton(SarhMode value, string label)
        {
            Color oldBackground = GUI.backgroundColor;
            if (draftMode == value)
            {
                GUI.backgroundColor = new Color(0.35f, 0.75f, 1f, 1f);
            }
            if (GUILayout.Button(label, GUILayout.Height(30f)))
            {
                draftMode = value;
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
            draftMode = CurrentMode;
            draftAffectSarh = affectSarh?.Value ?? true;
            draftAffectArh = affectArh?.Value ?? true;
            draftAiToPlayer = affectAiToPlayer?.Value ?? true;
            draftAiToAi = affectAiToAi?.Value ?? false;
            draftPlayerToAi = affectPlayerToAi?.Value ?? false;
            draftPlayerToPlayer = affectPlayerToPlayer?.Value ?? false;
            draftTrackingPercent = trackingPercent?.Value ?? 50f;
            draftLockPercent = lockPercent?.Value ?? 50f;
            draftJamPercent = jamPercent?.Value ?? 50f;
            draftThrustPercent = thrustPercent?.Value ?? 50f;
            draftSpeedPercent = speedPercent?.Value ?? 50f;
            draftAimErrorPercent = aimErrorPercent?.Value ?? 4f;
        }

        private void LoadDefaultDraft()
        {
            draftMode = SarhMode.Nerfed;
            draftAffectSarh = true;
            draftAffectArh = true;
            draftAiToPlayer = true;
            draftAiToAi = false;
            draftPlayerToAi = false;
            draftPlayerToPlayer = false;
            draftTrackingPercent = 50f;
            draftLockPercent = 50f;
            draftJamPercent = 50f;
            draftThrustPercent = 50f;
            draftSpeedPercent = 50f;
            draftAimErrorPercent = 4f;
        }

        private bool DraftDiffersFromConfig()
        {
            return draftMode != CurrentMode ||
                   draftAffectSarh != affectSarh.Value ||
                   draftAffectArh != affectArh.Value ||
                   draftAiToPlayer != affectAiToPlayer.Value ||
                   draftAiToAi != affectAiToAi.Value ||
                   draftPlayerToAi != affectPlayerToAi.Value ||
                   draftPlayerToPlayer != affectPlayerToPlayer.Value ||
                   !Mathf.Approximately(draftTrackingPercent, trackingPercent.Value) ||
                   !Mathf.Approximately(draftLockPercent, lockPercent.Value) ||
                   !Mathf.Approximately(draftJamPercent, jamPercent.Value) ||
                   !Mathf.Approximately(draftThrustPercent, thrustPercent.Value) ||
                   !Mathf.Approximately(draftSpeedPercent, speedPercent.Value) ||
                   !Mathf.Approximately(draftAimErrorPercent, aimErrorPercent.Value);
        }

        private void ApplyDraft()
        {
            applyingSettings = true;
            try
            {
                mode.Value = draftMode;
                affectSarh.Value = draftAffectSarh;
                affectArh.Value = draftAffectArh;
                affectAiToPlayer.Value = draftAiToPlayer;
                affectAiToAi.Value = draftAiToAi;
                affectPlayerToAi.Value = draftPlayerToAi;
                affectPlayerToPlayer.Value = draftPlayerToPlayer;
                trackingPercent.Value = draftTrackingPercent;
                lockPercent.Value = draftLockPercent;
                jamPercent.Value = draftJamPercent;
                thrustPercent.Value = draftThrustPercent;
                speedPercent.Value = draftSpeedPercent;
                aimErrorPercent.Value = draftAimErrorPercent;
            }
            finally
            {
                applyingSettings = false;
            }
            Config.Save();
            LoadDraftFromConfig();
            Logger.LogMessage($"Host radar missile settings applied: {BuildLocalStatusNotice()}.");
            if (IsAuthoritativeHost)
            {
                BroadcastStatus();
            }
            ShowNotice(BuildLocalStatusNotice());
        }

        private static string ModeLabel(SarhMode value)
        {
            switch (value)
            {
                case SarhMode.Nerfed: return "NERFED";
                case SarhMode.Disabled: return "DISABLED";
                default: return "OFF (VANILLA)";
            }
        }

        private static byte CurrentSeekerMask
        {
            get
            {
                byte mask = 0;
                if (affectSarh?.Value ?? true) mask |= (byte)RadarSeekerKind.Sarh;
                if (affectArh?.Value ?? true) mask |= (byte)RadarSeekerKind.Arh;
                return mask;
            }
        }

        private static byte CurrentEngagementMask
        {
            get
            {
                byte mask = 0;
                if (affectAiToPlayer?.Value ?? true) mask |= 1;
                if (affectAiToAi?.Value ?? false) mask |= 2;
                if (affectPlayerToAi?.Value ?? false) mask |= 4;
                if (affectPlayerToPlayer?.Value ?? false) mask |= 8;
                return mask;
            }
        }

        private static string SeekerLabel(byte mask)
        {
            bool sarh = (mask & (byte)RadarSeekerKind.Sarh) != 0;
            bool arh = (mask & (byte)RadarSeekerKind.Arh) != 0;
            if (sarh && arh) return "SARH+ARH";
            if (sarh) return "SARH";
            if (arh) return "ARH";
            return "NO MISSILES";
        }

        private static string EngagementLabel(byte mask)
        {
            List<string> labels = new List<string>();
            if ((mask & 1) != 0) labels.Add("AI>PLAYER");
            if ((mask & 2) != 0) labels.Add("AI>AI");
            if ((mask & 4) != 0) labels.Add("PLAYER>AI");
            if ((mask & 8) != 0) labels.Add("PLAYER>PLAYER");
            return labels.Count == 0 ? "NO ENGAGEMENTS" : string.Join("+", labels);
        }

        private static string BuildLocalStatusNotice()
        {
            return $"RADAR MISSILES: {ModeLabel(CurrentMode)} | {SeekerLabel(CurrentSeekerMask)} | {EngagementLabel(CurrentEngagementMask)}";
        }

        private static string BuildRemoteStatusNotice(SarhStatusMessage message)
        {
            return $"RADAR MISSILES: {ModeLabel((SarhMode)message.Mode)} | {SeekerLabel(message.SeekerMask)} | {EngagementLabel(message.EngagementMask)} (HOST)";
        }

        private void ShowNotice(string text)
        {
            noticeText = text;
            noticeUntil = Time.unscaledTime + 3.5f;
            Logger.LogMessage(text);
        }

        private void SubscribeSettings()
        {
            Subscribe(mode);
            Subscribe(affectSarh);
            Subscribe(affectArh);
            Subscribe(affectAiToPlayer);
            Subscribe(affectAiToAi);
            Subscribe(affectPlayerToAi);
            Subscribe(affectPlayerToPlayer);
            Subscribe(trackingPercent);
            Subscribe(lockPercent);
            Subscribe(jamPercent);
            Subscribe(thrustPercent);
            Subscribe(speedPercent);
            Subscribe(aimErrorPercent);
        }

        private void UnsubscribeSettings()
        {
            Unsubscribe(mode);
            Unsubscribe(affectSarh);
            Unsubscribe(affectArh);
            Unsubscribe(affectAiToPlayer);
            Unsubscribe(affectAiToAi);
            Unsubscribe(affectPlayerToAi);
            Unsubscribe(affectPlayerToPlayer);
            Unsubscribe(trackingPercent);
            Unsubscribe(lockPercent);
            Unsubscribe(jamPercent);
            Unsubscribe(thrustPercent);
            Unsubscribe(speedPercent);
            Unsubscribe(aimErrorPercent);
        }

        private void Subscribe<T>(ConfigEntry<T> entry)
        {
            entry.SettingChanged += OnSettingsChanged;
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
            if (settingsVisible)
            {
                LoadDraftFromConfig();
            }
            Logger.LogMessage($"Configured host radar missile settings changed: {BuildLocalStatusNotice()}.");
            if (IsAuthoritativeHost)
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
                    synchronizedStatusSignature = null;
                }
            }
        }

        private void OnClientAuthenticated(INetworkPlayer player)
        {
            if (player == null || player.IsHost || IsAuthoritativeHost)
            {
                return;
            }
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
                message.Mode > (byte)SarhMode.Disabled ||
                (message.SeekerMask & ~3) != 0 ||
                (message.EngagementMask & ~15) != 0 ||
                message.TrackingPercent > 100 ||
                message.LockPercent > 100 ||
                message.JamPercent > 100 ||
                message.ThrustPercent > 100 ||
                message.SpeedPercent > 100 ||
                message.AimErrorTenths > 100)
            {
                return;
            }
            SarhMode nextMode = (SarhMode)message.Mode;
            string nextSignature = StatusSignature(message);
            bool changed = synchronizedStatusSignature != null && synchronizedStatusSignature != nextSignature;
            synchronizedStatusSignature = nextSignature;
            if (changed)
            {
                ShowNotice(BuildRemoteStatusNotice(message));
            }
        }

        private static string StatusSignature(SarhStatusMessage message)
        {
            return $"{message.Mode}:{message.SeekerMask}:{message.EngagementMask}:{message.TrackingPercent}:{message.LockPercent}:{message.JamPercent}:{message.ThrustPercent}:{message.SpeedPercent}:{message.AimErrorTenths}";
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
                Mode = (byte)CurrentMode,
                SeekerMask = CurrentSeekerMask,
                EngagementMask = CurrentEngagementMask,
                TrackingPercent = PercentByte(trackingPercent, 50f),
                LockPercent = PercentByte(lockPercent, 50f),
                JamPercent = PercentByte(jamPercent, 50f),
                ThrustPercent = PercentByte(thrustPercent, 50f),
                SpeedPercent = PercentByte(speedPercent, 50f),
                AimErrorTenths = (byte)Mathf.Clamp(Mathf.RoundToInt((aimErrorPercent?.Value ?? 4f) * 10f), 0, 100)
            });
        }

        private static byte PercentByte(ConfigEntry<float> entry, float fallback)
        {
            return (byte)Mathf.Clamp(Mathf.RoundToInt(entry?.Value ?? fallback), 0, 100);
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

        internal static bool ShouldAffectLauncher(MissileLauncher launcher, Unit owner, Unit target)
        {
            if (launcher == null || launcher.missile == null || launcher.missile.unitPrefab == null)
            {
                return false;
            }
            RadarSeekerKind kind = RadarSeekerKind.None;
            if (launcher.missile.unitPrefab.GetComponent<SARHSeeker>() != null) kind |= RadarSeekerKind.Sarh;
            if (launcher.missile.unitPrefab.GetComponent<ARHSeeker>() != null) kind |= RadarSeekerKind.Arh;
            return ShouldAffect(kind, owner, target);
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
            writer.WriteByte(message.Mode);
            writer.WriteByte(message.SeekerMask);
            writer.WriteByte(message.EngagementMask);
            writer.WriteByte(message.TrackingPercent);
            writer.WriteByte(message.LockPercent);
            writer.WriteByte(message.JamPercent);
            writer.WriteByte(message.ThrustPercent);
            writer.WriteByte(message.SpeedPercent);
            writer.WriteByte(message.AimErrorTenths);
        }

        private static SarhStatusMessage ReadStatus(NetworkReader reader)
        {
            return new SarhStatusMessage
            {
                ProtocolVersion = reader.ReadByte(),
                Mode = reader.ReadByte(),
                SeekerMask = reader.ReadByte(),
                EngagementMask = reader.ReadByte(),
                TrackingPercent = reader.ReadByte(),
                LockPercent = reader.ReadByte(),
                JamPercent = reader.ReadByte(),
                ThrustPercent = reader.ReadByte(),
                SpeedPercent = reader.ReadByte(),
                AimErrorTenths = reader.ReadByte()
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
            internal float OriginalBlastYield;
            internal float OriginalPierceDamage;
            internal Missile.Warhead Warhead;
            internal bool OriginalArmed;
            internal Unit TargetBeforeDisabled;
            internal SarhMode? AppliedMode;
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
        private static readonly FieldInfo BlastYieldField = AccessTools.Field(typeof(Missile), "blastYield");
        private static readonly FieldInfo PierceDamageField = AccessTools.Field(typeof(Missile), "pierceDamage");
        private static readonly FieldInfo WarheadField = AccessTools.Field(typeof(Missile), "warhead");
        private static readonly ConditionalWeakTable<Missile, MissileState> States = new ConditionalWeakTable<Missile, MissileState>();

        internal static bool ReflectionReady =>
            SeekerMissileField != null && SeekerTargetField != null &&
            SarhLockPersistenceField != null && SarhJamToleranceField != null &&
            ArhLockPersistenceField != null && ArhJamToleranceField != null &&
            ArhLastActiveTrackAttemptField != null &&
            MotorsField != null &&
            MotorType != null && MotorThrustField != null && MotorTopSpeedField != null &&
            BlastYieldField != null && PierceDamageField != null && WarheadField != null;

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
            Missile.Warhead warhead = WarheadField.GetValue(missile) as Missile.Warhead;
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
                OriginalTopSpeed = originalTopSpeed,
                OriginalBlastYield = (float)BlastYieldField.GetValue(missile),
                OriginalPierceDamage = (float)PierceDamageField.GetValue(missile),
                Warhead = warhead,
                OriginalArmed = warhead != null && warhead.Armed
            });
        }

        internal static bool ApplyCurrentMode(MissileSeeker seeker)
        {
            if (!Plugin.IsAuthoritativeHost)
            {
                return true;
            }
            Missile missile = GetMissile(seeker);
            if (missile == null || !States.TryGetValue(missile, out MissileState state))
            {
                return true;
            }

            if (state.AppliedMode != SarhMode.Disabled)
            {
                Unit currentTarget = SeekerTargetField.GetValue(state.Seeker) as Unit;
                if (currentTarget != null)
                {
                    state.TargetIsHuman = Plugin.IsHumanControlled(currentTarget);
                }
            }

            if (!IsAffected(state))
            {
                if (state.AppliedMode.HasValue)
                {
                    Restore(missile, state);
                    state.AppliedMode = null;
                }
                return true;
            }

            SarhMode currentMode = Plugin.CurrentMode;
            if (state.AppliedMode != currentMode)
            {
                Restore(missile, state);
                if (currentMode == SarhMode.Disabled)
                {
                    state.TargetBeforeDisabled = SeekerTargetField.GetValue(state.Seeker) as Unit;
                    BlastYieldField.SetValue(missile, 0f);
                    PierceDamageField.SetValue(missile, 0f);
                    if (state.Warhead != null)
                    {
                        state.Warhead.Armed = false;
                    }
                }
                state.AppliedMode = currentMode;
            }

            if (currentMode == SarhMode.Nerfed)
            {
                ApplyFlightNerf(state);
            }

            if (currentMode != SarhMode.Disabled)
            {
                return true;
            }
            if (state.Warhead != null)
            {
                state.Warhead.Armed = false;
            }
            missile.SetTarget(null);
            missile.SetAimpoint(missile.GlobalPosition() + missile.transform.forward * 100000f, Vector3.zero);
            return false;
        }

        internal static bool ShouldNerf(MissileSeeker seeker)
        {
            if (!Plugin.IsAuthoritativeHost || Plugin.CurrentMode != SarhMode.Nerfed)
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
                Plugin.CurrentMode != SarhMode.Nerfed ||
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

        private static void Restore(Missile missile, MissileState state)
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
            BlastYieldField.SetValue(missile, state.OriginalBlastYield);
            PierceDamageField.SetValue(missile, state.OriginalPierceDamage);
            if (state.Warhead != null)
            {
                state.Warhead.Armed = state.OriginalArmed;
            }
            if (state.AppliedMode == SarhMode.Disabled &&
                state.TargetBeforeDisabled != null &&
                !state.TargetBeforeDisabled.disabled)
            {
                missile.SetTarget(state.TargetBeforeDisabled);
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
            SarhController.ApplyCurrentMode(__instance);
        }
    }

    [HarmonyPatch(typeof(SARHSeeker), nameof(SARHSeeker.Seek))]
    internal static class SarhSeekPatch
    {
        private static bool Prefix(SARHSeeker __instance)
        {
            return SarhController.ApplyCurrentMode(__instance);
        }
    }

    [HarmonyPatch(typeof(ARHSeeker), nameof(ARHSeeker.Initialize))]
    internal static class ArhInitializePatch
    {
        private static void Postfix(ARHSeeker __instance, Unit target)
        {
            SarhController.Register(__instance, target);
            SarhController.ApplyCurrentMode(__instance);
        }
    }

    [HarmonyPatch(typeof(ARHSeeker), nameof(ARHSeeker.Seek))]
    internal static class ArhSeekPatch
    {
        private static bool Prefix(ARHSeeker __instance)
        {
            return SarhController.ApplyCurrentMode(__instance);
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

    [HarmonyPatch(typeof(MissileLauncher), nameof(MissileLauncher.Fire))]
    internal static class RadarMissileLaunchPatch
    {
        private static bool Prefix(MissileLauncher __instance, Unit owner, Unit target)
        {
            if (!Plugin.IsAuthoritativeHost ||
                Plugin.CurrentMode != SarhMode.Disabled ||
                !Plugin.ShouldAffectLauncher(__instance, owner, target))
            {
                return true;
            }
            Plugin.Log?.LogDebug("Blocked a selected radar-missile launch for an affected engagement route.");
            return false;
        }
    }
}
