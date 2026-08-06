using System.Collections.Generic;
using Steamworks;
using UnityEngine;
using SailwindCoop.Networking.Packets;

namespace SailwindCoop.Sync
{
    /// <summary>
    /// (v0.3.0) Shows a mooring rope as CARRIED by the crewmate carrying it.
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

        /// <summary>(v0.3.0) A rope mid-flight toward a cleat on this machine, replaying the thrower's
        /// vanilla ThrowRopeSequence.</summary>
        private struct ThrowFlight
        {
            public Vector3 Target;
            public float Elapsed;
        }

        private static readonly Dictionary<PickupableBoatMooringRope, ThrowFlight> _thrown =
            new Dictionary<PickupableBoatMooringRope, ThrowFlight>();

        /// <summary>Vanilla's throw duration (PickupableBoatMooringRope.ThrowRopeSequence).</summary>
        private const float ThrowFlightSeconds = 0.8f;

        public static bool IsRemotelyHeld(PickupableBoatMooringRope rope)
        {
            return rope != null && _remotelyHeld.ContainsKey(rope);
        }

        /// <summary>
        /// (v0.3.0) True while this machine is REPLAYING someone else's throw.
        ///
        /// Vanilla's own thrower disables the rope's collider for the whole 0.8s flight and re-enables it at
        /// the target (ThrowRopeSequence), which is what makes the moor happen once, at the cleat that was
        /// aimed at. DriveThrows copies the lerp but deliberately does not copy the collider disable: _thrown
        /// entries are dropped on several paths that could never reach a matching re-enable, and a leaked
        /// disable would make the rope permanently unmoorable for the whole crew.
        ///
        /// So the flying rope here keeps a live trigger, and the moment the flight is armed the rope leaves
        /// _remotelyHeld, which is the only thing MooringRopeTriggerPatch consults. The local moor is
        /// therefore allowed to happen - it is harmless and the thrower's authoritative packet overwrites it
        /// - but BROADCASTING it is not, or every receiver would author a moor with its own rope length,
        /// several tenths of a second before the person who actually threw the rope.
        /// </summary>
        public static bool IsFlightInProgress(PickupableBoatMooringRope rope)
        {
            return rope != null && _thrown.ContainsKey(rope);
        }

        public static void Clear() { _remotelyHeld.Clear(); _thrown.Clear(); }

        /// <summary>
        /// (v0.3.0) A crewmate vanished while carrying a rope - quit to the menu, dropped, timed out. They
        /// never sent a release, so without this their rope stays pinned to an avatar that no longer exists;
        /// LateUpdate then stops moving it (no capsule) and it hangs in the air wherever they last stood,
        /// for the rest of the session. Put it back where vanilla parks an unheld rope instead.
        ///
        /// Both leave paths call this. A clean lobby leave and a P2P drop are different handlers, and only
        /// wiring one of them leaves the other case broken.
        /// </summary>
        public static void OnPeerLeft(ulong peerId)
        {
            List<PickupableBoatMooringRope> done = null;
            foreach (var kvp in _remotelyHeld)
                if (kvp.Value == peerId)
                    (done ?? (done = new List<PickupableBoatMooringRope>())).Add(kvp.Key);

            if (done == null) return;
            foreach (var rope in done)
            {
                _remotelyHeld.Remove(rope);
                _thrown.Remove(rope);
                // Only if it is still loose: a rope they managed to moor on the way out belongs to the
                // mooring sync now, and resetting it would tear it off the cleat.
                try { if (rope != null && !rope.IsMoored()) rope.ResetRopePos(); }
                catch (System.Exception e) { Plugin.Log.LogWarning("[MooringHold] Stow on peer-left failed: " + e.Message); }
            }
            Plugin.Log.LogInfo($"[MooringHold] Stowed {done.Count} rope(s) carried by departed peer {peerId}");
        }

        // ---- send ---------------------------------------------------------------------------------------

        /// <summary>
        /// (v0.3.0) Local player THREW a rope at a cleat. Replicates the flight instead of the outcome.
        ///
        /// Vanilla's ThrowRopeSequence is only a lerp of the rope's world position toward the cleat over
        /// 0.8s - it does not moor anything, the trigger does that afterwards - so a receiver can replay it
        /// verbatim with no risk of mooring twice.
        ///
        /// Before this, the throw was a choice between two wrong pictures. Broadcasting the release at
        /// OnDrop (which vanilla fires immediately, before the rope has gone anywhere) stowed the rope on
        /// every other screen for the length of the flight, which reads as the rope vanishing. Suppressing
        /// the release instead kept it in the thrower's hands for the same 0.8s, which reads as lag. Sending
        /// the TARGET lets everyone watch the same throw the thrower is watching.
        ///
        /// The position is sent origin-independent: two machines in different floating-origin regions
        /// disagree about world coordinates by kilometres, and a rope lerping toward a point in the wrong
        /// region is a much louder bug than the one being fixed.
        /// </summary>
        public static void OnLocalThrow(PickupableBoatMooringRope rope, Vector3 worldTarget)
        {
            try
            {
                if (!Plugin.IsMultiplayer || Plugin.NetworkManager == null || rope == null) return;

                string boatName;
                int index;
                if (!Identify(rope, out boatName, out index)) return;

                var offset = FloatingOriginManager.instance != null
                    ? FloatingOriginManager.instance.outCurrentOffset : Vector3.zero;
                Vector3 realTarget = worldTarget - offset;

                ulong me = SteamClient.SteamId.Value;
                Plugin.NetworkManager.SendToAllReliable(PacketType.MooringRopeHeld,
                    w =>
                    {
                        w.Write(boatName);
                        w.Write((byte)index);
                        w.Write(me);              // still "held" to an old peer, i.e. the pre-throw behavior
                        w.Write((byte)1);         // this one is a throw
                        w.Write(realTarget.x); w.Write(realTarget.y); w.Write(realTarget.z);
                    });

                // Arm the miss-release here rather than in the drop patch: this is the moment we know a
                // throw actually started, and a throw that never moors still has to let go eventually.
                ScheduleThrowRelease(rope);
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("[MooringHold] Throw send failed: " + e.Message);
            }
        }

        /// <summary>Local player picked a rope up or put it down. held=false means released.</summary>
        public static void OnLocalHoldChanged(PickupableBoatMooringRope rope, bool held)
        {
            try
            {
                if (!Plugin.IsMultiplayer || Plugin.NetworkManager == null || rope == null) return;

                // (v0.3.0) SELF-HEAL. If this machine's player has the rope in their hands, no remote
                // crewmate can also be holding it, whatever we last believed. A stale entry here is not
                // cosmetic: MooringRopeTriggerPatch suppresses vanilla's moor trigger for anything in this
                // set, so a rope wrongly marked remotely-held cannot be moored by the person actually
                // carrying it - reported as "I can take the rope, but when I try to moor it, it just
                // disappears and doesn't moor".
                // A rope back in our own hands is also not in flight from anyone else's throw, and a stale
                // _thrown entry would keep DriveThrows dragging it out of our grip toward an old cleat.
                if (held) { _remotelyHeld.Remove(rope); _thrown.Remove(rope); }

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

        /// <summary>
        /// (v0.3.0) The local player threw a rope at a cleat, so the release was deliberately NOT sent -
        /// see MooringRopeDropPatch. This is the safety net for a throw that does not end in a moor.
        ///
        /// WHY IT IS REQUIRED, learned the hard way: suppressing the release during a throw removed the
        /// half-second flicker, but a throw that MISSES leaves every other machine still carrying the rope
        /// on that crewmate, forever, because no second release is ever generated. Reported as "I set it
        /// down on my screen and the other player still sees me holding it no matter how far away I go",
        /// and it compounded - both players ended up seeing each other holding a rope neither of them had.
        ///
        /// Deliberately gated on the OUTCOME rather than on the `throwing` flag. If the rope moored, the
        /// receivers already dropped it when IsMoored() went true, and re-sending a release would only
        /// invite a stow. If someone has it in their hands, it is genuinely held. Everything else is a
        /// miss, and a miss must release. That also covers a `throwing` flag left latched by an
        /// interrupted coroutine, which the flag itself could never recover from.
        /// </summary>
        public static void ScheduleThrowRelease(PickupableBoatMooringRope rope)
        {
            if (rope == null || Plugin.Instance == null) return;
            try { Plugin.Instance.StartCoroutine(ThrowReleaseFallback(rope)); }
            catch (System.Exception e) { Plugin.Log.LogWarning("[MooringHold] Could not arm the throw fallback: " + e.Message); }
        }

        /// <summary>
        /// Just past vanilla's 0.8s ThrowRopeSequence lerp. This is dead time on a MISSED throw - the rope
        /// stays in the thrower's hands on every other screen until it elapses - and 1.5s was long enough to
        /// be read as the rope being stuck again ("is there just a significant delay?"). A moor still hands
        /// over instantly, because the receiver drops the rope the moment IsMoored() goes true rather than
        /// waiting for this.
        /// </summary>
        private const float ThrowSettleSeconds = 1.0f;

        private static System.Collections.IEnumerator ThrowReleaseFallback(PickupableBoatMooringRope rope)
        {
            yield return new WaitForSecondsRealtime(ThrowSettleSeconds);

            if (rope == null) yield break;
            if (rope.IsMoored()) yield break;   // it landed; the mooring packets own it now
            if (rope.held != null) yield break; // back in someone's hands locally

            Plugin.Log.LogInfo("[MooringHold] Throw did not moor - releasing the rope for the rest of the crew.");
            OnLocalHoldChanged(rope, false);
        }

        // ---- receive ------------------------------------------------------------------------------------

        public static void OnReceived(SteamId sender, System.IO.BinaryReader reader)
        {
            try
            {
                string boatName = reader.ReadString();
                int index = reader.ReadByte();
                ulong holder = reader.ReadUInt64();

                // (v0.3.0) Trailing throw block. Absent on a plain pick-up or put-down, and on any sender
                // that predates it - read tolerantly so those still parse as the simple hold they are.
                bool isThrow = false;
                Vector3 realTarget = Vector3.zero;
                if (reader.BaseStream.Position < reader.BaseStream.Length)
                {
                    isThrow = reader.ReadByte() != 0;
                    // Three floats, so twelve bytes must actually be there. A throw flag with a truncated
                    // target would otherwise fly the rope at the origin, which is a far worse picture than
                    // the delay this replaces.
                    if (isThrow && reader.BaseStream.Position + 12 <= reader.BaseStream.Length)
                        realTarget = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                    else
                        isThrow = false;
                }

                if (holder == SteamClient.SteamId.Value) return;   // relay echo of our own

                if (Plugin.IsHost)
                {
                    Plugin.NetworkManager.SendToAllExcept(sender, PacketType.MooringRopeHeld,
                        w =>
                        {
                            w.Write(boatName); w.Write((byte)index); w.Write(holder);
                            w.Write((byte)(isThrow ? 1 : 0));
                            if (isThrow) { w.Write(realTarget.x); w.Write(realTarget.y); w.Write(realTarget.z); }
                        });
                }

                var rope = Resolve(boatName, index);
                if (rope == null) return;   // boat not streamed in here; nothing to show, nothing to fix

                if (holder == 0UL)
                {
                    _thrown.Remove(rope);
                    if (_remotelyHeld.Remove(rope))
                    {
                        // Put it back where vanilla parks an unheld rope. Without this the rope would hang
                        // in mid-air at the spot the carrier let go, because nothing local is driving it.
                        rope.ResetRopePos();
                    }
                }
                else if (isThrow)
                {
                    // Hand the rope from the carrier's grip to the flight. Both must not drive it at once:
                    // the carrier pin would win every frame and the rope would never leave their hands.
                    _remotelyHeld.Remove(rope);

                    // Not if it is in OUR hands. Two players can grab one rope within a round trip of each
                    // other, and if the other one throws before our own pickup reaches them, we would arm a
                    // flight for a rope this player is holding: DriveThrows would drag it toward their cleat
                    // every frame while the pointer drags it back, and the flight would suppress this
                    // player's own moor for its whole duration. Whoever has it locally wins.
                    if (rope.held != null) return;
                    var offset = FloatingOriginManager.instance != null
                        ? FloatingOriginManager.instance.outCurrentOffset : Vector3.zero;
                    _thrown[rope] = new ThrowFlight
                    {
                        Target = realTarget + offset,
                        Elapsed = 0f,
                    };
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
            DriveThrows();
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

        /// <summary>
        /// (v0.3.0) Fly each thrown rope to its cleat, replaying vanilla's ThrowRopeSequence exactly.
        ///
        /// The easing is copied rather than approximated, and it is worth knowing that it is NOT a straight
        /// interpolation: vanilla lerps from the rope's CURRENT position toward the target each frame with a
        /// ratio that itself grows, so the rope accelerates and then eases in. Substituting a plain
        /// Lerp(start, target, t) would arrive at the same place by a visibly different path, and the point
        /// of this is that the throw looks the same to everyone watching.
        ///
        /// A rope that moors mid-flight is dropped immediately: the mooring sync owns it from that moment,
        /// and the cleat is where the flight was heading anyway.
        /// </summary>
        private static void DriveThrows()
        {
            if (_thrown.Count == 0) return;
            try
            {
                List<PickupableBoatMooringRope> done = null;
                var keys = new List<PickupableBoatMooringRope>(_thrown.Keys);
                foreach (var rope in keys)
                {
                    var flight = _thrown[rope];

                    if (rope == null || rope.IsMoored() || flight.Elapsed > ThrowFlightSeconds)
                    {
                        (done ?? (done = new List<PickupableBoatMooringRope>())).Add(rope);
                        continue;
                    }

                    rope.transform.position = Vector3.Lerp(
                        rope.transform.position, flight.Target, flight.Elapsed / ThrowFlightSeconds);
                    // Wall-clock by intent. On scaled time a machine that pauses mid-flight stops aging the
                    // entry entirely, so the rope hangs halfway to the cleat until that player unpauses.
                    flight.Elapsed += Time.unscaledDeltaTime;
                    _thrown[rope] = flight;
                }

                if (done != null) foreach (var r in done) _thrown.Remove(r);
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("[MooringHold] Throw flight failed: " + e.Message);
                _thrown.Clear();
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
