using System.Collections;
using SailwindCoop.Debug;
using SailwindCoop.Networking.Packets;
using Steamworks;
using UnityEngine;

namespace SailwindCoop.Sync
{
    /// <summary>
    /// (v0.2.32) Generic trapdoor/door/hatch sync - co-op synced NO GoPointerButton of any kind
    /// before this, so doors desynced on vanilla boats too. Design mirrors the mooring sync:
    /// peer-origin local events (TrapdoorPatches postfix) broadcast ABSOLUTE open state, the host
    /// star-relays, receivers converge by invoking OnActivate() only when their local IsOpen()
    /// differs (never a click replay). GPButtonTrapdoor.OnActivate() silently no-ops while its
    /// private inMotion coroutine runs, so the applier retries until the state matches.
    ///
    /// Trapdoor key: "{name}~{occurrence}" over GetComponentsInChildren enumeration order. Trapdoors
    /// are prefab-baked hull children (never part of the runtime-rebuilt sail hierarchy), so the
    /// enumeration order is prefab-deterministic and identical cross-machine - the property the rope
    /// sync had to build GetStableRopeKey to recover, available here for free.
    ///
    /// Leopard gunports ride the SAME packet with IsGunportGroup=true and Key = the group name:
    /// the mod fans one click out to the whole group and toggles the flooding masks with !activeSelf
    /// (HMSLeopard Patch_OnActivate.cs), so per-port packets would multiply and any drift would
    /// INVERT a guest's flooding. Group intent + ForceGunportAbsolutes keeps it convergent.
    /// </summary>
    public class TrapdoorSyncManager : MonoBehaviour
    {
        public static TrapdoorSyncManager Instance { get; private set; }

        public bool IsApplyingRemoteState { get; private set; }

        private const int ApplyRetryAttempts = 10;
        private const float ApplyRetryDelay = 0.3f;

        private void Awake()
        {
            Instance = this;
        }

        // === Key derivation (both directions; MUST stay symmetric) ===

        public static string KeyFor(SaveableObject boat, GPButtonTrapdoor td)
        {
            if (boat == null || td == null) return null;
            var all = boat.GetComponentsInChildren<GPButtonTrapdoor>(true);
            int occ = 0;
            foreach (var t in all)
            {
                if (t == null || t.name != td.name) continue;
                occ++;
                if (t == td) return td.name + "~" + occ;
            }
            return null; // not under this boat
        }

        public static GPButtonTrapdoor FindByKey(SaveableObject boat, string key)
        {
            if (boat == null || string.IsNullOrEmpty(key)) return null;
            int tilde = key.LastIndexOf('~');
            if (tilde <= 0 || !int.TryParse(key.Substring(tilde + 1), out int wantOcc)) return null;
            string name = key.Substring(0, tilde);
            int occ = 0;
            foreach (var t in boat.GetComponentsInChildren<GPButtonTrapdoor>(true))
            {
                if (t == null || t.name != name) continue;
                occ++;
                if (occ == wantOcc) return t;
            }
            return null;
        }

        /// <summary>Resolve the owning boat root for a trapdoor (trapdoors reparent to
        /// importedActualBoat in Awake, which stays inside the boat hierarchy). Requires BoatRefs so
        /// the name we send is one FindAllBoats on the receiver can actually resolve.</summary>
        public static SaveableObject BoatOf(GPButtonTrapdoor td)
        {
            var so = td != null ? td.GetComponentInParent<SaveableObject>() : null;
            return so != null && so.GetComponent<BoatRefs>() != null ? so : null;
        }

        // === Send path (called from TrapdoorPatches postfix) ===

        public void OnLocalTrapdoorActivated(GPButtonTrapdoor td, bool stateChanged)
        {
            if (!Plugin.IsMultiplayer || td == null) return;
            if (IsApplyingRemoteState) return;
            if (!stateChanged) return; // inMotion no-op: nothing to sync

            // PHANTOM-LOAD GATE (same trio as MooringAttachPatch, ControlPatches.cs:479-480): a
            // guest's own load (incl. NAND Tweaks' toggleDoors restore, which fires OnActivate on
            // load from the guest's PHANTOM save) must never broadcast as authoritative.
            if (TitleJoinManager.SuppressLoadErrors || BoatSyncManager.IsJoinInProgress
                || (!Plugin.IsHost && !BoatSyncManager.HasReceivedWorldState)) return;

            var boat = BoatOf(td);
            if (boat == null) return;

            // Leopard gunport? Fan-out sibling calls are suppressed (recursive flag), the ORIGINATING
            // click sends ONE group packet.
            var group = Compat.LeopardCompat.GunportGroupOf(td.name);
            if (group != null && boat.gameObject.name == Compat.LeopardCompat.LeopardRootName)
            {
                if (Compat.LeopardCompat.IsGunportFanoutInProgress) return; // sibling call, not the click
                Send(new TrapdoorStatePacket
                {
                    BoatName = boat.gameObject.name,
                    Key = group,
                    IsOpen = td.IsOpen(),
                    IsGunportGroup = true
                });
                // (final review) Pin the absolutes on the CLICKER too: the mod's ToggleAudio derives
                // the interior trigger from lowerGunports[0].IsOpen() INSIDE the prefix, before the
                // clicked port's body toggles it - so clicking port [0] itself leaves the clicker's
                // trigger inverted (a genuine Leopard bug; receivers were already healed by the apply
                // path). Pure SetActive, cannot echo.
                Compat.LeopardCompat.ForceGunportAbsolutes(group, td.IsOpen());
                return;
            }

            // Degraded-mode guard: with Leopard INSTALLED but SyncEnabled=false (reflection failed on
            // a future Leopard build), GunportGroupOf returns null and gunports would fall through to
            // per-port sync - and the mod's fan-out would then emit ~24 packets per click whose
            // receiver-side OnActivate re-fans-out. Never sync gunports per-port; drop them here.
            // (Both peers fail reflection identically on the same Leopard version, so both drop.)
            if (Compat.LeopardCompat.IsInstalled && !Compat.LeopardCompat.SyncEnabled
                && td.name.Contains("gunport")) return;

            string key = KeyFor(boat, td);
            if (key == null) return;
            Send(new TrapdoorStatePacket
            {
                BoatName = boat.gameObject.name,
                Key = key,
                IsOpen = td.IsOpen(),
                IsGunportGroup = false
            });
        }

        // === Hinged doors (Sailwind 0.39 GPButtonHouseDoor) ===
        //
        // 0.39 added a second door class: the front door of each buyable house and the five cabin doors of
        // the new large hull. It is a sibling of GPButtonTrapdoor, not a subclass, so nothing above sees it
        // and each player had to open their own copy. It rides the same packet: BoatName is the door's own
        // SaveableObject (a house or a boat) and Key carries the prefix below, which no trapdoor key has.

        private const string HouseDoorKeyPrefix = "housedoor|";

        /// <summary>Every hinged door that belongs to this root, in an order both machines agree on.</summary>
        private static System.Collections.Generic.List<GPButtonHouseDoor> HouseDoorsOf(SaveableObject root)
        {
            var doors = new System.Collections.Generic.List<GPButtonHouseDoor>();
            if (root == null) return doors;

            // Hierarchy order is prefab-baked, and includes doors under a cabin variant that is switched off.
            foreach (var d in root.GetComponentsInChildren<GPButtonHouseDoor>(true))
                if (d != null && d.saveable == root) doors.Add(d);
            if (doors.Count > 0) return doors;

            // A door that points at this root without sitting under it: order by hierarchy path instead.
            foreach (var d in Resources.FindObjectsOfTypeAll<GPButtonHouseDoor>())
                if (d != null && d.saveable == root && d.gameObject.scene.IsValid()) doors.Add(d);
            doors.Sort((a, b) => string.CompareOrdinal(PathOf(a.transform), PathOf(b.transform)));
            return doors;
        }

        private static string PathOf(Transform t)
        {
            var sb = new System.Text.StringBuilder();
            for (; t != null; t = t.parent) sb.Insert(0, "/" + t.name + "#" + t.GetSiblingIndex());
            return sb.ToString();
        }

        private static string HouseDoorKeyFor(SaveableObject root, GPButtonHouseDoor door)
        {
            int occ = 0;
            foreach (var d in HouseDoorsOf(root))
            {
                if (d.name != door.name) continue;
                occ++;
                if (d == door) return HouseDoorKeyPrefix + door.name + "~" + occ;
            }
            return null;
        }

        private static GPButtonHouseDoor FindHouseDoorByKey(SaveableObject root, string key)
        {
            int tilde = key.LastIndexOf('~');
            if (tilde <= HouseDoorKeyPrefix.Length || !int.TryParse(key.Substring(tilde + 1), out int wantOcc)) return null;
            string name = key.Substring(HouseDoorKeyPrefix.Length, tilde - HouseDoorKeyPrefix.Length);
            int occ = 0;
            foreach (var d in HouseDoorsOf(root))
            {
                if (d.name != name) continue;
                occ++;
                if (occ == wantOcc) return d;
            }
            return null;
        }

        public void OnLocalHouseDoorActivated(GPButtonHouseDoor door, bool stateChanged)
        {
            if (!Plugin.IsMultiplayer || door == null) return;
            if (IsApplyingRemoteState) return;
            if (!stateChanged) return; // locked, or mid-swing: nothing to sync

            // Same phantom-load gate as trapdoors.
            if (TitleJoinManager.SuppressLoadErrors || BoatSyncManager.IsJoinInProgress
                || (!Plugin.IsHost && !BoatSyncManager.HasReceivedWorldState)) return;

            var root = door.saveable;
            if (root == null) return;
            string key = HouseDoorKeyFor(root, door);
            if (key == null) return;
            Send(new TrapdoorStatePacket
            {
                BoatName = root.gameObject.name,
                Key = key,
                IsOpen = door.IsOpen(),
                IsGunportGroup = false
            });
        }

        /// <summary>One apply attempt for a hinged door. Same contract as TryApply.</summary>
        private bool TryApplyHouseDoor(TrapdoorStatePacket packet)
        {
            var root = BoatUtility.FindPurchasableByName(packet.BoatName);
            if (root == null)
            {
                VerboseLogger.ControlApply($"TrapdoorState FAILED: door owner '{packet.BoatName}' not found");
                return true; // fatal
            }

            var door = FindHouseDoorByKey(root, packet.Key);
            if (door == null)
            {
                Plugin.Log.LogWarning($"[TRAPDOOR] No door '{packet.Key}' on '{packet.BoatName}'");
                return true; // fatal
            }
            // A door under a cabin variant this machine has switched off cannot run its coroutine.
            if (!door.isActiveAndEnabled) return true;
            if (door.IsOpen() == packet.IsOpen) return true;

            IsApplyingRemoteState = true;
            try
            {
                // No-op while the door is swinging, or while ownership has not arrived here yet (the door
                // only moves on saveable.extraSetting). Either way the caller retries.
                door.OnActivate();
            }
            finally
            {
                IsApplyingRemoteState = false;
            }
            return door.IsOpen() == packet.IsOpen;
        }

        private void Send(TrapdoorStatePacket packet)
        {
            VerboseLogger.ControlSend($"TrapdoorState, boat={packet.BoatName}, key={packet.Key}, open={packet.IsOpen}, group={packet.IsGunportGroup}");
            Plugin.NetworkManager.SendToAllReliable(PacketType.TrapdoorState, w =>
                PacketSerializer.WriteTrapdoorState(w, packet));
        }

        // === Receive path ===

        public void OnRemoteTrapdoorState(TrapdoorStatePacket packet, SteamId sender)
        {
            VerboseLogger.ControlRecv($"TrapdoorState, boat={packet.BoatName}, key={packet.Key}, open={packet.IsOpen}, group={packet.IsGunportGroup}");

            // STAR host-relay, identical to MooringState (ControlSyncManager.cs:1364-1368).
            if (Plugin.IsHost)
            {
                Plugin.NetworkManager.SendToAllExcept(sender, PacketType.TrapdoorState,
                    w => PacketSerializer.WriteTrapdoorState(w, packet));
            }

            StartCoroutine(ApplyWithRetry(packet));
        }

        private IEnumerator ApplyWithRetry(TrapdoorStatePacket packet)
        {
            for (int attempt = 0; attempt < ApplyRetryAttempts; attempt++)
            {
                if (TryApply(packet)) yield break;
                // inMotion (a door animating on this machine) blocks OnActivate; wait it out.
                yield return new WaitForSecondsRealtime(ApplyRetryDelay);
            }
            Plugin.Log.LogWarning($"[TRAPDOOR] Apply gave up after {ApplyRetryAttempts} attempts: " +
                $"boat={packet.BoatName}, key={packet.Key}, open={packet.IsOpen}");
            // Give-up must not strand inverted flooding masks: pin the absolutes one final time.
            if (packet.IsGunportGroup)
                Compat.LeopardCompat.ForceGunportAbsolutes(packet.Key, packet.IsOpen);
        }

        /// <summary>One apply attempt. True = local state now matches (or target unresolvable-fatal).</summary>
        private bool TryApply(TrapdoorStatePacket packet)
        {
            if (!packet.IsGunportGroup && packet.Key != null
                && packet.Key.StartsWith(HouseDoorKeyPrefix, System.StringComparison.Ordinal))
                return TryApplyHouseDoor(packet);

            var boat = BoatUtility.FindBoatByName(packet.BoatName);
            if (boat == null)
            {
                VerboseLogger.ControlApply($"TrapdoorState FAILED: boat '{packet.BoatName}' not found");
                return true; // fatal, no point retrying
            }

            IsApplyingRemoteState = true;
            try
            {
                if (packet.IsGunportGroup)
                {
                    // Only the Leopard sets IsGunportGroup; keep the receiver honest if that ever drifts.
                    if (packet.BoatName != Compat.LeopardCompat.LeopardRootName) return true;

                    var current = Compat.LeopardCompat.GetGunportGroupOpen(packet.Key);
                    if (current == null) return true; // Leopard sync off / lists empty: fatal
                    bool converged = current == packet.IsOpen;
                    if (!converged)
                    {
                        Compat.LeopardCompat.ApplyGunportGroup(packet.Key, packet.IsOpen);
                        converged = Compat.LeopardCompat.GetGunportGroupOpen(packet.Key) == packet.IsOpen;
                    }
                    // ALWAYS force the masks/overflows/triggers absolute, converged or not: the
                    // Leopard prefix toggles them with !activeSelf even when the port's body no-ops
                    // (inMotion), so every failed attempt would otherwise flip a guest's flooding
                    // visuals; forcing after each attempt keeps them pinned to the authoritative
                    // state while the port animation catches up.
                    Compat.LeopardCompat.ForceGunportAbsolutes(packet.Key, packet.IsOpen);
                    return converged;
                }

                var td = FindByKey(boat, packet.Key);
                if (td == null)
                {
                    Plugin.Log.LogWarning($"[TRAPDOOR] No trapdoor '{packet.Key}' on '{packet.BoatName}'");
                    return true; // fatal
                }
                if (td.IsOpen() == packet.IsOpen) return true;
                td.OnActivate(); // no-op while inMotion -> retried by caller
                return td.IsOpen() == packet.IsOpen;
            }
            finally
            {
                IsApplyingRemoteState = false;
            }
        }

        // === Join replay (host -> joiner) ===

        /// <summary>
        /// Send the authoritative state of EVERY trapdoor on every boat to a joining guest. The
        /// guest's own phantom save may have restored doors via NAND Tweaks' toggleDoors before the
        /// snapshot arrived; these reliable, ordered sends win. Leopard gunports collapse to 3 group
        /// packets instead of 24 per-port ones.
        /// </summary>
        public void SendAllStatesTo(SteamId target)
        {
            if (!Plugin.IsMultiplayer) return;
            int sent = 0;
            foreach (var kv in BoatUtility.FindAllBoats())
            {
                var boat = kv.Value;
                bool isLeopard = boat.gameObject.name == Compat.LeopardCompat.LeopardRootName;

                foreach (var td in boat.GetComponentsInChildren<GPButtonTrapdoor>(true))
                {
                    if (td == null) continue;
                    // Gunports never sync per-port: grouped below when healthy, dropped entirely in
                    // degraded mode (GunportGroupOf returns null when !SyncEnabled, but the mod's
                    // fan-out prefix is live regardless - a per-port replay would re-trigger it ~24x).
                    if (isLeopard && td.name.Contains("gunport")) continue;
                    string key = KeyFor(boat, td);
                    if (key == null) continue;
                    var p = new TrapdoorStatePacket { BoatName = boat.gameObject.name, Key = key, IsOpen = td.IsOpen(), IsGunportGroup = false };
                    Plugin.NetworkManager.SendReliable(target, PacketType.TrapdoorState, w => PacketSerializer.WriteTrapdoorState(w, p));
                    sent++;
                }

                if (isLeopard)
                {
                    foreach (var group in new[] { "lower", "upper", "quarter" })
                    {
                        var open = Compat.LeopardCompat.GetGunportGroupOpen(group);
                        if (open == null) continue;
                        var p = new TrapdoorStatePacket { BoatName = boat.gameObject.name, Key = group, IsOpen = open.Value, IsGunportGroup = true };
                        Plugin.NetworkManager.SendReliable(target, PacketType.TrapdoorState, w => PacketSerializer.WriteTrapdoorState(w, p));
                        sent++;
                    }
                }
            }
            // Hinged doors (0.39). Their state is not saved, so every machine loads them shut and only the
            // open ones need sending.
            foreach (var door in Resources.FindObjectsOfTypeAll<GPButtonHouseDoor>())
            {
                if (door == null || !door.gameObject.scene.IsValid() || !door.IsOpen() || door.saveable == null) continue;
                string key = HouseDoorKeyFor(door.saveable, door);
                if (key == null) continue;
                var p = new TrapdoorStatePacket { BoatName = door.saveable.gameObject.name, Key = key, IsOpen = true, IsGunportGroup = false };
                Plugin.NetworkManager.SendReliable(target, PacketType.TrapdoorState, w => PacketSerializer.WriteTrapdoorState(w, p));
                sent++;
            }

            Plugin.Log.LogInfo($"[TRAPDOOR] Join replay: sent {sent} trapdoor states to {target}");
        }
    }
}
