using System.Collections.Generic;
using HarmonyLib;
using Steamworks;
using UnityEngine;
using SailwindCoop.Networking.Packets;

namespace SailwindCoop.Sync
{
    /// <summary>
    /// (v0.3.0) Shows a crewmate ADJUSTING a moored rope's length - the R interaction - to the rest of the
    /// crew.
    ///
    /// THE GAP THIS FILLS, and what it is NOT. The rope's LENGTH has been synced since well before this:
    /// MooringRopeLengthPatch postfixes ChangeRopeLength and ControlSyncManager streams the float, so a
    /// remote client's moored rope really does take up and pay out slack while someone scrolls. What was
    /// missing is that nobody could SEE who was doing it. Pressing R hands the player a separate
    /// MooringRopeLengthAdjuster object with a pull-rope running back to the cleat, and none of that object
    /// was ever touched on other machines - so from the other end the rope simply tightened by itself.
    ///
    /// WHY SYNCING THE FLOAT HARDER WOULD NOT HAVE HELPED. The adjuster's appearance is not a function of
    /// the length. It is LATCHED state on a second GameObject, set once at pickup and cleared once at
    /// release: OnPickup re-points the RopeEffect at the boat attachment, swaps the line renderer to the
    /// pull material, hides the parked coil renderer and shows the spinning one. No amount of length
    /// traffic produces it. It needs an explicit begin/end signal, which is what packet 222 is.
    ///
    /// WHY A NEW PACKET RATHER THAN A FLAG ON 221. Packet 221 (rope carried) is only ever sent for ropes
    /// that are NOT moored - vanilla unmoors on pickup - and its receiver calls ResetRopePos on release.
    /// Overloading it for the adjust case would have made an older peer call ResetRopePos on a MOORED rope,
    /// permanently displacing the rope end at the cleat and poisoning the distance the sag is computed
    /// from. An unknown packet type is logged and dropped, so a separate type is the fail-open choice.
    ///
    /// THE RECEIVER MUST NOT SET `held`. That field is what vanilla routes the local scroll wheel through
    /// (GoPointer.LateUpdate -> heldItem.OnScroll -> ChangeRopeLength), so a receiver that set it would let
    /// this machine's mouse wheel silently retrim a rope a different player is holding. The cost of leaving
    /// it null is that vanilla's own Update gates the coil spin and pull-material animation on it, so those
    /// two are driven here instead, from the same formulas and the already-synced length.
    /// </summary>
    public static class MooringRopeAdjustSync
    {
        // Rope -> who is adjusting it (remote adjusters only; our own hands need no bookkeeping).
        private static readonly Dictionary<PickupableBoatMooringRope, ulong> _remotelyAdjusted =
            new Dictionary<PickupableBoatMooringRope, ulong>();

        /// <summary>Held offset from the adjuster's avatar origin. Slightly lower and closer than the rope
        /// carry offset - this is a hauling grip, not a coil carried against the chest.</summary>
        private static readonly Vector3 HoldOffset = new Vector3(0f, 0.05f, 0.38f);

        /// <summary>Set while we are replaying a remote pickup/release through vanilla's own methods, so the
        /// patches that broadcast local ones do not echo it back out.</summary>
        private static bool _applying;
        public static bool IsApplying { get { return _applying; } }

        // `lengthAdjuster` is private on the rope and has no getter, but it is the authoritative link (the
        // adjuster registers itself in Awake), so reading it beats scanning the boat for a matching one.
        private static readonly AccessTools.FieldRef<PickupableBoatMooringRope, MooringRopeLengthAdjuster>
            AdjusterRef = AccessTools.FieldRefAccess<PickupableBoatMooringRope, MooringRopeLengthAdjuster>("lengthAdjuster");

        // (v0.3.0) WHICH END the pull rope runs to. There is only ONE adjuster per rope, but two ways to
        // take hold of it, and they differ by exactly this bit:
        //   R on the moored ROPE  -> OnPickup() then PickupFromMooring() -> attachment = the rope at the
        //                            DOCK cleat, pickedUpFromMooring = true
        //   R/click on the parked ADJUSTER (which lives on the ship) -> OnPickup() alone -> attachment =
        //                            boatAttachment, i.e. the SHIP cleat
        // Pressing R again while holding is not a third case: GoPointer routes alt-activate to the HELD item,
        // so it re-enters OnPickup and flips the dock state back to the ship state.
        //
        // The first version of this replayed OnPickup + PickupFromMooring unconditionally, so every remote
        // machine drew the dock variant no matter which end the holder actually grabbed - reported as
        // "picking up the adjust on the ship still shows me picking it up on the dock cleat".
        private static readonly AccessTools.FieldRef<MooringRopeLengthAdjuster, bool>
            FromMooringRef = AccessTools.FieldRefAccess<MooringRopeLengthAdjuster, bool>("pickedUpFromMooring");

        // Rope -> which end the remote holder took it from, so a state CHANGE (path C) re-applies.
        private static readonly Dictionary<PickupableBoatMooringRope, bool> _adjustFromMooring =
            new Dictionary<PickupableBoatMooringRope, bool>();

        public static bool IsRemotelyAdjusted(PickupableBoatMooringRope rope)
        {
            return rope != null && _remotelyAdjusted.ContainsKey(rope);
        }

        public static void Clear()
        {
            // Release anything still latched, or the pull rope and spinning coil would be left on screen
            // after the session ends and follow the player into singleplayer.
            foreach (var kvp in _remotelyAdjusted) ReleaseVisual(kvp.Key);
            _remotelyAdjusted.Clear();
            _adjustFromMooring.Clear();
        }

        /// <summary>(v0.3.0) A crewmate vanished (quit, timed out, streamed out) while holding an adjuster.
        /// Without this the pull rope stays stretched to wherever they were standing for the rest of the
        /// session, because nothing else ever clears the latch.</summary>
        public static void OnPeerLeft(ulong peerId)
        {
            List<PickupableBoatMooringRope> done = null;
            foreach (var kvp in _remotelyAdjusted)
                if (kvp.Value == peerId)
                    (done ?? (done = new List<PickupableBoatMooringRope>())).Add(kvp.Key);

            if (done == null) return;
            foreach (var rope in done)
            {
                ReleaseVisual(rope);
                _remotelyAdjusted.Remove(rope);
                _adjustFromMooring.Remove(rope);
            }
            Plugin.Log.LogInfo($"[MooringAdjust] Released {done.Count} adjuster(s) held by departed peer {peerId}");
        }

        // ---- send ---------------------------------------------------------------------------------------

        /// <summary>
        /// Local player took hold of a length adjuster, or let it go.
        ///
        /// A PICKUP is reported at the END OF THE FRAME rather than immediately. Vanilla's dock-end path is
        /// `PickUpItem(adjuster)` followed by `PickupFromMooring()`, and this is called from a postfix on the
        /// first of those - so at the moment we run, `pickedUpFromMooring` is still false whichever end was
        /// grabbed, and sending now would report every pickup as the ship end. One frame later the pair has
        /// finished and the flag is settled.
        /// </summary>
        public static void OnLocalAdjustChanged(MooringRopeLengthAdjuster adjuster, bool held)
        {
            try
            {
                if (_applying) return;   // we are replaying a remote one; do not echo it back
                if (!Plugin.IsMultiplayer || Plugin.NetworkManager == null || adjuster == null) return;

                // (v0.3.0) SELF-HEAL, the same one MooringRopeHoldSync needed for the rope itself.
                // Nothing stops two players grabbing one adjuster: vanilla's dock-end path is
                // PickupableBoatMooringRope.OnAltActivate, which raycasts the ROPE and then calls
                // PickUpItem(lengthAdjuster) without ever consulting the adjuster's own layer or `held`, and
                // receivers deliberately leave `held` null. So pressing R on a moored rope always takes the
                // adjuster, even when a crewmate is already hauling it.
                //
                // Without this, our own adjuster stays in _remotelyAdjusted, LateUpdate overwrites its
                // transform with that crewmate's capsule every frame, and their eventual release runs
                // ReleaseVisual on an adjuster we are still holding - which writes layer 0 and calls
                // OnDrop(). The layer write is the damaging half: the held item then falls inside the
                // pointer's own raycast mask, so the pointer targets what the player is holding and F stops
                // releasing it. Clearing here means the peer's later holder==0 packet finds nothing to
                // release. This is only half of it: the send side covers the player who grabs SECOND, and
                // OnReceived covers the one who grabbed first and then receives the other's grab.
                if (held && adjuster.mooringRope != null)
                {
                    _remotelyAdjusted.Remove(adjuster.mooringRope);
                    _adjustFromMooring.Remove(adjuster.mooringRope);
                }

                if (adjuster.mooringRope == null) return;

                if (held) Plugin.Instance?.StartCoroutine(SendGrabAtEndOfFrame(adjuster));
                else SendAdjustState(adjuster.mooringRope, false, false);
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("[MooringAdjust] Send failed: " + e.Message);
            }
        }

        private static System.Collections.IEnumerator SendGrabAtEndOfFrame(MooringRopeLengthAdjuster adjuster)
        {
            yield return null;

            if (adjuster == null || adjuster.mooringRope == null) yield break;
            // Let go again within the frame: the release send already covered it.
            if (adjuster.held == null) yield break;

            bool fromMooring = true;
            try { fromMooring = FromMooringRef(adjuster); }
            catch { /* field moved in a game update: assume the dock end, which is the commoner grab */ }

            SendAdjustState(adjuster.mooringRope, true, fromMooring);
        }

        private static void SendAdjustState(PickupableBoatMooringRope rope, bool held, bool fromMooring)
        {
            string boatName;
            int index;
            if (!Identify(rope, out boatName, out index)) return;

            ulong me = SteamClient.SteamId.Value;
            Plugin.NetworkManager.SendToAllReliable(PacketType.MooringRopeAdjusting,
                w =>
                {
                    w.Write(boatName);
                    w.Write((byte)index);
                    w.Write(held ? me : 0UL);
                    w.Write((byte)(fromMooring ? 1 : 0));
                });
        }

        // ---- receive ------------------------------------------------------------------------------------

        public static void OnReceived(SteamId sender, System.IO.BinaryReader reader)
        {
            try
            {
                string boatName = reader.ReadString();
                int index = reader.ReadByte();
                ulong holder = reader.ReadUInt64();
                // Trailing field, read tolerantly. A mixed-version crew is reachable through
                // Coop.AllowVersionMismatch, and an older HOST relaying this packet re-serializes only the
                // first three fields - so an unconditional ReadByte here would throw on a stripped body, the
                // catch below would swallow the whole packet, and adjust sync would degrade to NOTHING
                // instead of to its previous behavior. Absent means the dock end, which is what every build
                // before this one drew.
                bool fromMooring = true;
                if (reader.BaseStream.Position < reader.BaseStream.Length)
                    fromMooring = reader.ReadByte() != 0;

                if (holder == SteamClient.SteamId.Value) return;   // relay echo of our own

                if (Plugin.IsHost)
                {
                    Plugin.NetworkManager.SendToAllExcept(sender, PacketType.MooringRopeAdjusting,
                        w =>
                        {
                            w.Write(boatName); w.Write((byte)index); w.Write(holder);
                            w.Write((byte)(fromMooring ? 1 : 0)); // relay the end too, or peers behind us lose it
                        });
                }

                var rope = Resolve(boatName, index);
                if (rope == null) return;   // boat not streamed in here; nothing to show, nothing to fix

                if (holder == 0UL)
                {
                    if (_remotelyAdjusted.Remove(rope)) ReleaseVisual(rope);
                    _adjustFromMooring.Remove(rope);
                }
                else
                {
                    // (v0.3.0) The receive half of the double-grab heal. If this machine's player has the
                    // adjuster in their hands, no crewmate's grab of it can be true here - and drawing it
                    // anyway would run GrabVisual on an adjuster they are holding, then pin it to the other
                    // player's avatar every frame. Recording nothing is what makes this safe to skip: there
                    // is no entry left behind for their eventual release to have to clean up, so it cannot
                    // weld the adjuster to a crewmate the way a guard on the RELEASE branch would. The cost
                    // is that their hold is not drawn here until they take it again.
                    var localAdjuster = AdjusterRef(rope);
                    if (localAdjuster != null && localAdjuster.held != null) return;

                    // Re-apply when the END changes as well as on a fresh grab: pressing R while already
                    // holding flips dock <-> ship, and that is a real state change the watcher must see.
                    bool known = _adjustFromMooring.TryGetValue(rope, out var prevEnd);
                    if (!_remotelyAdjusted.ContainsKey(rope) || !known || prevEnd != fromMooring)
                        GrabVisual(rope, fromMooring);
                    _remotelyAdjusted[rope] = holder;
                    _adjustFromMooring[rope] = fromMooring;
                }
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("[MooringAdjust] Could not read a rope-adjust packet: " + e.Message);
            }
        }

        /// <summary>Replay vanilla's own pickup on this machine. OnPickup does the whole visual latch and
        /// PickupFromMooring re-points the pull rope at the cleat, exactly as the R path does locally.</summary>
        private static void GrabVisual(PickupableBoatMooringRope rope, bool fromMooring)
        {
            var adjuster = AdjusterRef(rope);
            if (adjuster == null) return;
            try
            {
                _applying = true;
                // OnPickup alone IS the ship-end state (attachment = boatAttachment). PickupFromMooring is
                // what re-points the pull rope at the dock. Replaying both unconditionally is what made every
                // grab look like a dock grab on other machines.
                adjuster.OnPickup();
                if (fromMooring) adjuster.PickupFromMooring();
                // Mirror the layer vanilla's PickUpItem sets, so this machine's own pointer cannot raycast
                // a grab on an adjuster a crewmate is already holding.
                adjuster.gameObject.layer = 2;
            }
            catch (System.Exception e) { Plugin.Log.LogWarning("[MooringAdjust] Grab visual failed: " + e.Message); }
            finally { _applying = false; }
        }

        /// <summary>Hand it back. OnDrop starts vanilla's ReturnRopeSequence, which lerps the adjuster home
        /// and restores the material, renderer and coil - the full release animation, for free.</summary>
        private static void ReleaseVisual(PickupableBoatMooringRope rope)
        {
            if (rope == null) return;
            var adjuster = AdjusterRef(rope);
            if (adjuster == null) return;
            try
            {
                _applying = true;
                adjuster.gameObject.layer = 0;
                adjuster.OnDrop();
            }
            catch (System.Exception e) { Plugin.Log.LogWarning("[MooringAdjust] Release visual failed: " + e.Message); }
            finally { _applying = false; }
        }

        // ---- per-frame ----------------------------------------------------------------------------------

        /// <summary>
        /// Hold each remotely-adjusted adjuster in its holder's hands and drive the two animations vanilla
        /// gates on `held`. LateUpdate so the boat and the avatar are both placed for this frame.
        /// </summary>
        public static void LateUpdate()
        {
            if (_remotelyAdjusted.Count == 0) return;
            try
            {
                List<PickupableBoatMooringRope> stale = null;
                foreach (var kvp in _remotelyAdjusted)
                {
                    var rope = kvp.Key;
                    if (rope == null) { (stale ?? (stale = new List<PickupableBoatMooringRope>())).Add(rope); continue; }

                    // Vanilla only offers the adjuster on a moored rope; if it has come off the cleat since,
                    // the latch is meaningless and the release visual is owned by the mooring sync.
                    if (!rope.IsMoored()) { (stale ?? (stale = new List<PickupableBoatMooringRope>())).Add(rope); continue; }

                    var adjuster = AdjusterRef(rope);
                    if (adjuster == null) continue;

                    var avatar = Plugin.RemotePlayerManager?.GetAvatar(kvp.Value);
                    var capsule = avatar?.GetRemoteCapsule();
                    if (capsule == null) continue;   // holder not spawned/streamed yet; leave it be

                    adjuster.transform.position = capsule.TransformPoint(HoldOffset);
                    DriveHeldAnimation(adjuster, rope);
                }

                if (stale != null)
                    foreach (var r in stale) { ReleaseVisual(r); _remotelyAdjusted.Remove(r); _adjustFromMooring.Remove(r); }
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("[MooringAdjust] Pin failed: " + e.Message);
            }
        }

        /// <summary>
        /// Reproduce the body of vanilla's `if (held)` block. Same formulas, same inputs - the distance from
        /// the boat attachment, and the rope length the length packets already keep in step - so a watching
        /// player sees the coil turn as the holder hauls, rather than a static prop on a moving rope.
        ///
        /// The pull material is a private STATIC on the adjuster class, shared by every adjuster in the
        /// scene. Two people hauling two different ropes at once therefore scroll one texture between them.
        /// That is vanilla's own design and the visible result is a slightly wrong scroll rate on a rope
        /// texture, so it is not worth cloning a material per rope to avoid.
        /// </summary>
        private static void DriveHeldAnimation(MooringRopeLengthAdjuster adjuster, PickupableBoatMooringRope rope)
        {
            var attach = adjuster.boatAttachment;
            if (attach == null || adjuster.transform.childCount == 0) return;

            float num = Vector3.SqrMagnitude(adjuster.transform.position - attach.position) * 6f;
            float coilFromLength = rope.currentRopeLengthSquared * 40f;

            var coil = adjuster.transform.GetChild(0);
            coil.localEulerAngles = new Vector3(num + coilFromLength, 0f, 0f) * -1f;

            var pull = PullMaterial;
            if (pull != null)
            {
                pull.mainTextureScale = new Vector2(num * 0.015f + 4f, 0.44f);
                pull.mainTextureOffset = new Vector2(num * -0.002f + rope.currentRopeLengthSquared * 15f, 0f);
            }
        }

        private static bool _pullMaterialResolved;
        private static Material _pullMaterial;
        private static Material PullMaterial
        {
            get
            {
                // Resolved lazily: it is created in the adjuster's Awake, so it does not exist at load time.
                if (!_pullMaterialResolved)
                {
                    try
                    {
                        var f = AccessTools.Field(typeof(MooringRopeLengthAdjuster), "pullMaterial");
                        _pullMaterial = f?.GetValue(null) as Material;
                    }
                    catch { _pullMaterial = null; }
                    _pullMaterialResolved = _pullMaterial != null;
                }
                return _pullMaterial;
            }
        }

        // ---- identity -----------------------------------------------------------------------------------

        /// <summary>(boat, rope index) - the same addressing the moor/unmoor and rope-carry packets use.</summary>
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
