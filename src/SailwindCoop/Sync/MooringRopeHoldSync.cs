using System.Collections.Generic;
using Steamworks;
using UnityEngine;
using SailwindCoop.Networking.Packets;

namespace SailwindCoop.Sync
{
    /// <summary>
    /// (v0.2.39) Shows a mooring rope as CARRIED by the crewmate carrying it.
    ///
    /// THE GAP THIS FILLS. Where a rope is ATTACHED has been synced for a long time (the MoorTo/Unmoor
    /// patches and the MooringState packet), but who is holding one was not, so a docking manoeuvre looked
    /// like this from the other end: nothing happens, nothing happens, then a rope is suddenly moored. The
    /// reported symptom was "I don't know if someone picked up the mooring rope at all".
    ///
    /// WHY IT WAS NEVER PICKED UP BY THE ITEM SYNC. ItemPatches.OnPickUpItem casts to ShipItem and does
    /// nothing otherwise, and PickupableBoatMooringRope derives from PickupableItem, NOT ShipItem - a
    /// sibling, not a subclass. Every mooring rope pickup fell through that cast.
    ///
    /// WHY IT DOES NOT NEED THE ITEM MACHINERY. A loose ShipItem needs cross-machine identity correlation:
    /// SaveablePrefab instance ids, ghost purging, lazy id correlation - the genuinely hard part of item
    /// sync. A mooring rope needs none of it. It is a permanent child of one boat and is addressed as
    /// (boat, rope index), exactly as the existing moor/unmoor packets already address it. So this is three
    /// fields and no identity problem.
    ///
    /// THE RECEIVER-SIDE TRAP, which is the only genuinely dangerous part. Vanilla's OnTriggerEnter moors a
    /// rope to any dock cleat it touches while `!held`. On a receiving machine `held` is null - the local
    /// player is not carrying anything - so simply moving the rope to follow a crewmate would make it MOOR
    /// ITSELF to the first cleat that crewmate walked past. MoorTo is patched to broadcast, so that phantom
    /// moor would then propagate to the whole crew as if a player had done it. The trigger is therefore
    /// suppressed for remotely-held ropes (see MooringRopeTriggerPatch in ControlPatches).
    ///
    /// Deliberately NOT streamed per frame. The rope follows the carrier's avatar, so it costs one small
    /// reliable packet on pickup and one on drop rather than a position stream for the length of a docking.
    /// The point is to see that a crewmate has the rope and is walking it to a cleat, and following the
    /// avatar conveys exactly that.
    /// </summary>
    public static class MooringRopeHoldSync
    {
        // Rope -> who is carrying it (remote holders only; our own hands need no bookkeeping).
        private static readonly Dictionary<PickupableBoatMooringRope, ulong> _remotelyHeld =
            new Dictionary<PickupableBoatMooringRope, ulong>();

        /// <summary>Carried offset from the holder's avatar origin: forward of the chest, roughly where a
        /// coil of rope sits when someone walks it to a cleat.</summary>
        private static readonly Vector3 CarryOffset = new Vector3(0f, 0.15f, 0.42f);

        public static bool IsRemotelyHeld(PickupableBoatMooringRope rope)
        {
            return rope != null && _remotelyHeld.ContainsKey(rope);
        }

        public static void Clear() { _remotelyHeld.Clear(); }

        // ---- send ---------------------------------------------------------------------------------------

        /// <summary>Local player picked a rope up or put it down. held=false means released.</summary>
        public static void OnLocalHoldChanged(PickupableBoatMooringRope rope, bool held)
        {
            try
            {
                if (!Plugin.IsMultiplayer || Plugin.NetworkManager == null || rope == null) return;

                string boatName;
                int index;
                if (!Identify(rope, out boatName, out index)) return;

                ulong me = SteamClient.SteamId.Value;
                Plugin.NetworkManager.SendToAllReliable(PacketType.MooringRopeHeld,
                    w => { w.Write(boatName); w.Write((byte)index); w.Write(held ? me : 0UL); });
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("[MooringHold] Send failed: " + e.Message);
            }
        }

        // ---- receive ------------------------------------------------------------------------------------

        public static void OnReceived(SteamId sender, System.IO.BinaryReader reader)
        {
            try
            {
                string boatName = reader.ReadString();
                int index = reader.ReadByte();
                ulong holder = reader.ReadUInt64();

                if (holder == SteamClient.SteamId.Value) return;   // relay echo of our own

                if (Plugin.IsHost)
                {
                    Plugin.NetworkManager.SendToAllExcept(sender, PacketType.MooringRopeHeld,
                        w => { w.Write(boatName); w.Write((byte)index); w.Write(holder); });
                }

                var rope = Resolve(boatName, index);
                if (rope == null) return;   // boat not streamed in here; nothing to show, nothing to fix

                if (holder == 0UL)
                {
                    if (_remotelyHeld.Remove(rope))
                    {
                        // Put it back where vanilla parks an unheld rope. Without this the rope would hang
                        // in mid-air at the spot the carrier let go, because nothing local is driving it.
                        rope.ResetRopePos();
                    }
                }
                else _remotelyHeld[rope] = holder;
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("[MooringHold] Could not read a rope-hold packet: " + e.Message);
            }
        }

        // ---- per-frame ----------------------------------------------------------------------------------

        /// <summary>
        /// Park each remotely-held rope on its carrier. Called from LateUpdate so the boat and the avatar
        /// have both been placed for this frame - the same ordering the remote held-item visuals rely on.
        /// </summary>
        public static void LateUpdate()
        {
            if (_remotelyHeld.Count == 0) return;
            try
            {
                List<PickupableBoatMooringRope> stale = null;
                foreach (var kvp in _remotelyHeld)
                {
                    var rope = kvp.Key;
                    if (rope == null) { (stale ?? (stale = new List<PickupableBoatMooringRope>())).Add(rope); continue; }

                    // A rope the carrier has since moored is owned by the mooring sync, not by us.
                    if (rope.IsMoored()) { (stale ?? (stale = new List<PickupableBoatMooringRope>())).Add(rope); continue; }

                    var avatar = Plugin.RemotePlayerManager?.GetAvatar(kvp.Value);
                    var capsule = avatar?.GetRemoteCapsule();
                    if (capsule == null) continue;   // carrier not spawned/streamed yet; leave the rope be

                    rope.transform.position = capsule.TransformPoint(CarryOffset);
                }

                if (stale != null) foreach (var r in stale) _remotelyHeld.Remove(r);
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("[MooringHold] Pin failed: " + e.Message);
            }
        }

        // ---- identity -----------------------------------------------------------------------------------

        /// <summary>(boat, rope index) - the same addressing the moor/unmoor packets use.</summary>
        private static bool Identify(PickupableBoatMooringRope rope, out string boatName, out int index)
        {
            boatName = null; index = -1;
            var boat = rope.GetBoatRigidbody()?.GetComponent<SaveableObject>();
            if (boat == null) return false;
            var ropes = boat.GetComponent<BoatMooringRopes>();
            if (ropes == null || ropes.ropes == null) return false;
            index = System.Array.IndexOf(ropes.ropes, rope);
            if (index < 0) return false;
            boatName = boat.gameObject.name;
            return true;
        }

        private static PickupableBoatMooringRope Resolve(string boatName, int index)
        {
            if (string.IsNullOrEmpty(boatName) || index < 0) return null;
            var boat = BoatUtility.FindBoatByName(boatName);
            if (boat == null) return null;
            var ropes = boat.GetComponent<BoatMooringRopes>();
            if (ropes == null || ropes.ropes == null || index >= ropes.ropes.Length) return null;
            return ropes.ropes[index];
        }
    }
}
