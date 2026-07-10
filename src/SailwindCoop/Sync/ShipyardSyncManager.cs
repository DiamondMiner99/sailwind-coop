using System.Linq;
using UnityEngine;
using SailwindCoop.Debug;
using SailwindCoop.Networking.Packets;

namespace SailwindCoop.Sync
{
    /// <summary>
    /// Syncs boat customization changes while at shipyard.
    /// Polls at 5Hz when GameState.currentShipyard is non-null.
    /// </summary>
    public class ShipyardSyncManager : MonoBehaviour
    {
        public static ShipyardSyncManager Instance { get; private set; }

        private const float PollInterval = 0.2f; // 5 Hz
        private float _lastPollTime;

        // Cached state for change detection
        private bool[] _lastMastsEnabled;
        private NetworkSailData[] _lastSails;
        private int[] _lastPartOptions;
        private bool _wasAtShipyard;

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

            bool atShipyard = GameState.currentShipyard != null;

            // Detect shipyard entry/exit
            if (atShipyard && !_wasAtShipyard)
            {
                OnEnterShipyard();
            }
            else if (!atShipyard && _wasAtShipyard)
            {
                OnExitShipyard();
            }

            _wasAtShipyard = atShipyard;

            // Poll for changes while at shipyard
            if (atShipyard)
            {
                PollForChanges();
            }

            Plugin.Profiler?.EndMeasure("Shipyard");
        }

        private void OnEnterShipyard()
        {
            VerboseLogger.ShipyardPoll("Entered shipyard mode");
            // Cache current state
            CacheCurrentState();
        }

        private void OnExitShipyard()
        {
            VerboseLogger.ShipyardPoll("Exited shipyard mode");
            // Clear cache
            _lastMastsEnabled = null;
            _lastSails = null;
            _lastPartOptions = null;

            // Belt-and-braces for the phantom-furled-sails fix: a change landing in the same frame as the
            // exit would be cached-then-missed, so re-invalidate on the way out, and (host only) re-seed
            // every current rope length so crew whose sails were rebuilt to defaults by LoadData converge
            // immediately instead of waiting for the next host winch movement.
            var boat = BoatUtility.GetCurrentBoat();
            if (boat != null) BoatUtility.InvalidateRopeCache(boat);
            if (Plugin.IsHost) ControlSyncManager.Instance?.ResendRopeForCurrentBoat();
        }

        private void PollForChanges()
        {
            if (Time.time - _lastPollTime < PollInterval) return;
            _lastPollTime = Time.time;

            var boat = BoatUtility.GetCurrentBoat();
            if (boat == null) return;

            var customization = boat.GetComponent<SaveableBoatCustomization>();
            if (customization == null) return;

            var data = customization.GetData();
            if (data == null) return;

            // Check if anything changed
            if (!HasCustomizationChanged(data))
            {
                VerboseLogger.ShipyardPoll($"No changes, masts={data.masts?.Count(m => m) ?? 0}, sails={data.sails?.Count ?? 0}", throttle: true);
                return;
            }

            // Send update
            SendCustomizationUpdate(boat, data);

            // Update cache
            CacheState(data);

            // Phantom-furled sails (Robin report, v0.2.25): a LOCAL shipyard sail change destroys the old
            // sail GameObjects (and their RopeControllers) and instantiates new ones, but only the RECEIVER
            // path (ApplyCustomization) invalidated the rope cache. The changer's own machine kept polling
            // the stale cached RopeController[] (destroyed entries read null and are skipped; the NEW reef/
            // sheet ropes are absent), so its unfurl/trim changes were never broadcast and the crew saw the
            // sails furled forever while the boat visibly sailed. Invalidate on every detected local change.
            BoatUtility.InvalidateRopeCache(boat);
        }

        private bool HasCustomizationChanged(SaveBoatCustomizationData data)
        {
            // First time - always changed
            if (_lastMastsEnabled == null) return true;

            // Check masts
            var currentMasts = data.masts ?? new bool[30];
            if (_lastMastsEnabled.Length != currentMasts.Length) return true;
            for (int i = 0; i < currentMasts.Length; i++)
            {
                if (_lastMastsEnabled[i] != currentMasts[i]) return true;
            }

            // Check sails count
            var currentSails = data.sails;
            if ((_lastSails?.Length ?? 0) != (currentSails?.Count ?? 0)) return true;

            // Check sail details
            if (currentSails != null && _lastSails != null)
            {
                for (int i = 0; i < currentSails.Count && i < _lastSails.Length; i++)
                {
                    var curr = currentSails[i];
                    var last = _lastSails[i];
                    if (curr.prefabIndex != last.PrefabIndex ||
                        curr.mastIndex != last.MastIndex ||
                        curr.sailColor != last.Color ||
                        Mathf.Abs(curr.installHeight - last.InstallHeight) > 0.01f ||
                        Mathf.Abs(curr.minAngle - last.MinAngle) > 0.01f ||
                        Mathf.Abs(curr.maxAngle - last.MaxAngle) > 0.01f ||
                        Mathf.Abs(curr.scaleY - last.ScaleY) > 0.001f ||      // BS1-live: detect a sail resize
                        Mathf.Abs(curr.scaleZ - last.ScaleZ) > 0.001f)
                        return true;
                }
            }

            // Check part options
            var currentParts = data.partActiveOptions;
            if ((_lastPartOptions?.Length ?? 0) != (currentParts?.Count ?? 0)) return true;
            if (currentParts != null && _lastPartOptions != null)
            {
                for (int i = 0; i < currentParts.Count; i++)
                {
                    if (_lastPartOptions[i] != currentParts[i]) return true;
                }
            }

            return false;
        }

        private void SendCustomizationUpdate(SaveableObject boat, SaveBoatCustomizationData data)
        {
            var packet = new ShipyardCustomizationPacket
            {
                BoatName = boat.gameObject.name,
                MastsEnabled = data.masts ?? new bool[30],
                Sails = data.sails?.Select(s => new NetworkSailData
                {
                    PrefabIndex = s.prefabIndex,
                    MastIndex = s.mastIndex,
                    InstallHeight = s.installHeight,
                    MinAngle = s.minAngle,
                    MaxAngle = s.maxAngle,
                    Health = s.health,
                    Color = s.sailColor,
                    ScaleY = s.scaleY,  // BS1-live
                    ScaleZ = s.scaleZ
                }).ToArray() ?? new NetworkSailData[0],
                PartActiveOptions = data.partActiveOptions?.ToArray() ?? new int[0]
            };

            VerboseLogger.ShipyardSend($"boat={packet.BoatName}, masts={packet.MastsEnabled.Count(m => m)}, sails={packet.Sails.Length}, parts={packet.PartActiveOptions.Length}");

            Plugin.NetworkManager.SendToAllReliable(PacketType.ShipyardCustomization, writer =>
            {
                PacketSerializer.WriteShipyardCustomization(writer, packet);
            });
        }

        private void CacheCurrentState()
        {
            var boat = BoatUtility.GetCurrentBoat();
            if (boat == null) return;

            var customization = boat.GetComponent<SaveableBoatCustomization>();
            if (customization == null) return;

            var data = customization.GetData();
            if (data != null)
            {
                CacheState(data);
            }
        }

        private void CacheState(SaveBoatCustomizationData data)
        {
            _lastMastsEnabled = (bool[])data.masts?.Clone() ?? new bool[30];
            _lastSails = data.sails?.Select(s => new NetworkSailData
            {
                PrefabIndex = s.prefabIndex,
                MastIndex = s.mastIndex,
                InstallHeight = s.installHeight,
                MinAngle = s.minAngle,
                MaxAngle = s.maxAngle,
                Health = s.health,
                Color = s.sailColor,
                ScaleY = s.scaleY,  // BS1-live
                ScaleZ = s.scaleZ
            }).ToArray() ?? new NetworkSailData[0];
            _lastPartOptions = data.partActiveOptions?.ToArray() ?? new int[0];
        }

        /// <summary>
        /// Called when receiving customization packet from other player.
        /// </summary>
        public void OnCustomizationReceived(ShipyardCustomizationPacket packet, Steamworks.SteamId sender = default)
        {
            VerboseLogger.ShipyardRecv($"boat={packet.BoatName}, masts={packet.MastsEnabled?.Count(m => m) ?? 0}, sails={packet.Sails?.Length ?? 0}, parts={packet.PartActiveOptions?.Length ?? 0}");

            // R4.8 N-player audit (star-relay): ShipyardCustomization was the ONE guest-originated state event
            // missing its host relay, so a guest's mast/sail/part change was invisible to OTHER guests at 3+ (and
            // the host folds the snapshot into its change-detection cache, so it never re-broadcasts either). The
            // packet is a full boat-keyed snapshot, so NO author field is needed - just forward it to the other
            // guests. Relay BEFORE FindBoatByName so it still forwards even if the host can't resolve the boat.
            if (Plugin.IsHost)
                Plugin.NetworkManager.SendToAllExcept(sender, PacketType.ShipyardCustomization, w =>
                    PacketSerializer.WriteShipyardCustomization(w, packet));

            var boat = BoatUtility.FindBoatByName(packet.BoatName);
            if (boat == null)
            {
                Plugin.Log.LogWarning($"ShipyardSync: Boat '{packet.BoatName}' not found");
                return;
            }

            ApplyCustomization(boat, packet);

            // Update our cache so we don't immediately re-send
            if (GameState.currentShipyard != null)
            {
                _lastMastsEnabled = (bool[])packet.MastsEnabled?.Clone() ?? new bool[30];
                _lastSails = (NetworkSailData[])packet.Sails?.Clone() ?? new NetworkSailData[0];
                _lastPartOptions = (int[])packet.PartActiveOptions?.Clone() ?? new int[0];
            }
        }

        private void ApplyCustomization(SaveableObject boat, ShipyardCustomizationPacket packet)
        {
            var customization = boat.GetComponent<SaveableBoatCustomization>();
            if (customization == null)
            {
                Plugin.Log.LogWarning($"ShipyardSync: No SaveableBoatCustomization on {boat.gameObject.name}");
                return;
            }

            // Build SaveBoatCustomizationData from packet
            var saveData = new SaveBoatCustomizationData
            {
                masts = packet.MastsEnabled ?? new bool[30],
                sails = packet.Sails?.Select(s => new SaveSailData
                {
                    prefabIndex = s.PrefabIndex,
                    mastIndex = s.MastIndex,
                    installHeight = s.InstallHeight,
                    minAngle = s.MinAngle,
                    maxAngle = s.MaxAngle,
                    health = s.Health,
                    sailColor = s.Color,
                    scaleY = s.ScaleY,  // BS1-live: apply the resized sail scale (Mast.LoadSail gates on scaleY!=0)
                    scaleZ = s.ScaleZ
                }).ToList() ?? new System.Collections.Generic.List<SaveSailData>(),
                partActiveOptions = packet.PartActiveOptions?.ToList() ?? new System.Collections.Generic.List<int>()
            };

            customization.LoadData(saveData);

            // Invalidate rope cache - LoadData destroys old RopeControllers and creates new ones
            BoatUtility.InvalidateRopeCache(boat);

            VerboseLogger.ShipyardApply($"boat={boat.gameObject.name}, applied masts/sails/parts");
        }

        /// <summary>
        /// Reset sync state when disconnecting.
        /// </summary>
        public void Reset()
        {
            _lastPollTime = 0f;
            _lastMastsEnabled = null;
            _lastSails = null;
            _lastPartOptions = null;
            _wasAtShipyard = false;
        }
    }
}
