using HarmonyLib;
using UnityEngine;

namespace AIM9XMod.Patches
{
    /// <summary>
    /// Patches the HUD and AI to allow IR missile launches up to 180°.
    /// The firing gate uses FiringGateAngle (180°) so you can launch at targets behind you.
    /// The seeker itself is limited to OffBoresightAngle (90°) — launches beyond 90° go
    /// out in LOAL mode and the missile must turn to acquire the target within its seeker cone.
    /// </summary>
    [HarmonyPatch]
    public static class OffBoresightPatch
    {
        /// <summary>
        /// Patch HUDMissileState.SetHUDWeaponState to widen the firing gate for IR weapons.
        /// Uses FiringGateAngle (180°) so the HUD won't show "OUT OF ARC" for rear targets.
        /// </summary>
        [HarmonyPatch(typeof(HUDMissileState), "SetHUDWeaponState")]
        [HarmonyPostfix]
        public static void HUDMissileState_SetHUDWeaponState_Postfix(HUDMissileState __instance, WeaponStation weaponStation)
        {
            if (!Plugin.EnableHighOffBoresight.Value) return;

            var info = weaponStation.WeaponInfo;
            if (info == null) return;

            if (info.targetRequirements.minIR <= 0f) return;

            var field = AccessTools.Field(typeof(HUDMissileState), "minAlignment");
            if (field != null)
            {
                field.SetValue(__instance, Plugin.FiringGateAngle.Value);
            }
        }

        /// <summary>
        /// Patch CombatAI.AnalyzeTarget to allow AI to fire IR missiles at wider angles.
        /// AI uses the seeker angle (90°) since AI missiles don't benefit from view-slaving.
        /// </summary>
        [HarmonyPatch(typeof(CombatAI), "AnalyzeTarget")]
        [HarmonyPrefix]
        public static void CombatAI_AnalyzeTarget_Prefix(WeaponStation weaponStation)
        {
            if (!Plugin.EnableHighOffBoresight.Value) return;

            var info = weaponStation.WeaponInfo;
            if (info == null || info.targetRequirements.minIR <= 0f) return;

            var reqs = info.targetRequirements;
            if (reqs.minAlignment < Plugin.OffBoresightAngle.Value)
            {
                reqs.minAlignment = Plugin.OffBoresightAngle.Value;
                info.targetRequirements = reqs;
            }
        }
    }
}
