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
        Vanilla,
        Nerf50,
        [Obsolete("Legacy configuration name; use Nerf50.")]
        Nerf80 = Nerf50,
        Disabled
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
    }

    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    [BepInProcess("NuclearOption.exe")]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "blacknight2u.sarhcontrol";
        public const string PluginName = "Radar Missile Control";
        public const string PluginVersion = "0.4.1";

        private const byte ProtocolVersion = 3;
        private const float NoticeWidth = 360f;
        private static ConfigEntry<SarhMode> mode;
        private static ConfigEntry<KeyboardShortcut> toggleShortcut;
        private readonly HashSet<INetworkPlayer> moddedClients = new HashSet<INetworkPlayer>();
        private Harmony harmony;
        private MessageHandler clientHandler;
        private MessageHandler serverHandler;
        private NetworkClient registeredClient;
        private SarhMode? synchronizedMode;
        private GUIStyle noticeStyle;
        private string noticeText;
        private float noticeUntil;

        internal static ManualLogSource Log { get; private set; }
        internal static SarhMode CurrentMode => mode?.Value ?? SarhMode.Vanilla;

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
                SarhMode.Nerf50,
                "Used only by the host or dedicated server. Nerf50 halves SARH and ARH tracking, lock persistence, motor thrust, and motor speed limits while adding aim error. Warhead damage remains vanilla."
            );
            toggleShortcut = Config.Bind(
                "General",
                "Host toggle shortcut",
                new KeyboardShortcut(KeyCode.F8),
                "Host-only shortcut that cycles Nerf50, Disabled, and Vanilla/off. Clients cannot change the server mode."
            );
            mode.SettingChanged += OnModeChanged;

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
            if (toggleShortcut == null || !toggleShortcut.Value.IsDown())
            {
                return;
            }

            if (IsAuthoritativeHost)
            {
                mode.Value = NextMode(CurrentMode);
                Config.Save();
                ShowNotice($"RADAR MISSILES: {ModeLabel(CurrentMode)}");
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
                case SarhMode.Nerf50: return SarhMode.Disabled;
                case SarhMode.Disabled: return SarhMode.Vanilla;
                default: return SarhMode.Nerf50;
            }
        }

        private void OnGUI()
        {
            if (string.IsNullOrEmpty(noticeText) || Time.unscaledTime >= noticeUntil)
            {
                return;
            }
            EnsureStyles();
            float x = Mathf.Max(10f, Screen.width - NoticeWidth - 16f);
            Color oldColor = GUI.color;
            int oldDepth = GUI.depth;
            GUI.depth = -1000;
            GUI.color = Color.white;
            GUI.Box(new Rect(x, 14f, NoticeWidth, 38f), noticeText, noticeStyle);
            GUI.color = oldColor;
            GUI.depth = oldDepth;
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
        }

        private static string ModeLabel(SarhMode value)
        {
            switch (value)
            {
                case SarhMode.Nerf50: return "NERFED (50%)";
                case SarhMode.Disabled: return "DISABLED";
                default: return "OFF (VANILLA)";
            }
        }


        private void ShowNotice(string text)
        {
            noticeText = text;
            noticeUntil = Time.unscaledTime + 3.5f;
            Logger.LogMessage(text);
        }

        private void OnModeChanged(object sender, EventArgs args)
        {
            Logger.LogMessage($"Configured host radar missile mode changed to {CurrentMode}.");
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
                    synchronizedMode = null;
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
            if (message.ProtocolVersion != ProtocolVersion || message.Mode > (byte)SarhMode.Disabled)
            {
                return;
            }
            SarhMode nextMode = (SarhMode)message.Mode;
            bool changed = synchronizedMode.HasValue && synchronizedMode.Value != nextMode;
            synchronizedMode = nextMode;
            if (changed)
            {
                ShowNotice($"RADAR MISSILES: {ModeLabel(nextMode)} (HOST)");
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
                Mode = (byte)CurrentMode
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
            if (mode != null)
            {
                mode.SettingChanged -= OnModeChanged;
            }
            UnregisterClientHandler();
            UnregisterServerHandler();
            harmony?.UnpatchSelf();
        }

        internal static bool IsHumanControlled(Unit unit)
        {
            Aircraft aircraft = unit as Aircraft;
            return aircraft != null && aircraft.Player != null;
        }

        internal static bool ShouldAffect(Unit owner, Unit target)
        {
            return !IsHumanControlled(owner) && IsHumanControlled(target);
        }

        internal static bool IsControlledRadarLauncher(MissileLauncher launcher)
        {
            return launcher != null &&
                   launcher.missile != null &&
                   launcher.missile.unitPrefab != null &&
                   (launcher.missile.unitPrefab.GetComponent<SARHSeeker>() != null ||
                    launcher.missile.unitPrefab.GetComponent<ARHSeeker>() != null);
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
        }

        private static SarhStatusMessage ReadStatus(NetworkReader reader)
        {
            return new SarhStatusMessage
            {
                ProtocolVersion = reader.ReadByte(),
                Mode = reader.ReadByte()
            };
        }
    }

    internal static class SarhController
    {
        private sealed class MissileState
        {
            internal MissileSeeker Seeker;
            internal bool AffectsPlayer;
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
                AffectsPlayer = Plugin.ShouldAffect(missile.owner, target),
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
            if (missile == null || !States.TryGetValue(missile, out MissileState state) || !state.AffectsPlayer)
            {
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

            if (currentMode == SarhMode.Nerf50)
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
            if (!Plugin.IsAuthoritativeHost || Plugin.CurrentMode != SarhMode.Nerf50)
            {
                return false;
            }
            Missile missile = GetMissile(seeker);
            return missile != null &&
                   States.TryGetValue(missile, out MissileState state) &&
                   state.AffectsPlayer;
        }

        internal static bool IsFreshArhRadarScan(ARHSeeker seeker)
        {
            return ShouldNerf(seeker) &&
                   Time.timeSinceLevelLoad - (float)ArhLastActiveTrackAttemptField.GetValue(seeker) >= 0.5f;
        }

        internal static void ApplyAimError(Missile missile, ref GlobalPosition aimPoint)
        {
            if (!Plugin.IsAuthoritativeHost ||
                Plugin.CurrentMode != SarhMode.Nerf50 ||
                missile == null ||
                !States.TryGetValue(missile, out MissileState state) ||
                !state.AffectsPlayer)
            {
                return;
            }

            Vector3 toAimpoint = aimPoint - missile.GlobalPosition();
            float remainingRange = toAimpoint.magnitude;
            if (remainingRange < 1f)
            {
                return;
            }

            float errorRadius = Mathf.Clamp(remainingRange * 0.04f, 25f, 400f);
            float phase = Time.timeSinceLevelLoad * 2.2f + missile.GetInstanceID() * 0.017f;
            Vector3 errorDirection =
                (missile.transform.right * Mathf.Sin(phase) +
                 missile.transform.up * Mathf.Cos(phase * 0.73f)).normalized;
            aimPoint += errorDirection * errorRadius;
        }

        private static void ApplyFlightNerf(MissileState state)
        {
            state.PersistenceField.SetValue(state.Seeker, state.OriginalLockPersistence * 0.5f);
            state.JamToleranceField.SetValue(state.Seeker, state.OriginalJamTolerance * 0.5f);
            int motorCount = state.Motors?.Length ?? 0;
            for (int i = 0; i < motorCount; i++)
            {
                object motor = state.Motors.GetValue(i);
                if (motor != null)
                {
                    MotorThrustField.SetValue(motor, state.OriginalThrust[i] * 0.5f);
                    MotorTopSpeedField.SetValue(motor, state.OriginalTopSpeed[i] * 0.5f);
                }
            }
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
                __result *= 0.5f;
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
                __result *= 0.5f;
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
                !Plugin.IsControlledRadarLauncher(__instance) ||
                !Plugin.ShouldAffect(owner, target))
            {
                return true;
            }
            Plugin.Log?.LogDebug("Blocked an AI SARH/ARH launch targeting a human-controlled aircraft.");
            return false;
        }
    }
}
