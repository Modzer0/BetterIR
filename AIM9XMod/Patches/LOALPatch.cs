using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace AIM9XMod.Patches
{
    /// <summary>
    /// Implements Lock-On After Launch (LOAL) for IR missiles.
    /// 
    /// In vanilla, if an IR missile loses lock or is launched without one, it drifts
    /// on its last known heading with accumulating error until self-destruct.
    /// 
    /// With LOAL, the seeker actively scans for IR-emitting targets within its search
    /// cone during flight. If it finds one with LOS and within the cone, it acquires
    /// lock and begins tracking — just like the AIM-9X Block II datalink capability.
    /// 
    /// Flare evasion memory: when a target successfully decoys the seeker with flares,
    /// the missile records that unit and its IR intensity at the moment of evasion.
    /// The LOAL scan will NOT reacquire that same unit unless its IR signature has
    /// increased beyond what it was when it evaded (e.g., lit afterburner).
    /// </summary>
    [HarmonyPatch]
    public static class LOALPatch
    {
        // Track which missiles are in LOAL search mode and their search start time
        private static readonly Dictionary<int, float> loalSearchStart = new Dictionary<int, float>();
        private static readonly Dictionary<int, float> lastScanTime = new Dictionary<int, float>();

        // Track units that successfully evaded each missile via flares.
        // Key: missile instance ID -> Dictionary of (evaded unit PersistentID -> IR intensity at evasion time)
        private static readonly Dictionary<int, Dictionary<PersistentID, float>> flareEvadedUnits
            = new Dictionary<int, Dictionary<PersistentID, float>>();

        /// <summary>
        /// Get the total IR intensity of a unit by summing all its IR sources.
        /// </summary>
        private static float GetTotalIRIntensity(Unit unit)
        {
            if (unit == null) return 0f;
            var sources = Traverse.Create(unit).Field("IRSources").GetValue<List<IRSource>>();
            if (sources == null) return 0f;

            float total = 0f;
            for (int i = 0; i < sources.Count; i++)
            {
                if (sources[i] != null && !sources[i].flare)
                    total += sources[i].intensity;
            }
            return total;
        }

        /// <summary>
        /// Patch IRSeeker.Initialize to register missiles for LOAL scanning.
        /// </summary>
        [HarmonyPatch(typeof(IRSeeker), "Initialize")]
        [HarmonyPostfix]
        public static void IRSeeker_Initialize_Postfix(IRSeeker __instance)
        {
            if (!Plugin.EnableLOAL.Value) return;

            var missile = Traverse.Create(__instance).Field("missile").GetValue<Missile>();
            if (missile == null) return;

            int id = missile.GetInstanceID();
            loalSearchStart[id] = Time.timeSinceLevelLoad;
            lastScanTime[id] = 0f;
            flareEvadedUnits[id] = new Dictionary<PersistentID, float>();
        }

        /// <summary>
        /// Patch IRSeeker_OnTargetFlare to record when a unit successfully evades via flares.
        /// 
        /// In vanilla, when dazzleAmount exceeds the target's effective IR signature,
        /// LoseLock() is called and IRTarget is set to the flare source. We detect this
        /// transition and record the evading unit + its IR intensity at that moment.
        /// </summary>
        [HarmonyPatch(typeof(IRSeeker), "IRSeeker_OnTargetFlare")]
        [HarmonyPrefix]
        public static void IRSeeker_OnTargetFlare_Prefix(
            IRSeeker __instance,
            out FlareEvasionSnapshot __state)
        {
            __state = default;
            if (!Plugin.EnableLOAL.Value) return;

            var t = Traverse.Create(__instance);
            var targetUnit = t.Field("targetUnit").GetValue<Unit>();
            var irTarget = t.Field("IRTarget").GetValue<IRSource>();

            // Snapshot the current state before the vanilla method runs.
            // If the vanilla method transfers lock to the flare, we'll know
            // by comparing IRTarget before and after.
            if (targetUnit != null && irTarget != null && !irTarget.flare)
            {
                __state = new FlareEvasionSnapshot
                {
                    valid = true,
                    unitId = targetUnit.persistentID,
                    irIntensity = GetTotalIRIntensity(targetUnit),
                    originalIRTarget = irTarget
                };
            }
        }

        [HarmonyPatch(typeof(IRSeeker), "IRSeeker_OnTargetFlare")]
        [HarmonyPostfix]
        public static void IRSeeker_OnTargetFlare_Postfix(
            IRSeeker __instance,
            FlareEvasionSnapshot __state)
        {
            if (!Plugin.EnableLOAL.Value || !__state.valid) return;

            var t = Traverse.Create(__instance);
            var missile = t.Field("missile").GetValue<Missile>();
            if (missile == null) return;

            var currentIRTarget = t.Field("IRTarget").GetValue<IRSource>();

            // If IRTarget changed from the original (non-flare) source to something else,
            // the flare successfully decoyed the seeker.
            if (currentIRTarget != __state.originalIRTarget)
            {
                int missileId = missile.GetInstanceID();
                if (!flareEvadedUnits.ContainsKey(missileId))
                    flareEvadedUnits[missileId] = new Dictionary<PersistentID, float>();

                // Record the evading unit and its IR intensity at evasion time
                flareEvadedUnits[missileId][__state.unitId] = __state.irIntensity;

                Plugin.Log.LogDebug(
                    $"[LOAL] Recorded flare evasion: unit {__state.unitId} at IR intensity {__state.irIntensity:F2}. " +
                    $"Missile will not relock unless target increases IR output.");
            }
        }

        /// <summary>
        /// Patch IRSeeker.Seek to add LOAL target acquisition when the seeker has no lock.
        /// Skips units that successfully evaded via flares unless their IR signature
        /// has increased beyond the level recorded at evasion time.
        /// </summary>
        [HarmonyPatch(typeof(IRSeeker), "Seek")]
        [HarmonyPostfix]
        public static void IRSeeker_Seek_Postfix(IRSeeker __instance)
        {
            if (!Plugin.EnableLOAL.Value) return;

            var t = Traverse.Create(__instance);
            var missile = t.Field("missile").GetValue<Missile>();
            if (missile == null || missile.disabled) return;

            var irTarget = t.Field("IRTarget").GetValue<IRSource>();

            // Only scan when we have no lock
            if (irTarget != null && irTarget.transform != null) return;

            // Check if guidance is active
            bool guidance = t.Field("guidance").GetValue<bool>();
            if (!guidance) return;

            int id = missile.GetInstanceID();
            if (!loalSearchStart.ContainsKey(id)) return;

            // Check search time limit
            float searchElapsed = Time.timeSinceLevelLoad - loalSearchStart[id];
            if (searchElapsed > Plugin.LOALSearchTime.Value) return;

            // Throttle scanning to every 0.25s (same as vanilla IRLockCheck interval)
            if (!lastScanTime.ContainsKey(id)) lastScanTime[id] = 0f;
            if (Time.timeSinceLevelLoad - lastScanTime[id] < 0.25f) return;
            lastScanTime[id] = Time.timeSinceLevelLoad;

            // Get the flare evasion blacklist for this missile
            Dictionary<PersistentID, float> evadedLookup = null;
            flareEvadedUnits.TryGetValue(id, out evadedLookup);

            // Scan for IR targets
            Unit bestTarget = null;
            IRSource bestSource = null;
            float bestScore = float.MaxValue;

            float searchAngle = Plugin.LOALSearchAngle.Value;
            float maxRange = missile.GetWeaponInfo().targetRequirements.maxRange;
            Vector3 missilePos = missile.transform.position;
            Vector3 missileForward = missile.transform.forward;
            FactionHQ missileHQ = missile.NetworkHQ;

            for (int i = 0; i < UnitRegistry.allUnits.Count; i++)
            {
                Unit unit = UnitRegistry.allUnits[i];
                if (unit == null || unit.disabled) continue;
                if (unit == (Unit)missile) continue;

                // Skip friendlies
                if (missileHQ != null && unit.NetworkHQ == missileHQ) continue;

                // Must have IR signature
                if (!unit.HasIRSignature()) continue;

                // --- Flare evasion check ---
                // If this unit previously evaded this missile with flares,
                // only allow relock if its current IR intensity exceeds what
                // it was at evasion time (e.g., afterburner was lit since then).
                if (evadedLookup != null)
                {
                    float evadedIR;
                    if (evadedLookup.TryGetValue(unit.persistentID, out evadedIR))
                    {
                        float currentIR = GetTotalIRIntensity(unit);
                        if (currentIR <= evadedIR)
                            continue; // Same or lower signature — skip this unit
                    }
                }

                // Range check
                Vector3 toTarget = unit.transform.position - missilePos;
                float dist = toTarget.magnitude;
                if (dist > maxRange || dist < 50f) continue;

                // Cone check
                float angle = Vector3.Angle(missileForward, toTarget);
                if (angle > searchAngle) continue;

                // Line of sight check (layer 64 = terrain)
                if (Physics.Linecast(missilePos, unit.transform.position, 64))
                    continue;

                // Score by angle and distance (prefer close, on-axis targets)
                float score = angle + dist * 0.001f;
                if (score < bestScore)
                {
                    IRSource source = unit.GetIRSource();
                    if (source != null && !source.flare)
                    {
                        bestTarget = unit;
                        bestSource = source;
                        bestScore = score;
                    }
                }
            }

            if (bestTarget != null && bestSource != null)
            {
                // Acquire lock
                t.Field("IRTarget").SetValue(bestSource);
                t.Field("targetUnit").SetValue(bestTarget);
                t.Field("driftError").SetValue(Vector3.zero);
                t.Field("dazzleAmount").SetValue(0f);
                t.Field("achievedLock").SetValue(false);

                // Subscribe to flare events on the new target
                try
                {
                    var flareHandler = AccessTools.Method(typeof(IRSeeker), "IRSeeker_OnTargetFlare");
                    if (flareHandler != null)
                    {
                        var del = (Action<IRSource>)Delegate.CreateDelegate(
                            typeof(Action<IRSource>), __instance, flareHandler);
                        bestTarget.onAddIRSource += del;
                    }
                }
                catch (Exception) { /* Non-critical */ }

                // Update the missile's target reference
                missile.SetTarget(bestTarget);

                // Remove from LOAL search pool — we have lock
                loalSearchStart.Remove(id);
                lastScanTime.Remove(id);
                // Keep flareEvadedUnits — if this new target also flares, we need the history

                Plugin.Log.LogDebug(
                    $"[LOAL] Acquired lock on {bestTarget.unitName} at {bestScore:F1} score");
            }
        }

        /// <summary>
        /// Patch IRSeeker.SlowChecks to prevent premature self-destruct during LOAL search.
        /// </summary>
        [HarmonyPatch(typeof(IRSeeker), "SlowChecks")]
        [HarmonyPrefix]
        public static bool IRSeeker_SlowChecks_Prefix(IRSeeker __instance)
        {
            if (!Plugin.EnableLOAL.Value) return true;

            var t = Traverse.Create(__instance);
            var missile = t.Field("missile").GetValue<Missile>();
            if (missile == null || missile.disabled) return true;

            int id = missile.GetInstanceID();
            if (!loalSearchStart.ContainsKey(id)) return true;

            float searchElapsed = Time.timeSinceLevelLoad - loalSearchStart[id];
            if (searchElapsed > Plugin.LOALSearchTime.Value)
            {
                loalSearchStart.Remove(id);
                lastScanTime.Remove(id);
                flareEvadedUnits.Remove(id);
                return true;
            }

            if (missile.EngineOn()) return false;

            bool losingGround = missile.LosingGround();
            bool missedTarget = missile.MissedTarget();
            float selfDestructSpeed = t.Field("selfDestructAtSpeed").GetValue<float>();

            if (losingGround || missedTarget || missile.speed < selfDestructSpeed)
            {
                loalSearchStart.Remove(id);
                lastScanTime.Remove(id);
                flareEvadedUnits.Remove(id);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Clean up tracking dictionaries when missiles are destroyed.
        /// </summary>
        [HarmonyPatch(typeof(Missile), "UnitDisabled")]
        [HarmonyPostfix]
        public static void Missile_UnitDisabled_Postfix(Missile __instance)
        {
            int id = __instance.GetInstanceID();
            loalSearchStart.Remove(id);
            lastScanTime.Remove(id);
            flareEvadedUnits.Remove(id);
        }
    }

    /// <summary>
    /// Snapshot of state before IRSeeker_OnTargetFlare runs, used to detect
    /// whether the flare successfully decoyed the seeker.
    /// </summary>
    public struct FlareEvasionSnapshot
    {
        public bool valid;
        public PersistentID unitId;
        public float irIntensity;
        public IRSource originalIRTarget;
    }
}
