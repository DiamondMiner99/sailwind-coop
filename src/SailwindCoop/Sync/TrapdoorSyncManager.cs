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
            Plugin.Log.LogInfo($"[TRAPDOOR] Join replay: sent {sent} trapdoor states to {target}");
        }
    }
}
