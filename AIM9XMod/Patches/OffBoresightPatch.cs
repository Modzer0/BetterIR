using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace AIM9XMod.Patches
{
    /// <summary>
    /// Server-side off-boresight patch.
    /// 
    /// Instead of patching the client HUD, we modify WeaponInfo ScriptableObject assets
    /// directly when missiles are initialized. Since WeaponInfo is a shared asset,
    /// changing minAlignment on it propagates to all weapon stations referencing it,
    /// including on clients that receive the weapon data.
    /// 
    /// For AI, we also patch CombatAI.AnalyzeTarget to widen the firing angle.
    /// </summary>
    [HarmonyPatch]
    public static class OffBoresightPatch
    {
        // Track which WeaponInfo assets we've already patched to avoid redundant work
        private static readonly HashSet<int> patchedWeaponInfos = new HashSet<int>();

        /// <summary>
        /// Patch WeaponStation.LaunchMount to widen minAlignment on the WeaponInfo asset
        /// before the missile is spawned. This runs on the server and the modified asset
        /// data is what clients see.
        /// </summary>
        [HarmonyPatch(typeof(WeaponStation), "LaunchMount")]
        [HarmonyPrefix]
        public static void WeaponStation_LaunchMount_Prefix(WeaponStation __instance)
        {
            if (!Plugin.EnableHighOffBoresight.Value) return;

            var info = __instance.WeaponInfo;
            if (info == null) return;
            if (info.targetRequirements.minIR <= 0f) return;

            int id = info.GetInstanceID();
            if (patchedWeaponInfos.Contains(id)) return;

            var reqs = info.targetRequirements;
            if (reqs.minAlignment < Plugin.FiringGateAngle.Value)
            {
                reqs.minAlignment = Plugin.FiringGateAngle.Value;
                info.targetRequirements = reqs;
                patchedWeaponInfos.Add(id);

                Plugin.Log.LogDebug(
                    $"[HOBS] Patched WeaponInfo '{info.weaponName}' minAlignment to {Plugin.FiringGateAngle.Value}°");
            }
        }

        /// <summary>
        /// Also patch when HUDMissileState reads the weapon info, to catch any assets
        /// that haven't been patched yet via LaunchMount.
        /// </summary>
        [HarmonyPatch(typeof(HUDMissileState), "SetHUDWeaponState")]
        [HarmonyPostfix]
        public static void HUDMissileState_SetHUDWeaponState_Postfix(HUDMissileState __instance, WeaponStation weaponStation)
        {
            if (!Plugin.EnableHighOffBoresight.Value) return;

            var info = weaponStation.WeaponInfo;
            if (info == null || info.targetRequirements.minIR <= 0f) return;

            int id = info.GetInstanceID();
            if (!patchedWeaponInfos.Contains(id))
            {
                var reqs = info.targetRequirements;
                if (reqs.minAlignment < Plugin.FiringGateAngle.Value)
                {
                    reqs.minAlignment = Plugin.FiringGateAngle.Value;
                    info.targetRequirements = reqs;
                    patchedWeaponInfos.Add(id);
                }
            }

            // Also set the HUD field directly for immediate effect on this client
            var field = AccessTools.Field(typeof(HUDMissileState), "minAlignment");
            if (field != null)
                field.SetValue(__instance, Plugin.FiringGateAngle.Value);
        }

        /// <summary>
        /// Patch CombatAI.AnalyzeTarget to allow AI to fire IR missiles at wider angles.
        /// </summary>
        [HarmonyPatch(typeof(CombatAI), "AnalyzeTarget")]
        [HarmonyPrefix]
        public static void CombatAI_AnalyzeTarget_Prefix(WeaponStation weaponStation)
        {
            if (!Plugin.EnableHighOffBoresight.Value) return;

            var info = weaponStation.WeaponInfo;
            if (info == null || info.targetRequirements.minIR <= 0f) return;

            var reqs = info.targetRequirements;
            if (reqs.minAlignment < Plugin.FiringGateAngle.Value)
            {
                reqs.minAlignment = Plugin.FiringGateAngle.Value;
                info.targetRequirements = reqs;
            }
        }
    }
}
