using System.Collections.Generic;
using UnityEngine;
using HarmonyLib;
using Steamworks;
using SailwindCoop.Debug;
using SailwindCoop.Networking.Packets;

namespace SailwindCoop.Sync
{
    /// <summary>
    /// Manages synchronization of boat damage between host and guest.
    /// Host sends state at 1Hz, guest receives and applies.
    /// All packets include BoatName, receivers use FindBoatByName.
    /// </summary>
    public class DamageSyncManager : MonoBehaviour
    {
        public static DamageSyncManager Instance { get; private set; }

        private const float SyncInterval = 1.0f; // 1Hz for damage state
        private const float PumpInputThreshold = 0.01f; // Min change to send

        private float _lastSyncTime;
        private float _lastSentPumpInput;

        // N-player (Phase 5): one pump-input entry per pumping peer. Multiple crew can work the bilge at
        // once; vanilla runs each BilgePump independently and their drains ADD, so the host sums every
        // pumping peer's drain per boat. The OLD single (_guestPumpInput/_guestPumpBoatName) slot became
        // this per-SteamId map; at N=1 there is exactly one entry, so a lone guest's pump drain is
        // identical to the old single-slot behavior.
        private struct GuestPumpState
        {
            public string BoatName;
            public float Input;
        }
        private readonly Dictionary<SteamId, GuestPumpState> _guestPumps = new Dictionary<SteamId, GuestPumpState>();

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

            Plugin.Profiler?.EndMeasure("Damage");
        }

        #region Host Methods

        private void UpdateHost()
        {
            // Send damage state at 1Hz
            if (Time.time - _lastSyncTime >= SyncInterval)
            {
                _lastSyncTime = Time.time;
                SendDamageState();
            }

            // Apply stored guest pump input
            ApplyGuestPumpDrain();
        }

        private void SendDamageState()
        {
            var boat = GameState.lastBoat;
            if (boat == null) return;

            var damage = boat.GetComponent<BoatDamage>();
            if (damage == null) return;

            var packet = new DamageStatePacket
            {
                BoatName = boat.name,
                WaterLevel = damage.waterLevel,
                HullDamage = damage.hullDamage,
                Oakum = damage.oakum,
                Sunk = damage.sunk
            };

            VerboseLogger.DamageSend($"DamageState, boat={boat.name}, water={packet.WaterLevel:F3}, hull={packet.HullDamage:F3}, oakum={packet.Oakum:F1}, sunk={packet.Sunk}", throttle: true);

            Plugin.NetworkManager.SendToAllReliable(PacketType.DamageState, w =>
                PacketSerializer.WriteDamageState(w, packet));
        }

        /// <summary>
        /// Called by DamagePatches when Impact() fires on host.
        /// </summary>
        public void OnLocalImpact(BoatDamage damage)
        {
            if (!Plugin.IsHost) return;

            // Get boat name from the damage component's parent
            var boatName = damage.GetComponent<SaveableObject>()?.gameObject.name ?? "";

            var packet = new DamageImpactPacket
            {
                BoatName = boatName,
                HullDamage = damage.hullDamage
            };

            VerboseLogger.DamageEvent($"Impact occurred, boat={boatName}, hull={packet.HullDamage:F3}");
            VerboseLogger.DamageSend($"DamageImpact, boat={boatName}, hull={packet.HullDamage:F3}");

            Plugin.NetworkManager.SendToAllReliable(PacketType.DamageImpact, w =>
                PacketSerializer.WriteDamageImpact(w, packet));
        }

        public void OnGuestPumpInputReceived(SteamId sender, GuestPumpInputPacket packet)
        {
            if (!Plugin.IsHost) return;

            VerboseLogger.DamageRecv($"GuestPumpInput, boat={packet.BoatName}, input={packet.PumpInput:F2}, from={sender}");

            // Record/replace THIS peer's pump input (keyed by sender). When this peer stops pumping it
            // sends input 0 (PollAndSendPumpInput only fires on change), which we drop from the map so it
            // no longer contributes drain. At N=1 there is a single entry == the old single slot.
            if (packet.PumpInput <= 0f)
                _guestPumps.Remove(sender);
            else
                _guestPumps[sender] = new GuestPumpState { BoatName = packet.BoatName, Input = packet.PumpInput };
        }

        /// <summary>
        /// Called when guest sends oakum repair request.
        /// Host finds the item, applies repair, and syncs item amount back.
        /// </summary>
        public void OnGuestOakumRepairReceived(GuestOakumRepairPacket packet)
        {
            if (!Plugin.IsHost) return;

            VerboseLogger.DamageRecv($"GuestOakumRepair, boat={packet.BoatName}, itemId={packet.ItemInstanceId}");

            // Find the oakum item
            var item = ItemSyncManager.FindItemByInstanceId(packet.ItemInstanceId);
            if (item == null)
            {
                Plugin.Log.LogWarning($"OnGuestOakumRepairReceived: item {packet.ItemInstanceId} not found");
                return;
            }

            var oakum = item as ShipItemOakum;
            if (oakum == null)
            {
                Plugin.Log.LogWarning($"OnGuestOakumRepairReceived: item {packet.ItemInstanceId} is not oakum");
                return;
            }

            // Look up boat by name
            var boatSaveable = BoatUtility.FindBoatByName(packet.BoatName);
            if (boatSaveable == null) return;

            var damage = boatSaveable.GetComponent<BoatDamage>();
            if (damage == null) return;

            // Replicate OnAltActivate logic
            float needed = damage.hullDamage * damage.waterUnitsCapacity - damage.oakum;
            if (oakum.amount < needed)
            {
                needed = oakum.amount;
            }

            if (needed <= 0.01f)
            {
                VerboseLogger.DamageEvent($"GuestOakumRepair: no repair needed (needed={needed:F3})");
                return;
            }

            // Apply repair
            oakum.amount -= needed;
            damage.oakum += needed;

            VerboseLogger.DamageEvent($"GuestOakumRepair applied: added={needed:F3}, item.amount={oakum.amount:F3}, boat.oakum={damage.oakum:F3}");

            // Sync item amount change back to guest
            ItemSyncManager.Instance?.OnLocalItemAmountChanged(oakum);

            // Play sound on host (guest won't hear it, but that's acceptable)
            UISoundPlayer.instance?.PlayUISound(UISounds.oakum, 1f, 1f);
        }

        /// <summary>
        /// Called when guest sends bail request (bucket/bottle removing water).
        /// Host applies the water level decrease.
        /// </summary>
        public void OnGuestBailRequestReceived(GuestBailRequestPacket packet)
        {
            if (!Plugin.IsHost) return;

            VerboseLogger.DamageRecv($"GuestBailRequest, boat={packet.BoatName}, bottleId={packet.BottleInstanceId}, amount={packet.AmountBailed:F2}");

            // Look up boat by name
            var boatSaveable = BoatUtility.FindBoatByName(packet.BoatName);
            if (boatSaveable == null) return;

            var damage = boatSaveable.GetComponent<BoatDamage>();
            if (damage == null || damage.sunk) return;

            // Apply the water level decrease (same formula as game)
            float waterDecrease = packet.AmountBailed * (1f / damage.waterUnitsCapacity);
            damage.waterLevel = Mathf.Max(0f, damage.waterLevel - waterDecrease);

            VerboseLogger.DamageEvent($"GuestBail applied: amount={packet.AmountBailed:F2}, waterLevel={damage.waterLevel:F3}");
        }

        private void ApplyGuestPumpDrain()
        {
            if (_guestPumps.Count == 0) return;

            // N-player (Phase 5): SUM each pumping peer's input PER BOAT, then apply one drain per boat.
            // Vanilla runs each BilgePump independently and the drains add, so summing the inputs and
            // multiplying by the boat's drainRate reproduces that (every bilge on a boat shares drainRate).
            // At N=1 there is one peer -> one boat with its single input == the old single-slot drain.
            _pumpInputByBoat.Clear();
            foreach (var kvp in _guestPumps)
            {
                var state = kvp.Value;
                if (state.Input <= 0f || string.IsNullOrEmpty(state.BoatName)) continue;
                _pumpInputByBoat.TryGetValue(state.BoatName, out var sum);
                _pumpInputByBoat[state.BoatName] = sum + state.Input;
            }

            foreach (var kvp in _pumpInputByBoat)
            {
                float totalInput = kvp.Value;
                if (totalInput <= 0f) continue;

                // Look up boat by name
                var boatSaveable = BoatUtility.FindBoatByName(kvp.Key);
                if (boatSaveable == null) continue;
                var boat = boatSaveable.transform;

                var damage = boatSaveable.GetComponent<BoatDamage>();
                if (damage == null || damage.sunk) continue;

                // Find bilge pump to get drain rate
                var pump = boat.GetComponentInChildren<BilgePump>();
                if (pump == null) continue;

                float drainRate = Traverse.Create(pump).Field("drainRate").GetValue<float>();

                // Apply summed drain from all peers pumping this boat (shared bilge - host-authoritative)
                float drain = Time.deltaTime * totalInput * drainRate;
                damage.waterLevel = Mathf.Max(0f, damage.waterLevel - drain);

                // INDEPENDENT NEEDS: each guest now drains its OWN food/water from pumping (vanilla
                // BilgePump runs locally on the guest), so the host no longer drains stats here.
            }
        }

        // Scratch accumulator reused each tick (no per-frame alloc) for summing pump input per boat.
        private readonly Dictionary<string, float> _pumpInputByBoat = new Dictionary<string, float>();

        #endregion

        #region Guest Methods

        private void UpdateGuest()
        {
            // Poll pump input and send if changed
            PollAndSendPumpInput();
        }

        private void PollAndSendPumpInput()
        {
            var boat = GameState.lastBoat;
            if (boat == null) return;

            var pump = boat.GetComponentInChildren<BilgePump>();
            if (pump == null) return;

            float currentInput = Traverse.Create(pump).Field("currentInput").GetValue<float>();

            // Only send if changed significantly
            if (Mathf.Abs(currentInput - _lastSentPumpInput) < PumpInputThreshold)
                return;

            _lastSentPumpInput = currentInput;

            var packet = new GuestPumpInputPacket
            {
                BoatName = boat.name,
                PumpInput = currentInput
            };

            VerboseLogger.DamageLocal($"Pump input changed, boat={boat.name}, input={currentInput:F2}");
            VerboseLogger.DamageSend($"GuestPumpInput, boat={boat.name}, input={currentInput:F2}");

            Plugin.NetworkManager.SendToAllReliable(PacketType.GuestPumpInput, w =>
                PacketSerializer.WriteGuestPumpInput(w, packet));
        }

        public void OnDamageStateReceived(DamageStatePacket packet)
        {
            if (Plugin.IsHost) return;

            VerboseLogger.DamageRecv($"DamageState, boat={packet.BoatName}, water={packet.WaterLevel:F3}, hull={packet.HullDamage:F3}, oakum={packet.Oakum:F1}, sunk={packet.Sunk}");

            ApplyDamageState(packet);
        }

        public void OnDamageImpactReceived(DamageImpactPacket packet)
        {
            if (Plugin.IsHost) return;

            VerboseLogger.DamageRecv($"DamageImpact, boat={packet.BoatName}, hull={packet.HullDamage:F3}");

            // Look up boat by name
            var boatSaveable = BoatUtility.FindBoatByName(packet.BoatName);
            if (boatSaveable == null) return;

            var damage = boatSaveable.GetComponent<BoatDamage>();
            if (damage == null) return;

            damage.hullDamage = packet.HullDamage;
            VerboseLogger.DamageApply($"Impact applied, boat={packet.BoatName}, hull={packet.HullDamage:F3}");
        }

        private void ApplyDamageState(DamageStatePacket packet)
        {
            // Look up boat by name
            var boatSaveable = BoatUtility.FindBoatByName(packet.BoatName);
            if (boatSaveable == null) return;

            var damage = boatSaveable.GetComponent<BoatDamage>();
            if (damage == null) return;

            damage.waterLevel = packet.WaterLevel;
            damage.hullDamage = packet.HullDamage;
            damage.oakum = packet.Oakum;

            // Handle sink state transition - SYMMETRIC. A one-way version (only ever
            // set sunk=true), so an ashore guest whose boat sank and was then host-recovered kept stale
            // sunk=true forever: the only sunk=false writer is the join coroutine, which the BS2 recovery-skip
            // bypasses for an ashore guest, and the 1Hz DamageState never cleared it. Mirror the host's flag.
            if (packet.Sunk && !damage.sunk)
            {
                damage.sunk = true;
                VerboseLogger.DamageEvent($"Boat {packet.BoatName} sunk (from host state)");
            }
            else if (!packet.Sunk && damage.sunk)
            {
                damage.sunk = false;
                VerboseLogger.DamageEvent($"Boat {packet.BoatName} un-sunk / recovered (from host state)");
            }

            VerboseLogger.DamageApply($"State applied, boat={packet.BoatName}, water={packet.WaterLevel:F3}");
        }

        // ApplyLocalPumpDrain was removed: vanilla BilgePump.Update already drains waterLevel locally on
        // the guest for optimistic feel; this method double-drained it, so its only caller (BilgePumpUpdatePatch)
        // no longer invokes it. Guest pump intent still reaches the host via PollAndSendPumpInput.

        /// <summary>
        /// Send oakum repair request to host.
        /// Called by DamagePatches when guest uses oakum item.
        /// </summary>
        public void SendOakumRepairRequest(int itemInstanceId)
        {
            if (Plugin.IsHost) return;

            var boat = GameState.lastBoat;
            var boatName = boat?.name ?? "";

            var packet = new GuestOakumRepairPacket
            {
                BoatName = boatName,
                ItemInstanceId = itemInstanceId
            };

            VerboseLogger.DamageSend($"GuestOakumRepair, boat={boatName}, itemId={itemInstanceId}");

            Plugin.NetworkManager.SendToAllReliable(PacketType.GuestOakumRepair, w =>
                PacketSerializer.WriteGuestOakumRepair(w, packet));
        }

        /// <summary>
        /// Send bail request to host when guest uses bucket/bottle to remove water.
        /// Called by DamagePatches when guest bails water.
        /// </summary>
        public void SendBailRequest(int bottleInstanceId, float amountBailed)
        {
            if (Plugin.IsHost) return;

            var boat = GameState.lastBoat;
            var boatName = boat?.name ?? "";

            var packet = new GuestBailRequestPacket
            {
                BoatName = boatName,
                BottleInstanceId = bottleInstanceId,
                AmountBailed = amountBailed
            };

            VerboseLogger.DamageSend($"GuestBailRequest, boat={boatName}, bottleId={bottleInstanceId}, amount={amountBailed:F2}");

            Plugin.NetworkManager.SendToAllReliable(PacketType.GuestBailRequest, w =>
                PacketSerializer.WriteGuestBailRequest(w, packet));
        }

        #endregion

        /// <summary>
        /// Drop ONE peer's pump input (called on that peer's disconnect). A guest leaving mid-pump never
        /// sends a 0-input update, so without this its drain would persist. Other pumpers are untouched.
        /// At N=1 the dropped peer is the only pumper, so this matches the old full Reset of the pump slot.
        /// </summary>
        public void OnPeerDisconnected(SteamId peer)
        {
            if (_guestPumps.Remove(peer))
                VerboseLogger.DamageEvent($"Pump input cleared for disconnected peer {peer}");
        }

        public void Reset()
        {
            _lastSyncTime = 0f;
            _lastSentPumpInput = 0f;
            _guestPumps.Clear();
            _pumpInputByBoat.Clear();
        }
    }
}
