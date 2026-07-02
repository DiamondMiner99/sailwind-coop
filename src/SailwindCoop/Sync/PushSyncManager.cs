using System.Collections.Generic;
using System.IO;
using Steamworks;
using UnityEngine;
using SailwindCoop.Networking;
using SailwindCoop.Networking.Packets;
using SailwindCoop.Debug;

namespace SailwindCoop.Sync
{
    /// <summary>
    /// Manages event-based push sync. Replaces polling-based approach.
    /// Guest sends push events to host, host applies force continuously.
    /// </summary>
    public class PushSyncManager : MonoBehaviour
    {
        public static PushSyncManager Instance { get; private set; }

        // One active push per pushing peer. Multiple crew can shove the same boat/sail at once; vanilla
        // already SUMS multiple local pushers (each is an independent AddForceAtPosition), so the host
        // applies one AddForceAtPosition per remote pusher and the forces sum the same way. Do NOT collapse
        // this per-SteamId map back to a single shared slot: concurrent pushers would overwrite each other's
        // state and a guest's push would be silently dropped. At N=1 there is exactly one entry, so a lone
        // guest's push must stay byte-identical to single-pusher behavior (a guest's local AddForce is
        // host-overwritten by the sync stream, so the host-applied push is the only one that sticks).
        private class RemotePushState
        {
            // PushType byte: 0 = boat push (GPButtonBoatPushCol), 1 = sail push (GPButtonSailPusher),
            // 2 = dock push (DockPushCol). Kept as the raw wire byte so the host applies the matching
            // vanilla force formula (each push collider has different up-lift / point-offset constants).
            public byte PushType;
            public Vector3 Direction;
            public Vector3 Position;
            public Rigidbody Target;
            public float ForceMult;
            public float BaseMass;
        }

        // Wire values for PushStartPacket.PushType (raw byte, no schema change - just new enum values).
        // Public so the ControlPatches push patches name the type explicitly at the call site.
        public const byte PushTypeBoat = 0;
        public const byte PushTypeSail = 1;
        public const byte PushTypeDock = 2;

        // Remote push state (host tracks each guest's push, keyed by the pushing peer's SteamId)
        private readonly Dictionary<SteamId, RemotePushState> _remotePushes = new Dictionary<SteamId, RemotePushState>();

        // Reflective access to GPButtonSailPusher's private serialized sail rigidbody, so a guest's sail
        // push is applied to the SAIL (vanilla's `body`) rather than the hull.
        private static readonly HarmonyLib.AccessTools.FieldRef<GPButtonSailPusher, Rigidbody> SailBodyRef =
            HarmonyLib.AccessTools.FieldRefAccess<GPButtonSailPusher, Rigidbody>("body");

        // Local push state (guest tracks own push for 10Hz updates)
        private bool _localPushActive;
        private GoPointerMovement _localPushPointer;
        private float _lastPushUpdateTime;
        private const float PushUpdateInterval = 0.1f; // 10Hz

        private void Awake()
        {
            Instance = this;
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        /// <summary>
        /// Host applies force at 50Hz for EVERY active remote pusher. One AddForceAtPosition per pusher,
        /// so concurrent crew pushing the same boat/sail sum exactly as vanilla local pushers do.
        /// </summary>
        private void FixedUpdate()
        {
            if (!Plugin.IsMultiplayer || !Plugin.IsHost) return;
            if (_remotePushes.Count == 0) return;

            // Floating-origin: positions were sent with the SENDER's offset subtracted; add the HOST's own
            // offset back at apply time (it can change between receipt and this physics step). Matches the
            // sender-subtracts / receiver-adds convention used by the mooring sync.
            var hostOffset = FloatingOriginManager.instance?.outCurrentOffset ?? Vector3.zero;

            foreach (var kvp in _remotePushes)
            {
                var push = kvp.Value;
                if (push.Target == null) continue;

                // Apply force same as game logic
                float velocity = push.Target.velocity.magnitude;
                if (velocity < 1f) velocity = 1f;

                Vector3 force;
                Vector3 pointOffset = Vector3.zero;
                if (push.PushType == PushTypeSail)
                {
                    // Sail push: simple directional force (vanilla GPButtonSailPusher applies no up-lift/offset).
                    force = push.ForceMult * push.Direction * 2.5f;
                }
                else if (push.PushType == PushTypeDock)
                {
                    // Dock push (on-deck): replicate vanilla DockPushCol. UNLIKE the water boat-push it has NO
                    // up-lift (upForceMult == 0) and NO point offset (verticalOffset == 0); force is applied at
                    // the pusher's own position. force = pushForceMult * mass * dir / velocity. The dock
                    // pushForceMult is negative (~ -0.55), so the guest shoves the boat off the dock.
                    force = push.ForceMult * push.BaseMass * push.Direction / velocity;
                }
                else
                {
                    // Boat push: replicate vanilla GPButtonBoatPushCol exactly. Vanilla adds an up-lift term and
                    // applies the force 2m below the pointer; upForceMult (1f) and verticalOffset (-2f) are private
                    // constant initializers on GPButtonBoatPushCol (not per-boat tunable), so we hardcode them here
                    // and need no extra packet fields. force = (fwd + up) / velocity; point = pointer + up*-2.
                    Vector3 fwd = push.ForceMult * push.BaseMass * push.Direction;
                    Vector3 up = push.BaseMass * Vector3.up; // upForceMult == 1f
                    force = (fwd + up) / velocity;
                    pointOffset = Vector3.up * -2f;          // verticalOffset == -2f
                }

                push.Target.AddForceAtPosition(force, push.Position + hostOffset + pointOffset);
            }
        }

        /// <summary>
        /// Guest sends position updates at 10Hz while pushing.
        /// </summary>
        private void Update()
        {
            if (!Plugin.IsMultiplayer || Plugin.IsHost) return;
            if (!_localPushActive || _localPushPointer == null) return;

            if (Time.time - _lastPushUpdateTime < PushUpdateInterval) return;
            _lastPushUpdateTime = Time.time;

            SendPushUpdate();
        }

        private void SendPushUpdate()
        {
            if (_localPushPointer == null) return;

            // Floating-origin: subtract our own offset so the host can re-add its offset at apply time
            // (sender-subtracts / receiver-adds, matching the mooring sync). Direction is rotation-only.
            var offset = FloatingOriginManager.instance?.outCurrentOffset ?? Vector3.zero;
            var packet = new PushUpdatePacket
            {
                Direction = _localPushPointer.transform.forward,
                Position = _localPushPointer.transform.position - offset
            };

            // In 2-player mode, SendToAllReliable sends to host (the only other player)
            Plugin.NetworkManager.SendToAllReliable(PacketType.PushUpdate,
                w => PacketSerializer.WritePushUpdate(w, packet)
            );
        }

        #region Public Methods (called by patches)

        /// <summary>
        /// Called when guest starts pushing (push collider ExtraFixedUpdate edge).
        /// pushType: 0 = boat push (water), 1 = sail push, 2 = dock push (on-deck).
        /// </summary>
        public void OnLocalPushStart(byte pushType, string boatName, float forceMult, GoPointerMovement pointer, int sailIndex = -1)
        {
            if (Plugin.IsHost) return; // Host uses local physics

            _localPushActive = true;
            _localPushPointer = pointer;
            _lastPushUpdateTime = 0; // Send update immediately on next frame

            if (pointer == null)
            {
                Plugin.Log.LogWarning("[PushSync] OnLocalPushStart: pointer is null");
                return;
            }

            // Floating-origin: subtract our own offset (sender-subtracts / receiver-adds; see FixedUpdate).
            var offset = FloatingOriginManager.instance?.outCurrentOffset ?? Vector3.zero;
            var packet = new PushStartPacket
            {
                PushType = pushType,
                BoatName = boatName,
                Direction = pointer.transform.forward,
                Position = pointer.transform.position - offset,
                ForceMult = forceMult,
                SailIndex = sailIndex // sail push: which GPButtonSailPusher (so the host pushes the SAIL, not the hull)
            };

            // In 2-player mode, SendToAllReliable sends to host (the only other player)
            Plugin.NetworkManager.SendToAllReliable(PacketType.PushStart,
                w => PacketSerializer.WritePushStart(w, packet)
            );

            VerboseLogger.ControlSend($"Push start, type={PushTypeName(pushType)}, boat={boatName}");
        }

        private static string PushTypeName(byte pushType)
        {
            switch (pushType)
            {
                case PushTypeSail: return "Sail";
                case PushTypeDock: return "Dock";
                default: return "Boat";
            }
        }

        /// <summary>
        /// Called when guest stops pushing (OnUnactivate on push collider).
        /// </summary>
        public void OnLocalPushStop()
        {
            if (!_localPushActive) return;

            _localPushActive = false;
            _localPushPointer = null;

            // In 2-player mode, SendToAllReliable sends to host (the only other player)
            Plugin.NetworkManager.SendToAllReliable(PacketType.PushStop,
                w => { } // No data
            );

            VerboseLogger.ControlSend("Push stop");
        }

        #endregion

        #region Packet Handlers (host-side)

        public void RegisterPacketHandlers()
        {
            Plugin.NetworkManager.RegisterHandler(PacketType.PushStart, OnPushStartReceived);
            Plugin.NetworkManager.RegisterHandler(PacketType.PushUpdate, OnPushUpdateReceived);
            Plugin.NetworkManager.RegisterHandler(PacketType.PushStop, OnPushStopReceived);
        }

        private void OnPushStartReceived(SteamId sender, BinaryReader reader)
        {
            if (!Plugin.IsHost) return;

            var packet = PacketSerializer.ReadPushStart(reader);

            // Find target boat rigidbody
            var boat = BoatUtility.FindBoatByName(packet.BoatName);
            if (boat == null)
            {
                Plugin.Log.LogWarning($"[PushSync] Boat not found: {packet.BoatName}");
                return;
            }

            // Resolve the target rigidbody. Boat/dock push -> the hull. Sail push -> the SAIL'S OWN rigidbody
            // (vanilla GPButtonSailPusher applies its force to its serialized `body` == the sail spar, NOT
            // the hull). Without this the host would shove the whole boat when a guest works a sail pusher.
            // Dock push targets the same hull as a boat push (vanilla uses GameState.currentBoat.parent's
            // rigidbody, which is the hull the guest is standing on == this boat).
            Rigidbody target = null;
            if (packet.PushType == PushTypeSail && packet.SailIndex >= 0)
            {
                var pushers = boat.GetComponentsInChildren<GPButtonSailPusher>(true);
                if (packet.SailIndex < pushers.Length && pushers[packet.SailIndex] != null)
                {
                    var pusher = pushers[packet.SailIndex];
                    target = SailBodyRef(pusher);
                    if (target == null && pusher.transform.parent != null)
                        target = pusher.transform.parent.GetComponent<Rigidbody>(); // vanilla Awake fallback
                }
            }
            if (target == null) target = boat.GetComponentInParent<Rigidbody>(); // boat/dock push, or sail fallback
            if (target == null)
            {
                Plugin.Log.LogWarning($"[PushSync] No rigidbody on boat: {packet.BoatName}");
                return;
            }

            // Record/replace THIS pusher's entry (keyed by sender). Other pushers keep theirs and keep
            // summing in FixedUpdate. At N=1 this is the only entry == the old single-slot assignment.
            _remotePushes[sender] = new RemotePushState
            {
                PushType = packet.PushType,
                Direction = packet.Direction,
                Position = packet.Position,
                Target = target,
                ForceMult = packet.ForceMult,
                BaseMass = target.mass // hull mass for boat/dock push (unused for sail)
            };

            VerboseLogger.ControlRecv($"Push start, type={PushTypeName(packet.PushType)}, boat={packet.BoatName}, from={sender}");
        }

        private void OnPushUpdateReceived(SteamId sender, BinaryReader reader)
        {
            if (!Plugin.IsHost) return;

            var packet = PacketSerializer.ReadPushUpdate(reader);
            // Update only THIS pusher's direction/position; ignore updates for a pusher who never started
            // (or already stopped) so a stale update can't resurrect a removed push.
            if (_remotePushes.TryGetValue(sender, out var push))
            {
                push.Direction = packet.Direction;
                push.Position = packet.Position;
            }
        }

        private void OnPushStopReceived(SteamId sender, BinaryReader reader)
        {
            if (!Plugin.IsHost) return;

            // Remove only THIS sender's push; every other pusher keeps shoving.
            _remotePushes.Remove(sender);

            VerboseLogger.ControlRecv($"Push stop, from={sender}");
        }

        #endregion

        /// <summary>
        /// Drop ONE peer's push (called on that peer's disconnect). A guest leaving mid-push never sends
        /// PushStop, so without this the host keeps applying its force forever. Other pushers are untouched.
        /// At N=1 the dropped peer is the only pusher, so this matches the old ClearState behavior.
        /// </summary>
        public void OnPeerDisconnected(SteamId peer)
        {
            if (_remotePushes.Remove(peer))
                VerboseLogger.ControlEvent($"Push state cleared for disconnected peer {peer}");
        }

        /// <summary>
        /// Clear ALL push state. Called when leaving multiplayer / full reset (no peers remain).
        /// </summary>
        public void ClearState()
        {
            _remotePushes.Clear();
            _localPushActive = false;
            _localPushPointer = null;
            VerboseLogger.ControlEvent("Push state cleared (disconnect/lobby-leave); force application stopped");
        }
    }
}
