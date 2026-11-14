using System.Collections.Generic;
using HarmonyLib;
using SailwindCoop.Debug;
using SailwindCoop.Networking.Packets;
using SailwindCoop.Patches;
using Steamworks;
using UnityEngine;

namespace SailwindCoop.Sync
{
    /// <summary>
    /// Where a boat's anchor physically is.
    ///
    /// The anchor is a PickupableItem but not a ShipItem, so item sync never saw anyone carry it, and AnchorEvent
    /// carries only set + rope length. So a crewmate could walk an anchor ashore and set it, while everyone else's
    /// copy stayed where it had been and froze kinematic there, in mid-air under the hawse, with a rope running to
    /// a spot that had nothing on it.
    ///
    /// While carried, the carrier streams a boat-relative pose. A fake `held` is not an option on the receiving
    /// side: Anchor.ExtraFixedUpdate calls held.DropItem() on it, which would empty the local player's hands. The
    /// receiver leaves `held` alone and overrules the two fields vanilla derives from it, after vanilla has run.
    /// When set, the real-world pose goes out right before the AnchorEvent, and the host repeats it now and then,
    /// which also covers joins and boats streaming back in.
    /// </summary>
    internal static class AnchorPoseSync
    {
        private const float CarrySendInterval = 0.1f;
        private const float CarryTimeout = 2.5f;       // A carrier who left sends no release
        private const float CarryFollowSpeed = 15f;
        private const float RopeSlack = 0.05f;         // Vanilla's own payout margin while held (Anchor.ExtraFixedUpdate)
        private const float RopeMaxWhileCarried = 0.97f;
        private const float HostRefreshInterval = 5f;
        private const float RefreshSnapDistance = 0.5f;
        private const int IgnoreRaycastLayer = 2;

        private class RemoteCarry
        {
            public SaveableObject Boat;
            public Vector3 LocalPos;
            public Quaternion LocalRot;
            public float LastPacket;
        }

        private static readonly Dictionary<Anchor, RemoteCarry> _carried = new Dictionary<Anchor, RemoteCarry>();
        private static readonly List<Anchor> _endScratch = new List<Anchor>();

        private static GoPointer _pointer;
        private static Anchor _localHeld;
        private static float _nextCarrySend;
        private static float _nextHostRefresh;

        public static bool IsRemotelyCarried(Anchor anchor)
        {
            return anchor != null && _carried.ContainsKey(anchor);
        }

        public static void Reset()
        {
            foreach (var kvp in _carried)
                if (kvp.Key != null) Restore(kvp.Key);
            _carried.Clear();
            _localHeld = null;
            _pointer = null;
            _nextCarrySend = 0f;
            _nextHostRefresh = 0f;
        }

        // === Sending ===

        public static void Tick()
        {
            float now = Time.unscaledTime;

            TickLocalCarry(now);
            TickRemoteCarries(now);

            if (Plugin.IsHost && now >= _nextHostRefresh)
            {
                _nextHostRefresh = now + HostRefreshInterval;
                RefreshSetAnchors();
            }
        }

        private static void TickLocalCarry(float now)
        {
            if (_pointer == null) _pointer = Object.FindObjectOfType<GoPointer>();
            var held = _pointer != null ? _pointer.GetHeldItem() as Anchor : null;

            if (held != _localHeld)
            {
                if (_localHeld != null) SendPose(_localHeld, ControlPatches.ResolveAnchorBoat(_localHeld), AnchorPoseState.Released);
                _localHeld = held;
                _nextCarrySend = 0f;
            }

            if (held != null && now >= _nextCarrySend)
            {
                _nextCarrySend = now + CarrySendInterval;
                SendPose(held, ControlPatches.ResolveAnchorBoat(held), AnchorPoseState.Carried);
            }
        }

        private static void RefreshSetAnchors()
        {
            foreach (var boat in BoatUtility.FindAllBoats().Values)
            {
                var anchor = BoatUtility.GetAnchor(boat);
                if (anchor == null || !anchor.IsSet() || anchor.held != null || IsRemotelyCarried(anchor)) continue;
                SendPose(anchor, boat, AnchorPoseState.SetRefresh);
            }
        }

        /// <summary>Called right before the AnchorEvent for a set made on this machine.</summary>
        public static void SendSetPose(Anchor anchor, SaveableObject boat)
        {
            SendPose(anchor, boat, AnchorPoseState.Set);
        }

        private static void SendPose(Anchor anchor, SaveableObject boat, AnchorPoseState state)
        {
            if (anchor == null || boat == null || Plugin.NetworkManager == null) return;

            var packet = new AnchorPosePacket
            {
                BoatName = boat.gameObject.name,
                State = state,
                RopeLength = GetRope(boat)?.currentLength ?? 0f
            };

            if (IsWorldFrame(state))
            {
                // A set anchor stays put while the boat swings around it.
                packet.Position = anchor.transform.position - FloatingOffset();
                packet.Rotation = anchor.transform.rotation;
            }
            else
            {
                packet.Position = boat.transform.InverseTransformPoint(anchor.transform.position);
                packet.Rotation = Quaternion.Inverse(boat.transform.rotation) * anchor.transform.rotation;
            }

            if (IsReliable(state))
                Plugin.NetworkManager.SendToAllReliable(PacketType.AnchorPose, w => PacketSerializer.WriteAnchorPose(w, packet));
            else
                Plugin.NetworkManager.SendToAllUnreliable(PacketType.AnchorPose, w => PacketSerializer.WriteAnchorPose(w, packet));
        }

        private static bool IsWorldFrame(AnchorPoseState state)
        {
            return state == AnchorPoseState.Set || state == AnchorPoseState.SetRefresh;
        }

        private static bool IsReliable(AnchorPoseState state)
        {
            return state == AnchorPoseState.Released || state == AnchorPoseState.Set;
        }

        // === Receiving ===

        public static void OnRemotePose(AnchorPosePacket packet, SteamId sender)
        {
            if (Plugin.IsHost)
                Plugin.NetworkManager.SendToAllExcept(sender, PacketType.AnchorPose,
                    w => PacketSerializer.WriteAnchorPose(w, packet), IsReliable(packet.State));

            if (GameState.currentlyLoading || GameState.justStarted) return;

            var boat = BoatUtility.FindBoatByName(packet.BoatName);
            var anchor = boat != null ? BoatUtility.GetAnchor(boat) : null;
            if (anchor == null) return;

            // Two people grabbed it at once. Your own hand wins here, and the other's release lands when it comes.
            if (anchor.held != null) return;

            var rope = GetRope(boat);

            switch (packet.State)
            {
                case AnchorPoseState.Carried:
                    PayOutTo(rope, packet.RopeLength);
                    if (!_carried.TryGetValue(anchor, out var carry))
                    {
                        carry = new RemoteCarry { Boat = boat };
                        _carried[anchor] = carry;
                        // GoPointer.PickUpItem does the same to a held item: nobody can look at it or grab it.
                        anchor.gameObject.layer = IgnoreRaycastLayer;
                        Overrule(anchor);
                        Place(anchor, boat.transform.TransformPoint(packet.Position), boat.transform.rotation * packet.Rotation, null, null);
                        VerboseLogger.ControlApply($"Anchor of {packet.BoatName} is being carried by {sender}");
                    }
                    carry.LocalPos = packet.Position;
                    carry.LocalRot = packet.Rotation;
                    carry.LastPacket = Time.unscaledTime;
                    break;

                case AnchorPoseState.Released:
                    EndCarry(anchor);
                    PayOutTo(rope, packet.RopeLength);
                    if (!anchor.IsSet())
                        Place(anchor, boat.transform.TransformPoint(packet.Position), boat.transform.rotation * packet.Rotation, null, null);
                    break;

                case AnchorPoseState.Set:
                    EndCarry(anchor);
                    PayOutTo(rope, packet.RopeLength);
                    Place(anchor, packet.Position + FloatingOffset(), packet.Rotation, rope, anchor.GetComponent<ConfigurableJoint>());
                    VerboseLogger.ControlApply($"Anchor of {packet.BoatName} set at {packet.Position}");
                    break;

                case AnchorPoseState.SetRefresh:
                    // Only the position: whether it is set is the AnchorEvent's word, and a guest mid-grab or a
                    // release in flight must not be overruled by a packet that may be seconds old.
                    if (!anchor.IsSet() || IsRemotelyCarried(anchor)) return;
                    var refreshed = packet.Position + FloatingOffset();
                    if ((anchor.transform.position - refreshed).sqrMagnitude <= RefreshSnapDistance * RefreshSnapDistance) return;
                    Place(anchor, refreshed, packet.Rotation, rope, anchor.GetComponent<ConfigurableJoint>());
                    VerboseLogger.ControlApply($"Anchor of {packet.BoatName} moved to the host's position");
                    break;
            }
        }

        private static void TickRemoteCarries(float now)
        {
            if (_carried.Count == 0) return;

            _endScratch.Clear();
            foreach (var kvp in _carried)
            {
                var anchor = kvp.Key;
                var carry = kvp.Value;
                if (anchor == null || carry.Boat == null || now - carry.LastPacket > CarryTimeout)
                {
                    _endScratch.Add(anchor);
                    continue;
                }

                float t = Mathf.Clamp01(Time.unscaledDeltaTime * CarryFollowSpeed);
                var pos = Vector3.Lerp(anchor.transform.position, carry.Boat.transform.TransformPoint(carry.LocalPos), t);
                var rot = Quaternion.Slerp(anchor.transform.rotation, carry.Boat.transform.rotation * carry.LocalRot, t);
                Place(anchor, pos, rot, null, null);

                // The same rule the carrier's vanilla runs, and for the same reason: a kinematic anchor outside the
                // joint limit pulls on the hull, and on the host that is the real hull.
                var rope = GetRope(carry.Boat);
                if (rope != null && rope.maxLength > 0f)
                {
                    float needed = Mathf.InverseLerp(0f, rope.maxLength, Vector3.Distance(rope.transform.position, pos));
                    if (needed > rope.currentLength - RopeSlack)
                        rope.currentLength = Mathf.Min(needed + RopeSlack, RopeMaxWhileCarried);
                }
            }

            foreach (var anchor in _endScratch)
                EndCarry(anchor);
            _endScratch.Clear();
        }

        /// <summary>Runs after vanilla's ExtraFixedUpdate, which has just derived both of these from a null held.</summary>
        public static void OnAnchorFixedUpdate(Anchor anchor)
        {
            if (IsRemotelyCarried(anchor)) Overrule(anchor);
        }

        private static void Overrule(Anchor anchor)
        {
            var rb = anchor.GetComponent<Rigidbody>();
            if (rb != null) rb.isKinematic = true;
            var col = anchor.GetComponent<CapsuleCollider>();
            if (col != null) col.isTrigger = true;
        }

        private static void EndCarry(Anchor anchor)
        {
            if (anchor == null)
            {
                _carried.Remove(anchor);
                return;
            }
            if (_carried.Remove(anchor)) Restore(anchor);
        }

        private static void Restore(Anchor anchor)
        {
            if (anchor.gameObject.layer == IgnoreRaycastLayer) anchor.gameObject.layer = 0;
            var rb = anchor.GetComponent<Rigidbody>();
            if (rb != null) rb.isKinematic = anchor.IsSet();
            var col = anchor.GetComponent<CapsuleCollider>();
            if (col != null) col.isTrigger = false;
        }

        private static void PayOutTo(RopeControllerAnchor rope, float length)
        {
            // Only ever out. Hauling in has its own stream, and a pose must not shorten a rope under a taut anchor.
            if (rope != null && length > rope.currentLength) rope.currentLength = length;
        }

        /// <summary>
        /// Move the body. With a rope and joint, a target the local joint could not reach is pulled in along the same
        /// bearing: hulls differ a little between machines, and a frozen anchor just outside the limit yanks the hull.
        /// </summary>
        private static void Place(Anchor anchor, Vector3 worldPos, Quaternion worldRot, RopeControllerAnchor rope, ConfigurableJoint joint)
        {
            if (rope != null && joint != null)
            {
                var delta = worldPos - rope.transform.position;
                float limit = joint.linearLimit.limit;
                if (limit > 0.5f && delta.magnitude > limit + 0.5f)
                    worldPos = rope.transform.position + delta.normalized * (limit - 0.25f);
            }

            anchor.transform.position = worldPos;
            anchor.transform.rotation = worldRot;

            var rb = anchor.GetComponent<Rigidbody>();
            if (rb == null) return;
            rb.position = worldPos; // Transform writes alone do not reliably move the physics pose
            rb.rotation = worldRot;
            if (!rb.isKinematic)
            {
                rb.velocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
            }
        }

        private static RopeControllerAnchor GetRope(SaveableObject boat)
        {
            return boat != null ? boat.GetComponent<BoatMooringRopes>()?.GetAnchorController() : null;
        }

        private static Vector3 FloatingOffset()
        {
            return FloatingOriginManager.instance != null ? FloatingOriginManager.instance.outCurrentOffset : Vector3.zero;
        }
    }
}
