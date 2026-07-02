using UnityEngine;
using HarmonyLib;
using SailwindCoop.Debug;
using SailwindCoop.Networking.Packets;

namespace SailwindCoop.Sync
{
    /// <summary>
    /// Manages synchronization of survival stats between host and guest.
    /// Host sends stats at 1Hz, guest sends activity state and consumption deltas.
    /// </summary>
    public class SurvivalSyncManager : MonoBehaviour
    {
        public static SurvivalSyncManager Instance { get; private set; }

        private const float SyncInterval = 1.0f; // 1Hz for stats
        private const float ActivitySyncInterval = 0.5f; // 2Hz for activity

        private float _lastStatsSyncTime;
        private float _lastActivitySyncTime;

        // Cached activity state to detect changes
        private ActivityFlags _lastActivityFlags;
        private TobaccoType _lastTobaccoType;

        // Accumulated movement for activity drain (sum of outLastMovement.sqrMagnitude per frame)
        // This matches how the game accumulates drain in DrainEnergyFromMovement() each LateUpdate
        private float _accumulatedMovementSqrMag;

        private void Awake()
        {
            if (Instance != null)
            {
                Destroy(this);
                return;
            }
            Instance = this;
        }

        private void Update()
        {
            if (!Plugin.IsMultiplayer) return;

            Plugin.Profiler?.StartMeasure();

            if (Plugin.IsHost)
            {
                UpdateHost();
            }
            else
            {
                UpdateGuest();
            }

            Plugin.Profiler?.EndMeasure("Survival");
        }

        private void UpdateHost()
        {
            // Send stats at 1Hz
            if (Time.time - _lastStatsSyncTime >= SyncInterval)
            {
                _lastStatsSyncTime = Time.time;
                SendSurvivalStats();
            }
        }

        private void UpdateGuest()
        {
            // Maintain eatCooldown since LateUpdate is disabled
            if (PlayerNeeds.instance != null && PlayerNeeds.instance.eatCooldown > 0f)
            {
                PlayerNeeds.instance.eatCooldown -= Time.deltaTime;
            }

            // Accumulate movement sqrMagnitude each frame (matches game's DrainEnergyFromMovement per-frame accumulation)
            _accumulatedMovementSqrMag += GetFrameMovementSqrMagnitude();

            // Send activity state at 2Hz when active
            if (Time.time - _lastActivitySyncTime >= ActivitySyncInterval)
            {
                _lastActivitySyncTime = Time.time;
                SendActivityStateIfNeeded();
            }
        }

        #region Host Methods

        private void SendSurvivalStats()
        {
            // INDEPENDENT NEEDS: each client keeps its own needs. The host no longer mirrors stats to
            // the guest (this also disables SendStatsImmediate, which calls through here).
            return;

#pragma warning disable CS0162 // unreachable code (kept for reference)
            var packet = new SurvivalStatsPacket
            {
                Food = PlayerNeeds.food,
                Water = PlayerNeeds.water,
                Sleep = PlayerNeeds.sleep,
                FoodDebt = PlayerNeeds.foodDebt,
                SleepDebt = PlayerNeeds.sleepDebt,
                Alcohol = PlayerNeeds.alcohol,
                Vitamins = PlayerNeeds.vitamins,
                Protein = PlayerNeeds.protein
            };

            VerboseLogger.SurvivalSend($"Stats, food={packet.Food:F1}, water={packet.Water:F1}, sleep={packet.Sleep:F1}", throttle: true);

            Plugin.NetworkManager.SendToAllReliable(PacketType.SurvivalStats, w =>
                PacketSerializer.WriteSurvivalStats(w, packet));
#pragma warning restore CS0162
        }

        /// <summary>
        /// Send stats immediately (called on guest join and after recovery).
        /// </summary>
        public void SendStatsImmediate()
        {
            if (!Plugin.IsHost) return;
            _lastStatsSyncTime = Time.time;
            SendSurvivalStats();
        }

        public void OnActivityStateReceived(ActivityStatePacket packet)
        {
            // INDEPENDENT NEEDS: ignore stale activity packets - each player drains its own needs.
            return;

#pragma warning disable CS0162
            if (!Plugin.IsHost) return;

            VerboseLogger.SurvivalRecv($"ActivityState, flags={packet.Flags}, tobacco={packet.TobaccoType}");

            ApplyGuestActivityDrain(packet);
#pragma warning restore CS0162
        }

        private void ApplyGuestActivityDrain(ActivityStatePacket packet)
        {
            // Apply drain formulas for guest's activity
            // These mirror what DrainEnergyFromMovement does locally

            if (packet.Flags.HasFlag(ActivityFlags.Running))
            {
                float waterCost = PlayerNeeds.instance?.runningWaterCost ?? 0.2f;

                // MovementSqrMagnitude is now accumulated over the sync interval (sum of per-frame sqrMagnitudes)
                // This matches how the game accumulates drain via DrainEnergyFromMovement() each frame
                float sqrMag = packet.MovementSqrMagnitude;

                if (packet.Flags.HasFlag(ActivityFlags.Swimming))
                {
                    // Swimming + running: drain both food and water
                    float foodCost = PlayerNeeds.instance?.swimmingFoodCost ?? 0.1f;
                    float swimmingWaterCost = PlayerNeeds.instance?.swimmingWaterCost ?? 0.3f;

                    if (PlayerNeeds.food < 0f)
                        PlayerNeeds.foodDebt -= sqrMag * foodCost;
                    else
                        PlayerNeeds.food -= sqrMag * foodCost;

                    PlayerNeeds.water -= sqrMag * swimmingWaterCost;
                }
                else
                {
                    // Running only: drain water
                    PlayerNeeds.water -= sqrMag * waterCost;
                }
            }

            if (packet.Flags.HasFlag(ActivityFlags.Pumping))
            {
                // BilgePump drain - use actual pump intensity from packet
                // Formula: drain = Time.deltaTime * cost * currentInput
                // We use ActivitySyncInterval as the time delta, and packet.PumpIntensity as currentInput

                // Read actual costs from BilgePump instance (Unity serialized fields)
                float waterCost = 0.5f;  // Fallback
                float foodCost = 0.3f;   // Fallback

                var pump = FindObjectOfType<BilgePump>();
                if (pump != null)
                {
                    try
                    {
                        waterCost = Traverse.Create(pump).Field("waterCost").GetValue<float>();
                        foodCost = Traverse.Create(pump).Field("foodCost").GetValue<float>();
                    }
                    catch
                    {
                        // Use fallback values if reflection fails
                    }
                }

                float drain = ActivitySyncInterval * packet.PumpIntensity;

                PlayerNeeds.water -= drain * waterCost;
                PlayerNeeds.food -= drain * foodCost;
            }

            if (packet.Flags.HasFlag(ActivityFlags.Smoking))
            {
                // Tobacco effects while smoking
                // Apply effect for the ActivitySyncInterval duration (0.5s)
                float sleepDelta = packet.TobaccoType switch
                {
                    TobaccoType.Cigarette => 0.66f,
                    TobaccoType.Cigar => -0.11f,
                    TobaccoType.Pipe => 1.2f,
                    TobaccoType.Hookah => 0.8f,
                    _ => 0f
                };

                PlayerNeeds.sleep += sleepDelta * ActivitySyncInterval;
            }

            VerboseLogger.SurvivalApply($"Guest activity drain applied, sqrMag={packet.MovementSqrMagnitude:F2}, pump={packet.PumpIntensity:F2}");
        }

        public void OnConsumptionDeltaReceived(ConsumptionDeltaPacket packet)
        {
            // INDEPENDENT NEEDS: ignore stale consumption deltas - eating only feeds the local eater.
            return;

#pragma warning disable CS0162
            if (!Plugin.IsHost) return;

            VerboseLogger.SurvivalRecv($"ConsumptionDelta, food={packet.DeltaFood:F1}, water={packet.DeltaWater:F1}");

            // Apply deltas from guest consumption
            PlayerNeeds.food += packet.DeltaFood;
            PlayerNeeds.water += packet.DeltaWater;
            PlayerNeeds.sleep += packet.DeltaSleep;
            PlayerNeeds.foodDebt += packet.DeltaFoodDebt;
            PlayerNeeds.sleepDebt += packet.DeltaSleepDebt;
            PlayerNeeds.alcohol += packet.DeltaAlcohol;
            PlayerNeeds.vitamins += packet.DeltaVitamins;
            PlayerNeeds.protein += packet.DeltaProtein;

            VerboseLogger.SurvivalApply($"Guest consumption applied");
#pragma warning restore CS0162
        }

        #endregion

        #region Guest Methods

        private void SendActivityStateIfNeeded()
        {
            // INDEPENDENT NEEDS: guest no longer ships its activity/movement drain to the host.
            return;

#pragma warning disable CS0162
            var flags = GetCurrentActivityFlags(out float pumpIntensity);
            var tobacco = GetCurrentTobaccoType();
            // Get accumulated movement and reset (always reset even if not sending, to prevent stale accumulation)
            var accumulatedMovementSqrMag = GetAccumulatedMovementSqrMagnitude();

            // Only send if something is active
            if (flags == ActivityFlags.None)
            {
                _lastActivityFlags = flags;
                _lastTobaccoType = tobacco;
                return;
            }

            var packet = new ActivityStatePacket
            {
                Flags = flags,
                TobaccoType = tobacco,
                MovementSqrMagnitude = accumulatedMovementSqrMag, // Now sends accumulated value, not snapshot
                PumpIntensity = pumpIntensity
            };

            VerboseLogger.SurvivalSend($"ActivityState, flags={flags}, tobacco={tobacco}, sqrMag={accumulatedMovementSqrMag:F4}, pump={pumpIntensity:F2}");

            Plugin.NetworkManager.SendToAllUnreliable(PacketType.ActivityState, w =>
                PacketSerializer.WriteActivityState(w, packet));

            _lastActivityFlags = flags;
            _lastTobaccoType = tobacco;
#pragma warning restore CS0162
        }

        /// <summary>
        /// Gets the per-frame movement sqrMagnitude from OVRPlayerController.outLastMovement.
        /// This matches what DrainEnergyFromMovement() uses in the game's PlayerNeeds.LateUpdate().
        /// Note: outLastMovement is displacement (meters), not velocity (m/s).
        /// </summary>
        private float GetFrameMovementSqrMagnitude()
        {
            try
            {
                // Access Refs.ovrController.outLastMovement via reflection
                var ovrControllerField = typeof(Refs).GetField("ovrController");
                var ovrController = ovrControllerField?.GetValue(null);
                if (ovrController != null)
                {
                    var outLastMovementField = ovrController.GetType().GetField("outLastMovement");
                    if (outLastMovementField != null)
                    {
                        var outLastMovement = (Vector3)outLastMovementField.GetValue(ovrController);
                        // Zero out Y component (matches game behavior)
                        outLastMovement.y = 0f;
                        return outLastMovement.sqrMagnitude;
                    }
                }
            }
            catch
            {
                // Ignore reflection errors
            }
            return 0f;
        }

        /// <summary>
        /// Gets the accumulated movement sqrMagnitude since last sync and resets accumulator.
        /// This value represents the sum of per-frame sqrMagnitudes over the sync interval.
        /// </summary>
        private float GetAccumulatedMovementSqrMagnitude()
        {
            float accumulated = _accumulatedMovementSqrMag;
            _accumulatedMovementSqrMag = 0f;
            return accumulated;
        }

        private ActivityFlags GetCurrentActivityFlags(out float pumpIntensity)
        {
            var flags = ActivityFlags.None;
            pumpIntensity = 0f;

            // Check running - use reflection since OVRPlayerController is from Oculus.VR assembly
            // which is not referenced in our project
            try
            {
                // Access Refs.ovrController via reflection
                var ovrControllerField = typeof(Refs).GetField("ovrController");
                var ovrController = ovrControllerField?.GetValue(null);
                if (ovrController != null)
                {
                    var isRunningMethod = ovrController.GetType().GetMethod("IsRunning");
                    if (isRunningMethod != null)
                    {
                        bool isRunning = (bool)isRunningMethod.Invoke(ovrController, null);
                        if (isRunning)
                        {
                            flags |= ActivityFlags.Running;
                        }
                    }
                }
            }
            catch
            {
                // Ignore reflection errors
            }

            // Check swimming
            if (PlayerSwimming.swimming)
            {
                flags |= ActivityFlags.Swimming;
            }

            // Check bilge pumping - find active bilge pump
            // BilgePump has private currentInput > 0 when actively pumping
            var pump = FindObjectOfType<BilgePump>();
            if (pump != null)
            {
                try
                {
                    float currentInput = Traverse.Create(pump).Field("currentInput").GetValue<float>();
                    if (currentInput > 0f)
                    {
                        flags |= ActivityFlags.Pumping;
                        pumpIntensity = currentInput;  // Capture actual intensity (0-1)
                    }
                }
                catch
                {
                    // Ignore reflection errors - field may not exist in some game versions
                }
            }

            // Check smoking - PlayerTobacco.Smoke() is called while actively smoking
            // We detect this by checking if tobacco levels are increasing (white, green, black, brown)
            var tobacco = PlayerTobacco.instance;
            if (tobacco != null)
            {
                // If any tobacco level is above a threshold, player is likely smoking
                // Since Smoke() adds Time.deltaTime * 2f per frame, any value above ~1 means active smoking
                if (tobacco.white > 1f || tobacco.green > 1f || tobacco.black > 1f || tobacco.brown > 1f)
                {
                    flags |= ActivityFlags.Smoking;
                }
            }

            return flags;
        }

        private TobaccoType GetCurrentTobaccoType()
        {
            var tobacco = PlayerTobacco.instance;
            if (tobacco == null) return TobaccoType.None;

            // Determine tobacco type by which color is currently highest/increasing
            // white=cigarette(1), green=cigar(2), black=pipe(3), brown=hookah(4)
            float maxValue = 0f;
            TobaccoType activeType = TobaccoType.None;

            if (tobacco.white > maxValue && tobacco.white > 1f)
            {
                maxValue = tobacco.white;
                activeType = TobaccoType.Cigarette;
            }
            if (tobacco.green > maxValue && tobacco.green > 1f)
            {
                maxValue = tobacco.green;
                activeType = TobaccoType.Cigar;
            }
            if (tobacco.black > maxValue && tobacco.black > 1f)
            {
                maxValue = tobacco.black;
                activeType = TobaccoType.Pipe;
            }
            if (tobacco.brown > maxValue && tobacco.brown > 1f)
            {
                maxValue = tobacco.brown;
                activeType = TobaccoType.Hookah;
            }

            return activeType;
        }

        public void OnSurvivalStatsReceived(SurvivalStatsPacket packet)
        {
            // INDEPENDENT NEEDS: ignore mirrored stats - the guest keeps its own needs.
            return;

#pragma warning disable CS0162
            if (Plugin.IsHost) return;

            VerboseLogger.SurvivalRecv($"Stats, food={packet.Food:F1}, water={packet.Water:F1}, sleep={packet.Sleep:F1}", throttle: true);

            // Apply received stats
            PlayerNeeds.food = packet.Food;
            PlayerNeeds.water = packet.Water;
            PlayerNeeds.sleep = packet.Sleep;
            PlayerNeeds.foodDebt = packet.FoodDebt;
            PlayerNeeds.sleepDebt = packet.SleepDebt;
            PlayerNeeds.alcohol = packet.Alcohol;
            PlayerNeeds.vitamins = packet.Vitamins;
            PlayerNeeds.protein = packet.Protein;

            VerboseLogger.SurvivalApply($"Stats applied from host");
#pragma warning restore CS0162
        }

        /// <summary>
        /// Send consumption delta to host. Called by Harmony patches.
        /// </summary>
        public void SendConsumptionDelta(ConsumptionDeltaPacket packet)
        {
            // INDEPENDENT NEEDS: no longer ship consumption to the host.
            return;

#pragma warning disable CS0162
            if (Plugin.IsHost) return;

            // Check if any delta is non-zero
            if (packet.DeltaFood == 0 && packet.DeltaWater == 0 && packet.DeltaSleep == 0 &&
                packet.DeltaFoodDebt == 0 && packet.DeltaSleepDebt == 0 && packet.DeltaAlcohol == 0 &&
                packet.DeltaVitamins == 0 && packet.DeltaProtein == 0)
            {
                return;
            }

            VerboseLogger.SurvivalSend($"ConsumptionDelta, food={packet.DeltaFood:F1}, water={packet.DeltaWater:F1}");

            Plugin.NetworkManager.SendToAllReliable(PacketType.ConsumptionDelta, w =>
                PacketSerializer.WriteConsumptionDelta(w, packet));
#pragma warning restore CS0162
        }

        #endregion

        // NOTE: Recovery handlers removed - recovery now uses BoatWorldState resync
        // (handled by BoatStateApplicator.ApplyWorldState with IsRecoveryResync=true)

        public void Reset()
        {
            _lastStatsSyncTime = 0f;
            _lastActivitySyncTime = 0f;
            _lastActivityFlags = ActivityFlags.None;
            _lastTobaccoType = TobaccoType.None;
            _accumulatedMovementSqrMag = 0f;
        }
    }
}
