using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace AIM9XMod
{
    [BepInPlugin(PluginGUID, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGUID = "com.modder.aim9xmod";
        public const string PluginName = "AIM-9X IR Missile Overhaul";
        public const string PluginVersion = "0.5.1";

        internal static ManualLogSource Log;
        internal static Harmony HarmonyInstance;

        // Config entries
        public static ConfigEntry<float> OffBoresightAngle;
        public static ConfigEntry<float> FiringGateAngle;
        public static ConfigEntry<float> MissileMaxTurnRate;
        public static ConfigEntry<float> MissileTorqueMultiplier;
        public static ConfigEntry<float> LOALSearchAngle;
        public static ConfigEntry<float> LOALSearchTime;
        public static ConfigEntry<bool> EnableLOAL;
        public static ConfigEntry<bool> EnableHighOffBoresight;
        public static ConfigEntry<bool> EnableEnhancedTurning;
        public static ConfigEntry<bool> UsePeakIRThreshold;
        public static ConfigEntry<bool> EnableTargetDirectedLOAL;

        private void Awake()
        {
            Log = Logger;

            // Config
            EnableHighOffBoresight = Config.Bind("Features", "EnableHighOffBoresight", true,
                "Enable 90° off-boresight launch capability for IR missiles");
            EnableEnhancedTurning = Config.Bind("Features", "EnableEnhancedTurning", true,
                "Enable AIM-9X-class turning performance for IR missiles");
            EnableLOAL = Config.Bind("Features", "EnableLOAL", true,
                "Enable Lock-On After Launch for IR missiles");
            UsePeakIRThreshold = Config.Bind("Features", "UsePeakIRThreshold", false,
                "When true, flare evasion threshold uses the highest IR the missile ever observed while tracking. " +
                "When false (default), uses the aircraft's IR output at the moment of flare evasion.");
            EnableTargetDirectedLOAL = Config.Bind("Features", "EnableTargetDirectedLOAL", true,
                "When true, IR missiles in LOAL mode steer toward the designated target's position. " +
                "Works server-side for all players without requiring the mod on clients.");

            OffBoresightAngle = Config.Bind("Boresight", "OffBoresightAngle", 90f,
                "Maximum off-boresight seeker angle in degrees (AIM-9X: 90°). " +
                "The firing gate allows 180° but launches beyond this angle go out in LOAL mode.");
            FiringGateAngle = Config.Bind("Boresight", "FiringGateAngle", 180f,
                "Maximum angle at which IR missiles can be launched. Targets beyond OffBoresightAngle " +
                "but within this angle launch the missile in LOAL (no-lock) mode.");

            MissileMaxTurnRate = Config.Bind("Turning", "MaxTurnRate", 12f,
                "Maximum turn rate for IR missile PID (vanilla default ~3). Higher = tighter tracking.");
            MissileTorqueMultiplier = Config.Bind("Turning", "TorqueMultiplier", 3f,
                "Multiplier applied to IR missile torque for enhanced maneuverability");

            LOALSearchAngle = Config.Bind("LOAL", "SearchAngle", 90f,
                "Seeker search cone half-angle (degrees) when acquiring target after launch");
            LOALSearchTime = Config.Bind("LOAL", "SearchTime", 8f,
                "Maximum time (seconds) the seeker will search for a target after launch before going ballistic");

            HarmonyInstance = new Harmony(PluginGUID);
            HarmonyInstance.PatchAll();

            Log.LogInfo($"AIM-9X IR Missile Overhaul v{PluginVersion} loaded!");
        }

        private void OnDestroy()
        {
            HarmonyInstance?.UnpatchSelf();
        }
    }
}
