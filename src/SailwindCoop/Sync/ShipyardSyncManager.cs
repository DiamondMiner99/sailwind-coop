using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
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

        // (v0.2.28 Fix C) Cross-peer shipyard cradle tracking. Vanilla AdmitShip/DischargeShip is purely
        // local: only the editing machine lifts the boat kinematically onto the cradle; the discharge's
        // instant teleport + physics re-enable registered a >1.5 m/s BoatDamage.Impact. Every peer tracks
        // the set of boats currently in a cradle (fed by ShipyardState packets + the local Harmony
        // postfixes below). The set is purely BOOKKEEPING on receivers: it gates when the discharge-time
        // impact-suppression window may start. We deliberately do NOT freeze or otherwise touch the boat's
        // rigidbody on receivers - the host keeps streaming and stays authoritative; the cosmetic cradle
        // lift is simply not synced. DamagePatches skips Impact for a short window after release.
        // A stale entry (e.g. the editing peer disconnected mid-edit) is therefore low-harm - it only
        // gates when damage suppression may begin - so no per-peer disconnect tracking is kept; the set
        // is cleared on session start and on Reset().
        // Static: read from Harmony patches and DamagePatches without an Instance dance.
        private static readonly HashSet<string> _shipyardActiveBoats = new HashSet<string>();
        // Boat name -> Time.unscaledTime of the shipyard release; Impact is suppressed within the window.
        private static readonly Dictionary<string, float> _dischargeTimes = new Dictionary<string, float>();
        private const float ImpactSuppressionWindow = 3f; // seconds after discharge (covers the release
                                                          // teleport, the local depenetration settle AND
                                                          // the remote forced convergence snap)

        // Session-start transition detection (Update early-returns while not in a session, so the first
        // in-session frame is the session start).
        private bool _wasInSession;

        /// <summary>True while ANY peer (including this machine) has this boat on a shipyard cradle.</summary>
        public static bool IsBoatShipyardActive(string boatName)
        {
            return !string.IsNullOrEmpty(boatName) && _shipyardActiveBoats.Contains(boatName);
        }

        /// <summary>
        /// True within a short window after the boat left a shipyard cradle. The discharge is an instant
        /// teleport + physics re-enable (vanilla MoveShip instantMove), and on non-editing peers a forced
        /// snap - either can depenetrate at >1.5 m/s and register phantom hull damage.
        /// </summary>
        public static bool IsImpactSuppressed(string boatName)
        {
            if (string.IsNullOrEmpty(boatName)) return false;
            if (!_dischargeTimes.TryGetValue(boatName, out var t)) return false;
            if (Time.unscaledTime - t < ImpactSuppressionWindow) return true;
            // Expired: drop the entry so HasActiveSuppression goes false again and DamagePatches'
            // cheap early-out keeps the common Impact path free of GetComponent lookups.
            _dischargeTimes.Remove(boatName);
            return false;
        }

        /// <summary>
        /// Cheap early-out for DamagePatches: false when no discharge window can possibly be live,
        /// so the common Impact path never pays a GetComponent lookup.
        /// </summary>
        public static bool HasActiveSuppression => _dischargeTimes.Count > 0;

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
            if (!Plugin.IsMultiplayer)
            {
                _wasInSession = false;
                return;
            }

            // Session start: clear the static cradle/discharge tracking so nothing leaks in from a
            // previous session (Reset() also clears on teardown; this is belt-and-braces for paths
            // that start a new session without a clean teardown, e.g. a hot-reload race).
            if (!_wasInSession)
            {
                _wasInSession = true;
                _shipyardActiveBoats.Clear();
                _dischargeTimes.Clear();
            }

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
        /// (v0.2.28 Fix C) Local AdmitShip/DischargeShip happened on THIS machine (Harmony postfixes
        /// below): record it and announce to the crew. Guests send to the host, which relays.
        /// </summary>
        internal static void OnLocalShipyardState(string boatName, bool active)
        {
            // Multiplayer only: in solo every side effect here (set mutation, snap arming, packet send)
            // must stay off so vanilla shipyard behavior is 100% untouched.
            if (!Plugin.IsMultiplayer) return;
            if (string.IsNullOrEmpty(boatName)) return;

            if (active)
            {
                _shipyardActiveBoats.Add(boatName);
            }
            else
            {
                _shipyardActiveBoats.Remove(boatName);
                // Suppress Impact locally too: the editing machine's own instant release teleport +
                // physics re-enable can depenetrate against the water/dock at >1.5 m/s.
                _dischargeTimes[boatName] = Time.unscaledTime;
                // The editing machine is the peer whose boat pose diverges MOST from the host stream
                // (cradle lift + release teleport happened only here), and if it is a guest it never
                // receives its own relayed ShipyardState packet (the host relays SendToAllExcept sender).
                // Arm the one-shot convergence snap locally so the resumed stream teleports instead of
                // velocity-chasing the cradle-to-release gap.
                BoatSyncManager.Instance?.ForceSnapOnNextApply(boatName);
            }

            var packet = new ShipyardStatePacket { BoatName = boatName, Active = active };
            VerboseLogger.ShipyardSend($"ShipyardState, boat={boatName}, active={active}");
            Plugin.NetworkManager.SendToAllReliable(PacketType.ShipyardState, w =>
                PacketSerializer.WriteShipyardState(w, packet));
        }

        /// <summary>
        /// (v0.2.28 Fix C) A ShipyardState packet arrived from the editing peer. Host relays to the other
        /// guests (star topology, same pattern as OnCustomizationReceived). The receiver is by construction
        /// NOT the editing machine (the relay excludes the sender), so:
        /// - active=true: bookkeeping ONLY. Record the boat name; no rigidbody is touched on any receiver,
        ///   ever - the host stream stays authoritative and the boat keeps bobbing in the water here (the
        ///   cosmetic cradle lift is deliberately not synced).
        /// - active=false: start the impact-suppression window, zero velocities ONCE, and force a
        ///   snap-on-next-apply so this peer converges to the post-discharge pose without velocity-chasing.
        /// </summary>
        public void OnShipyardStateReceived(ShipyardStatePacket packet, Steamworks.SteamId sender = default)
        {
            VerboseLogger.ShipyardRecv($"ShipyardState, boat={packet.BoatName}, active={packet.Active}");

            if (Plugin.IsHost)
                Plugin.NetworkManager.SendToAllExcept(sender, PacketType.ShipyardState, w =>
                    PacketSerializer.WriteShipyardState(w, packet));

            if (string.IsNullOrEmpty(packet.BoatName)) return;

            if (packet.Active)
            {
                _shipyardActiveBoats.Add(packet.BoatName);
            }
            else
            {
                _shipyardActiveBoats.Remove(packet.BoatName);
                _dischargeTimes[packet.BoatName] = Time.unscaledTime;

                // One-time settle: kill any residual correction velocity so the convergence snap below
                // starts from rest (never continuous, never kinematic - just this single zeroing).
                var boat = BoatUtility.FindBoatByName(packet.BoatName);
                var rb = boat != null ? boat.GetComponent<Rigidbody>() : null;
                if (rb != null && !rb.isKinematic)
                {
                    rb.velocity = Vector3.zero;
                    rb.angularVelocity = Vector3.zero;
                }

                // Converge to the editing peer's post-discharge pose via a one-shot teleport snap instead
                // of a violent velocity chase across the cradle-to-release-point gap.
                BoatSyncManager.Instance?.ForceSnapOnNextApply(packet.BoatName);
                Plugin.Log.LogInfo($"[SHIPYARD] '{packet.BoatName}' discharged on a remote peer; snapping to authoritative pose, impact suppressed {ImpactSuppressionWindow:F0}s");
            }
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
            _shipyardActiveBoats.Clear();
            _dischargeTimes.Clear();
        }
    }

    /// <summary>
    /// (v0.2.28 Fix C) Harmony patches announcing the local shipyard cradle lift/release to the crew.
    /// Lives here with the rest of the shipyard sync logic (no dedicated ShipyardPatches file exists).
    /// </summary>
    [HarmonyPatch(typeof(Shipyard), "AdmitShip")]
    public static class ShipyardAdmitShipPatch
    {
        [HarmonyPostfix]
        public static void Postfix(GameObject ship)
        {
            if (!Plugin.IsMultiplayer) return; // solo shipyard use stays 100% vanilla
            if (ship == null) return;
            // ship is the boat ROOT (it carries BoatRefs); prefer the SaveableObject name, the shared key.
            var saveable = ship.GetComponent<SaveableObject>();
            ShipyardSyncManager.OnLocalShipyardState(saveable != null ? saveable.gameObject.name : ship.name, active: true);
        }
    }

    [HarmonyPatch(typeof(Shipyard), nameof(Shipyard.DischargeShip))]
    public static class ShipyardDischargeShipPatch
    {
        // DischargeShip nulls currentShip inside the method, so capture the boat in a prefix.
        [HarmonyPrefix]
        public static void Prefix(Shipyard __instance, out GameObject __state)
        {
            __state = __instance.GetCurrentBoat();
        }

        [HarmonyPostfix]
        public static void Postfix(GameObject __state)
        {
            if (!Plugin.IsMultiplayer) return; // solo shipyard use stays 100% vanilla (no velocity zeroing)
            if (__state == null) return;

            // Vanilla MoveShip(instantMove: true) runs synchronously to completion inside DischargeShip
            // (t starts at 1, the lerp loop never yields), so by this postfix the boat has already been
            // teleported to shipReleasePosition and physics re-enabled. Zero the velocities NOW so the
            // water/dock depenetration on the editing machine cannot register a >1.5 m/s BoatDamage.Impact.
            var rb = __state.GetComponent<Rigidbody>();
            if (rb != null && !rb.isKinematic)
            {
                rb.velocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
            }

            var saveable = __state.GetComponent<SaveableObject>();
            ShipyardSyncManager.OnLocalShipyardState(saveable != null ? saveable.gameObject.name : __state.name, active: false);
        }
    }
}
