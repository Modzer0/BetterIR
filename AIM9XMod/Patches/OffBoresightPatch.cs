using HarmonyLib;
using UnityEngine;

namespace AIM9XMod.Patches
{
    /// <summary>
    /// Patches the HUD missile state to allow high off-boresight launches for IR missiles.
    /// The game uses TargetRequirements.minAlignment to gate whether a missile can fire.
    /// We widen this to 90° for any weapon with minIR > 0 (i.e., IR-seeking weapons).
    /// </summary>
    [HarmonyPatch]
    public static class OffBoresightPatch
    {
        /// <summary>
        /// Patch HUDMissileState.SetHUDWeaponState to override minAlignment for IR weapons.
        /// This controls the "OUT OF ARC" HUD indicator and the firing gate.
        /// </summary>
        [HarmonyPatch(typeof(HUDMissileState), "SetHUDWeaponState")]
        [HarmonyPostfix]
        public static void HUDMissileState_SetHUDWeaponState_Postfix(HUDMissileState __instance, WeaponStation weaponStation)
        {
            if (!Plugin.EnableHighOffBoresight.Value) return;

            var info = weaponStation.WeaponInfo;
            if (info == null) return;

            // Only apply to IR-seeking weapons (minIR > 0 means it needs an IR signature)
            if (info.targetRequirements.minIR <= 0f) return;

            // Override the private minAlignment field via reflection
            var field = AccessTools.Field(typeof(HUDMissileState), "minAlignment");
            if (field != null)
            {
                field.SetValue(__instance, Plugin.OffBoresightAngle.Value);
                Plugin.Log.LogDebug($"[HOBS] Set HUD minAlignment to {Plugin.OffBoresightAngle.Value}° for {info.weaponName}");
            }
        }

        /// <summary>
        /// Patch CombatAI.AnalyzeTarget to allow AI to fire IR missiles at high off-boresight angles.
        /// The AI checks targetAngle < minAlignment before firing. We widen this for IR weapons.
        /// </summary>
        [HarmonyPatch(typeof(CombatAI), "AnalyzeTarget")]
        [HarmonyPrefix]
        public static void CombatAI_AnalyzeTarget_Prefix(WeaponStation weaponStation)
        {
            if (!Plugin.EnableHighOffBoresight.Value) return;

            var info = weaponStation.WeaponInfo;
            if (info == null || info.targetRequirements.minIR <= 0f) return;

            // Widen the minAlignment on the struct directly
            // TargetRequirements is a struct, so we modify it on the WeaponInfo reference
            var reqs = info.targetRequirements;
            if (reqs.minAlignment < Plugin.OffBoresightAngle.Value)
            {
                reqs.minAlignment = Plugin.OffBoresightAngle.Value;
                info.targetRequirements = reqs;
            }
        }
    }
}
