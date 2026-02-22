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
        public const string PluginVersion = "0.5.0";

        internal static ManualLogSource Log;
        internal static Harmony HarmonyInstance;

        // Config entries
        public static ConfigEntry<float> OffBoresightAngle;
        public static ConfigEntry<float> MissileMaxTurnRate;
        public static ConfigEntry<float> MissileTorqueMultiplier;
        public static ConfigEntry<float> LOALSearchAngle;
        public static ConfigEntry<float> LOALSearchTime;
        public static ConfigEntry<bool> EnableLOAL;
        public static ConfigEntry<bool> EnableHighOffBoresight;
        public static ConfigEntry<bool> EnableEnhancedTurning;
        public static ConfigEntry<bool> UsePeakIRThreshold;
        public static ConfigEntry<bool> EnableViewSlaving;

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
            EnableViewSlaving = Config.Bind("Features", "EnableViewSlaving", true,
                "When true, IR missiles without a lock steer toward the player's view direction (center of view marker) " +
                "while scanning for targets. Only affects player-launched missiles.");

            OffBoresightAngle = Config.Bind("Boresight", "OffBoresightAngle", 90f,
                "Maximum off-boresight launch angle in degrees (AIM-9X: 90°)");

            MissileMaxTurnRate = Config.Bind("Turning", "MaxTurnRate", 12f,
                "Maximum turn rate for IR missile PID (vanilla default ~3). Higher = tighter tracking.");
            MissileTorqueMultiplier = Config.Bind("Turning", "TorqueMultiplier", 3f,
                "Multiplier applied to IR missile torque for enhanced maneuverability");

            LOALSearchAngle = Config.Bind("LOAL", "SearchAngle", 90f,
                "Seeker search cone angle (degrees) when acquiring target after launch");
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
