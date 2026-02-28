using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace AIM9XMod.Patches
{
    /// <summary>
    /// Implements Lock-On After Launch (LOAL) for IR missiles (server-side).
    /// 
    /// In vanilla, if an IR missile loses lock or is launched without one, it drifts
    /// on its last known heading with accumulating error until self-destruct.
    /// 
    /// With LOAL, the seeker actively scans for IR-emitting targets within its search
    /// cone during flight. If it finds one with LOS and within the cone, it acquires
    /// lock and begins tracking — just like the AIM-9X Block II datalink capability.
    /// 
    /// Target-directed steering: when a missile is in LOAL mode and was launched with
    /// a designated target, the server steers the missile toward the target's last known
    /// position. This replaces client-side view-slaving and works for all players on
    /// the server without requiring the mod on clients.
    /// 
    /// Flare evasion memory: when a target successfully decoys the seeker with flares,
    /// the seeker records a relock threshold. By default, this is the aircraft's IR
    /// output at the moment of evasion. With UsePeakIRThreshold enabled, the threshold
    /// is the highest IR the missile ever observed while tracking that unit.
    /// </summary>
    [HarmonyPatch]
    public static class LOALPatch
    {
        // Track which missiles are in LOAL search mode and their search start time
        private static readonly Dictionary<int, float> loalSearchStart = new Dictionary<int, float>();
        private static readonly Dictionary<int, float> lastScanTime = new Dictionary<int, float>();

        // Peak observed IR intensity per unit, tracked while the seeker has active lock.
        // Only used when UsePeakIRThreshold is enabled.
        // Key: missile instance ID -> Dictionary of (unit PersistentID -> peak IR intensity)
        private static readonly Dictionary<int, Dictionary<PersistentID, float>> peakObservedIR
            = new Dictionary<int, Dictionary<PersistentID, float>>();

        // Units that successfully evaded each missile via flares.
        // Stores either the aircraft's IR at moment of evasion, or the peak observed IR,
        // depending on the UsePeakIRThreshold config setting.
        // Key: missile instance ID -> Dictionary of (evaded unit PersistentID -> IR threshold)
        private static readonly Dictionary<int, Dictionary<PersistentID, float>> flareEvadedUnits
            = new Dictionary<int, Dictionary<PersistentID, float>>();

        // The designated target at launch time, used for target-directed LOAL steering.
        // The server steers the missile toward this target's known position when in LOAL mode.
        // Key: missile instance ID -> designated target unit
        private static readonly Dictionary<int, Unit> designatedTarget
            = new Dictionary<int, Unit>();

        /// <summary>
        /// Get the total IR intensity of a unit by summing all non-flare IR sources.
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

        private static void CleanupMissile(int id)
        {
            loalSearchStart.Remove(id);
            lastScanTime.Remove(id);
            peakObservedIR.Remove(id);
            flareEvadedUnits.Remove(id);
            designatedTarget.Remove(id);
        }

        /// <summary>
        /// Patch IRSeeker.Initialize to register missiles for LOAL tracking.
        /// If the target is beyond the seeker cone (e.g. rear-hemisphere launch),
        /// force the missile into LOAL mode so it launches without lock and acquires
        /// after turning.
        /// </summary>
        [HarmonyPatch(typeof(IRSeeker), "Initialize")]
        [HarmonyPostfix]
        public static void IRSeeker_Initialize_Postfix(IRSeeker __instance)
        {
            if (!Plugin.EnableLOAL.Value) return;

            var t = Traverse.Create(__instance);
            var missile = t.Field("missile").GetValue<Missile>();
            if (missile == null) return;

            int id = missile.GetInstanceID();
            loalSearchStart[id] = Time.timeSinceLevelLoad;
            lastScanTime[id] = 0f;
            peakObservedIR[id] = new Dictionary<PersistentID, float>();
            flareEvadedUnits[id] = new Dictionary<PersistentID, float>();

            // Check if the target is outside the seeker cone at launch.
            // This handles rear-hemisphere shots: the missile fires forward,
            // enters LOAL, steers toward the target's known position, and locks
            // on once the target enters the seeker's 90° cone.
            var targetUnit = t.Field("targetUnit").GetValue<Unit>();
            var irTarget = t.Field("IRTarget").GetValue<IRSource>();

            // Store the designated target for target-directed LOAL steering.
            // Even if we have lock now, we keep this in case we lose lock later.
            if (targetUnit != null)
                designatedTarget[id] = targetUnit;

            if (targetUnit != null && irTarget != null && !irTarget.flare)
            {
                Vector3 toTarget = targetUnit.transform.position - missile.transform.position;
                float angle = Vector3.Angle(missile.transform.forward, toTarget);
                if (angle > Plugin.OffBoresightAngle.Value)
                {
                    // Target is outside seeker cone — force LOAL mode
                    t.Field("IRTarget").SetValue(null);
                    t.Field("targetUnit").SetValue(null);
                    t.Field("achievedLock").SetValue(false);

                    Plugin.Log.LogDebug(
                        $"[LOAL] Rear-hemisphere launch: target at {angle:F0}° off-bore, " +
                        $"exceeds {Plugin.OffBoresightAngle.Value}° seeker cone. Entering LOAL.");
                }
            }
        }

        /// <summary>
        /// Patch IRSeeker.Seek to implement LOAL scanning when the seeker has no lock.
        /// Scans for targets within the search cone, respecting flare evasion memory.
        /// </summary>
        [HarmonyPatch(typeof(IRSeeker), "Seek")]
        [HarmonyPostfix]
        public static void IRSeeker_Seek_Postfix(IRSeeker __instance)
        {
            if (!Plugin.EnableLOAL.Value) return;

            var t = Traverse.Create(__instance);
            var missile = t.Field("missile").GetValue<Missile>();
            if (missile == null || missile.disabled) return;

            int id = missile.GetInstanceID();
            var irTarget = t.Field("IRTarget").GetValue<IRSource>();
            var targetUnit = t.Field("targetUnit").GetValue<Unit>();

            // If we have active lock, update peak IR tracking if enabled, then return.
            if (irTarget != null && irTarget.transform != null && targetUnit != null)
            {
                if (Plugin.UsePeakIRThreshold.Value)
                {
                    Dictionary<PersistentID, float> peaks;
                    if (peakObservedIR.TryGetValue(id, out peaks))
                    {
                        float currentIR = GetTotalIRIntensity(targetUnit);
                        PersistentID uid = targetUnit.persistentID;
                        float existing;
                        if (!peaks.TryGetValue(uid, out existing) || currentIR > existing)
                            peaks[uid] = currentIR;
                    }
                }
                return;
            }

            // --- LOAL scanning when we have no lock ---
            bool guidance = t.Field("guidance").GetValue<bool>();
            if (!guidance) return;

            if (!loalSearchStart.ContainsKey(id)) return;

            float searchElapsed = Time.timeSinceLevelLoad - loalSearchStart[id];
            if (searchElapsed > Plugin.LOALSearchTime.Value) return;

            // Steer the missile toward the designated target's known position.
            // This is the server-side equivalent of view-slaving — the server knows
            // the target from the launch command and steers the missile toward it
            // during LOAL, so it works for all players without client-side mod.
            if (Plugin.EnableTargetDirectedLOAL.Value)
            {
                Unit designatedUnit;
                if (designatedTarget.TryGetValue(id, out designatedUnit)
                    && designatedUnit != null && !designatedUnit.disabled)
                {
                    GlobalPosition targetPos = designatedUnit.GlobalPosition();
                    Vector3 targetVel = designatedUnit.rb != null
                        ? designatedUnit.rb.velocity : Vector3.zero;
                    missile.SetAimpoint(targetPos, targetVel);
                }
            }

            // Throttle scanning to every 0.25s
            if (!lastScanTime.ContainsKey(id)) lastScanTime[id] = 0f;
            if (Time.timeSinceLevelLoad - lastScanTime[id] < 0.25f) return;
            lastScanTime[id] = Time.timeSinceLevelLoad;

            // Get the flare evasion blacklist for this missile
            Dictionary<PersistentID, float> evadedLookup = null;
            flareEvadedUnits.TryGetValue(id, out evadedLookup);

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

                // Range check (do early to avoid expensive angle/LOS checks)
                Vector3 toTarget = unit.transform.position - missilePos;
                float dist = toTarget.magnitude;
                if (dist > maxRange || dist < 50f) continue;

                // Cone check — must be within seeker search angle
                float angle = Vector3.Angle(missileForward, toTarget);
                if (angle > searchAngle) continue;

                // --- Flare evasion memory check ---
                // Only allow relock if the aircraft's current IR output exceeds
                // what it was putting out when it successfully flared.
                if (evadedLookup != null)
                {
                    float evasionIR;
                    if (evadedLookup.TryGetValue(unit.persistentID, out evasionIR))
                    {
                        float currentIR = GetTotalIRIntensity(unit);
                        if (currentIR <= evasionIR)
                            continue; // Aircraft hasn't increased throttle — skip
                    }
                }

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

                missile.SetTarget(bestTarget);

                // Remove from LOAL search — we have lock now
                // Keep peakObservedIR and flareEvadedUnits for continued tracking
                loalSearchStart.Remove(id);
                lastScanTime.Remove(id);

                Plugin.Log.LogDebug(
                    $"[LOAL] Acquired lock on {bestTarget.unitName} at {bestScore:F1} score");
            }
        }

        /// <summary>
        /// Snapshot state before IRSeeker_OnTargetFlare runs so we can detect
        /// whether the flare successfully decoyed the seeker.
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

            if (targetUnit != null && irTarget != null && !irTarget.flare)
            {
                __state = new FlareEvasionSnapshot
                {
                    valid = true,
                    unitId = targetUnit.persistentID,
                    originalIRTarget = irTarget
                };
            }
        }

        /// <summary>
        /// After the vanilla flare logic runs, check if lock transferred to the flare.
        /// If so, store the peak observed IR for that unit as the evasion threshold.
        /// </summary>
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

            // If IRTarget changed, the flare won
            if (currentIRTarget != __state.originalIRTarget)
            {
                int missileId = missile.GetInstanceID();

                float thresholdIR = 0f;

                if (Plugin.UsePeakIRThreshold.Value)
                {
                    // Use the highest IR the missile ever observed while tracking this unit
                    Dictionary<PersistentID, float> peaks;
                    if (peakObservedIR.TryGetValue(missileId, out peaks))
                        peaks.TryGetValue(__state.unitId, out thresholdIR);
                }

                // Fall back to (or use) the aircraft's current IR at moment of evasion
                if (thresholdIR <= 0f)
                {
                    Unit evadedUnit;
                    if (UnitRegistry.TryGetUnit(new PersistentID?(__state.unitId), out evadedUnit))
                        thresholdIR = GetTotalIRIntensity(evadedUnit);
                }

                // Store as the relock threshold
                if (!flareEvadedUnits.ContainsKey(missileId))
                    flareEvadedUnits[missileId] = new Dictionary<PersistentID, float>();

                flareEvadedUnits[missileId][__state.unitId] = thresholdIR;

                // Re-enter LOAL search mode
                if (!loalSearchStart.ContainsKey(missileId))
                    loalSearchStart[missileId] = Time.timeSinceLevelLoad;

                string mode = Plugin.UsePeakIRThreshold.Value ? "peak observed" : "at evasion";
                Plugin.Log.LogDebug(
                    $"[LOAL] Flare evasion: unit {__state.unitId}, IR threshold ({mode}): {thresholdIR:F2}. " +
                    $"Relock requires aircraft IR > {thresholdIR:F2} while in seeker cone.");
            }
        }

        /// <summary>
        /// Prevent premature self-destruct during LOAL search.
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
                CleanupMissile(id);
                return true;
            }

            if (missile.EngineOn()) return false;

            bool losingGround = missile.LosingGround();
            bool missedTarget = missile.MissedTarget();
            float selfDestructSpeed = t.Field("selfDestructAtSpeed").GetValue<float>();

            if (losingGround || missedTarget || missile.speed < selfDestructSpeed)
            {
                CleanupMissile(id);
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
            CleanupMissile(__instance.GetInstanceID());
        }
    }

    /// <summary>
    /// Snapshot of state before IRSeeker_OnTargetFlare runs.
    /// </summary>
    public struct FlareEvasionSnapshot
    {
        public bool valid;
        public PersistentID unitId;
        public IRSource originalIRTarget;
    }
}
