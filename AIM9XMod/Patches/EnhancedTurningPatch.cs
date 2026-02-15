using HarmonyLib;
using UnityEngine;

namespace AIM9XMod.Patches
{
    /// <summary>
    /// Enhances IR missile turning performance to AIM-9X levels.
    /// The AIM-9X uses thrust vector control (jet vanes) for 50+ G maneuverability.
    /// We increase the PID turn rate limit and torque for missiles with IR seekers.
    /// </summary>
    [HarmonyPatch]
    public static class EnhancedTurningPatch
    {
        /// <summary>
        /// Patch Missile.StartMissile (called via OnStartNetwork) to boost IR missile agility.
        /// At this point the PID is created with: new PID2D(PIDFactors, maxTurnRate, 3f)
        /// and torqueAxes = rb.inertiaTensor * torque.
        /// We patch after StartMissile to override these values for IR missiles.
        /// </summary>
        [HarmonyPatch(typeof(Missile), "StartMissile")]
        [HarmonyPostfix]
        public static void Missile_StartMissile_Postfix(Missile __instance)
        {
            if (!Plugin.EnableEnhancedTurning.Value) return;

            // Check if this missile has an IR seeker
            var seeker = __instance.gameObject.GetComponent<IRSeeker>();
            if (seeker == null) return;

            // Get the original values
            float origTurnRate = __instance.GetMaxTurnRate();
            float origTorque = __instance.GetTorque();

            // Apply enhanced values
            float newTurnRate = Mathf.Max(origTurnRate, Plugin.MissileMaxTurnRate.Value);
            float newTorque = origTorque * Plugin.MissileTorqueMultiplier.Value;

            // SetTorque also updates the PID pLimit when LocalSim is true
            __instance.SetTorque(newTorque, newTurnRate);

            Plugin.Log.LogDebug($"[TVC] Enhanced IR missile turning: turnRate {origTurnRate} -> {newTurnRate}, torque {origTorque} -> {newTorque}");
        }
    }
}
