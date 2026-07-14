using SailwindCoop.Debug;
using SailwindCoop.Networking.Packets;
using Steamworks;
using UnityEngine;

namespace SailwindCoop.Sync
{
    /// <summary>
    /// (v0.2.32) HMS Leopard runtime sync: cutter deploy/recover (217), oar input (218), bell (219).
    /// Everything here hard no-ops when LeopardCompat.SyncEnabled is false. The cutter is a real
    /// second boat (root "BOAT CUTTER (212)(Clone)"): activating it must invalidate the boat-name
    /// cache (P2) and pin it into the host's always-stream set (P4) or an empty cutter drifts and is
    /// pruned on guests.
    /// </summary>
    public class LeopardSyncManager : MonoBehaviour
    {
        public static LeopardSyncManager Instance { get; private set; }

        private void Awake()
        {
            Instance = this;
        }

        // === Cutter (217) ===

        /// <summary>Guest -> host intent. Called by the guest-side controller prefixes.</summary>
        public void RequestCutter(bool deploy)
        {
            if (!Plugin.IsMultiplayer || Plugin.IsHost) return;
            // (review) never put a zeroed quaternion on the wire.
            var p = new CutterStatePacket { Active = deploy, IsRequest = true, Rotation = Quaternion.identity };
            Plugin.NetworkManager.SendReliable(Plugin.LobbyManager.HostSteamId, PacketType.CutterState,
                w => PacketSerializer.WriteCutterState(w, p));
            VerboseLogger.ControlSend($"CutterState REQUEST deploy={deploy}");
        }

        public void OnCutterState(CutterStatePacket packet, SteamId sender)
        {
            if (!Compat.LeopardCompat.SyncEnabled) return;

            if (packet.IsRequest)
            {
                if (!Plugin.IsHost) return; // requests are host-only business
                // (review) RECOVER SOFTLOCK GATE: the mod's own recover gate counts ITEMS aboard the
                // cutter, not PLAYERS (its distance check is commented out in Leopard v1.4.0). On a
                // guest, the local player is PARENTED into the boat hierarchy while aboard, so
                // applying SetActive(false) with someone standing on the cutter deactivates the
                // GameObject under their feet - camera, controller and all - an unrecoverable
                // softlock. Refuse a recover while ANY crew member (host included) is aboard;
                // still broadcast below so the requester converges on the unchanged state.
                if (!packet.Active && AnyPlayerAboardCutter())
                {
                    Plugin.Log.LogInfo("[LEOPARD] Cutter recover refused: a crew member is aboard the cutter");
                    Plugin.Notify("Cutter recover refused - someone is aboard it", 5f);
                    BroadcastCutterState();
                    return;
                }
                // Run the MOD'S OWN controller so its gates (velocity <= 1.5 m/s; items-left-aboard
                // child count) execute on authoritative host state. CutterController.OnActivate never
                // reads its GoPointer parameter (HMSLeopard CutterController.cs:22-56), so null is safe.
                HostRunCutterController(packet.Active);
                // The reflected OnActivate above already fired CutterAnyPostfix -> one broadcast; this
                // second, byte-identical broadcast is a deliberate convergence backstop for the case
                // where the reflected invoke no-ops (null MethodInfo/comp). Applies are idempotent.
                // Broadcast whatever ACTUALLY happened (a refused gate = state unchanged; guests
                // converge on the truth either way).
                BroadcastCutterState();
                return;
            }

            // Authoritative state from the host.
            if (Plugin.IsHost) return; // host originated it
            ApplyCutterState(packet);
        }

        /// <summary>Host: true when the local player or any remote crew member is on the cutter.</summary>
        internal static bool AnyPlayerAboardCutter()
        {
            var local = BoatUtility.GetCurrentBoat();
            if (local != null && local.gameObject.name == Compat.LeopardCompat.CutterRootName) return true;
            var rpm = Player.RemotePlayerManager.Instance;
            if (rpm != null)
            {
                foreach (var avatar in rpm.Avatars)
                {
                    if (avatar != null && avatar.CurrentBoatName == Compat.LeopardCompat.CutterRootName)
                        return true;
                }
            }
            return false;
        }

        /// <summary>Host: invoke the Leopard's own deploy/recover controller (gates included).</summary>
        private void HostRunCutterController(bool deploy)
        {
            var ship = Compat.LeopardCompat.LeopardShip;
            if (ship == null) return;
            if (deploy)
            {
                var t = ship.transform.Find("boat leopard/structure_container/Wooden Rowboat");
                var comp = t != null ? t.GetComponent(Compat.LeopardCompat.CutterControllerType) : null;
                if (comp == null)
                {
                    Plugin.Log.LogWarning("[LEOPARD] Cutter controller component not found (prefab path changed?); request dropped, state unchanged");
                    return;
                }
                // public override void OnActivate(GoPointer) - parameter unused by the mod.
                Compat.LeopardCompat.CutterControllerType
                    .GetMethod("OnActivate", new[] { typeof(GoPointer) })
                    ?.Invoke(comp, new object[] { null });
            }
            else
            {
                var t = ship.transform.Find("boat leopard/structure_container/rowboat rope");
                var comp = t != null ? t.GetComponent(Compat.LeopardCompat.CutterRopeControllerType) : null;
                if (comp == null)
                {
                    Plugin.Log.LogWarning("[LEOPARD] Cutter controller component not found (prefab path changed?); request dropped, state unchanged");
                    return;
                }
                // public override void OnActivate() - the no-arg overload.
                Compat.LeopardCompat.CutterRopeControllerType
                    .GetMethod("OnActivate", System.Type.EmptyTypes)
                    ?.Invoke(comp, null);
            }
        }

        /// <summary>Host: broadcast the cutter's current authoritative state to everyone.</summary>
        public void BroadcastCutterState()
        {
            if (!Plugin.IsHost || !Plugin.IsMultiplayer || !Compat.LeopardCompat.SyncEnabled) return;
            var p = CaptureCutterState();
            Plugin.NetworkManager.SendToAllReliable(PacketType.CutterState,
                w => PacketSerializer.WriteCutterState(w, p));
            VerboseLogger.ControlSend($"CutterState BROADCAST active={p.Active}, realPos={p.RealPosition}");
            SyncHostSideEffects(p.Active);
        }

        /// <summary>Host: targeted join replay (modData does not travel; without this the guest's
        /// phantom-save cutterActive silently diverges from the first frame).</summary>
        public void SendCutterStateTo(SteamId target)
        {
            if (!Plugin.IsHost || !Compat.LeopardCompat.SyncEnabled) return;
            var p = CaptureCutterState();
            Plugin.NetworkManager.SendReliable(target, PacketType.CutterState,
                w => PacketSerializer.WriteCutterState(w, p));
            // (review) A host who RELOADED a save with the cutter already deployed never went through
            // BroadcastCutterState, so the always-stream pin + cache invalidate never ran - an empty
            // deployed cutter would stream nothing and guests would prune it. The join is the first
            // moment streaming matters; SyncHostSideEffects is idempotent, run it here too.
            SyncHostSideEffects(p.Active);
        }

        private CutterStatePacket CaptureCutterState()
        {
            var cutter = Compat.LeopardCompat.CutterBoat;
            var offset = FloatingOriginManager.instance?.outCurrentOffset ?? Vector3.zero;
            return new CutterStatePacket
            {
                Active = cutter != null && cutter.activeSelf,
                RealPosition = cutter != null ? cutter.transform.position - offset : Vector3.zero,
                Rotation = cutter != null ? cutter.transform.rotation : Quaternion.identity,
                IsRequest = false
            };
        }

        /// <summary>Guest: apply the host's authoritative cutter state.</summary>
        public void ApplyCutterState(CutterStatePacket packet)
        {
            var cutter = Compat.LeopardCompat.CutterBoat;
            var ship = Compat.LeopardCompat.LeopardShip;
            if (cutter == null || ship == null) return;

            VerboseLogger.ControlApply($"CutterState active={packet.Active}, realPos={packet.RealPosition}");

            if (packet.Active)
            {
                cutter.SetActive(true);
                var offset = FloatingOriginManager.instance?.outCurrentOffset ?? Vector3.zero;
                cutter.transform.SetPositionAndRotation(packet.RealPosition + offset, packet.Rotation);
                // Same instant horizon refresh the mod does on deploy (CutterController.cs:46-49).
                var horizon = cutter.transform.Find("boat cutter")?.GetComponent<BoatHorizon>();
                if (horizon != null)
                    HarmonyLib.AccessTools.Field(typeof(BoatHorizon), "updateCooldown")?.SetValue(horizon, 0f);
            }
            else
            {
                // (review) Belt-and-braces for a race the host gate can't see (recover approved in the
                // same instant this guest boarded): never deactivate an ancestor of the local player.
                // Re-parent them out at their current world position first - they end up in the water
                // where the cutter was, which is the physically sensible outcome. This reuses the exact
                // reparent-to-"_shifting world" mechanism BoatStateApplicator.TeleportPlayer already uses
                // for cross-region joins (SetParent(shiftingWorld, worldPositionStays:true) on
                // Refs.charController and Refs.observerMirror) rather than inventing a new one.
                var localBoatRoot = GameState.currentBoat != null ? GameState.currentBoat.parent : null;
                if (localBoatRoot != null && localBoatRoot.gameObject.name == Compat.LeopardCompat.CutterRootName)
                {
                    var shiftingWorld = GameObject.Find("_shifting world")?.transform;
                    if (shiftingWorld != null && Refs.charController != null)
                    {
                        Refs.charController.transform.SetParent(shiftingWorld, true);
                        if (Refs.observerMirror != null)
                            Refs.observerMirror.transform.SetParent(shiftingWorld, true);
                        Plugin.Log.LogWarning("[LEOPARD] Local player re-parented out of the cutter before deactivation (recover raced a boarding)");
                    }
                    else
                    {
                        // Fallback: unresolvable rescue path - deactivate anyway, documenting the
                        // residual race (vanilla embark bookkeeping will right itself once the boat map
                        // refreshes).
                        Plugin.Log.LogWarning("[LEOPARD] Local player is aboard the cutter but could not be re-parented (_shifting world or charController unresolved); deactivating anyway (residual race)");
                    }
                }
                cutter.SetActive(false);
            }

            // Deck prop flip, exactly as the mod does on deploy/recover.
            var rowboatProp = ship.transform.Find("boat leopard/structure_container/Wooden Rowboat");
            var rowboatRope = ship.transform.Find("boat leopard/structure_container/rowboat rope");
            if (rowboatProp != null) rowboatProp.gameObject.SetActive(!packet.Active);
            if (rowboatRope != null) rowboatRope.gameObject.SetActive(packet.Active);

            Compat.LeopardCompat.SetCutterActive(packet.Active);
            SyncHostSideEffects(packet.Active);
        }

        /// <summary>Cache + streaming bookkeeping shared by host broadcast and guest apply.</summary>
        private void SyncHostSideEffects(bool active)
        {
            // (P2) The cutter just entered/left the playable world: rebuild the boat-name map.
            BoatUtility.ClearCaches();
            // (P4, host-only inside the registry) Pin the deployed cutter into the 10Hz stream.
            if (active) BoatSyncManager.RegisterAlwaysStream(Compat.LeopardCompat.CutterRootName);
            else BoatSyncManager.UnregisterAlwaysStream(Compat.LeopardCompat.CutterRootName);
        }

        // === Oars (218) ===

        private const float OarSendInterval = 0.1f;   // 10 Hz while rowing
        private const float OarFreshSeconds = 0.5f;   // received bits older than this are ignored
        private float _lastOarSend;
        private byte _lastSentBits;
        private Component _oarController;             // cached from the postfix (the paddles button)
        private Rigidbody _cutterRb;                   // cached host force-path rigidbody (Fix 4)
        private bool _oarsShownForRemote;               // last SetOars state we invoked for remote rowing
        private bool _wasGrabbed;                        // previous-frame grip state; the mod hides the oars on the GRIP-release edge

        // Received remote input, keyed by author. Additive application matches the unmodified mod
        // (it lets two players grab the same oars and both push).
        private readonly System.Collections.Generic.Dictionary<ulong, (byte bits, float at)> _remoteOars
            = new System.Collections.Generic.Dictionary<ulong, (byte, float)>();
        private readonly System.Collections.Generic.List<ulong> _oarPruneScratch = new System.Collections.Generic.List<ulong>();
        private float _oarAnimTime; // observer-side animation phase

        private const byte OarUp = 1, OarDown = 2, OarLeft = 4, OarRight = 8;

        /// <summary>Called from the oar ExtraLateUpdate postfix on EVERY machine, every frame.</summary>
        public void SampleAndSendOarInput(Component oarController, bool grabbed)
        {
            _oarController = oarController;

            // The mod's else-branch fires SetOars(false) when the GRIP is released (not the keys),
            // and our postfix runs right after it in the same LateUpdate - reset the shown flag on
            // exactly that edge so the next remote-rowing frame re-shows the oars.
            if (_wasGrabbed && !grabbed) _oarsShownForRemote = false;
            _wasGrabbed = grabbed;

            byte bits = 0;
            if (grabbed)
            {
                if (GameInput.GetKey(InputName.MoveUp)) bits |= OarUp;
                if (GameInput.GetKey(InputName.MoveDown)) bits |= OarDown;
                if (GameInput.GetKey(InputName.MoveLeft)) bits |= OarLeft;
                if (GameInput.GetKey(InputName.MoveRight)) bits |= OarRight;
            }

            // 10Hz while held, but a bit change sends immediately - a sub-interval tap or adding a
            // turn key must not be dropped/delayed by the 10Hz gate; one zero-bits packet on release
            // so remotes stop promptly.
            bool due = Time.unscaledTime - _lastOarSend >= OarSendInterval;
            if ((bits != 0 && (due || bits != _lastSentBits)) || (bits == 0 && _lastSentBits != 0))
            {
                _lastOarSend = Time.unscaledTime;
                _lastSentBits = bits;
                var p = new OarInputPacket { KeyBits = bits, AuthorId = SteamClient.SteamId.Value };
                Plugin.NetworkManager.SendToAllUnreliable(PacketType.OarInput,
                    w => PacketSerializer.WriteOarInput(w, p));
            }
        }

        public void OnOarInput(OarInputPacket packet, SteamId sender)
        {
            if (!Compat.LeopardCompat.SyncEnabled) return;
            if (packet.AuthorId == SteamClient.SteamId.Value) return; // our own relay echo

            if (Plugin.IsHost)
            {
                // Unreliable relay: matches the origin send (SampleAndSendOarInput uses
                // SendToAllUnreliable) - a dropped/late oar sample is superseded by the next 10Hz
                // tick, so reliable delivery would only add latency, not correctness.
                Plugin.NetworkManager.SendToAllExcept(sender, PacketType.OarInput,
                    w => PacketSerializer.WriteOarInput(w, packet), reliable: false);
            }
            _remoteOars[packet.AuthorId] = (packet.KeyBits, Time.unscaledTime);
        }

        private void Update()
        {
            if (!Plugin.IsMultiplayer || !Compat.LeopardCompat.SyncEnabled) return;

            // The postfix stops firing when the cutter is stowed - if that happens mid-keyhold the
            // release edge never runs and _lastSentBits would latch non-zero, permanently muting
            // remote oar animation on this machine. Clear it whenever the cutter is not live.
            var cutterLive = Compat.LeopardCompat.CutterBoat;
            if ((cutterLive == null || !cutterLive.activeInHierarchy) && _lastSentBits != 0)
                _lastSentBits = 0;

            // Prune stale rowers so _remoteOars can empty and the Count==0 early-out works again -
            // without this the per-frame animate path would run forever after the first oar packet.
            _oarPruneScratch.Clear();
            foreach (var kv in _remoteOars)
                if (Time.unscaledTime - kv.Value.at > OarFreshSeconds) _oarPruneScratch.Add(kv.Key);
            foreach (var stale in _oarPruneScratch) _remoteOars.Remove(stale);
            if (_remoteOars.Count == 0 && !_oarsShownForRemote) return;

            byte combined = 0;
            foreach (var kv in _remoteOars)
                if (Time.unscaledTime - kv.Value.at <= OarFreshSeconds) combined |= kv.Value.bits;

            // HOST: apply the same per-frame forces the mod applies locally (OarController.cs:53-118
            // uses ForceMode.Force in LateUpdate; Update matches that per-render-frame cadence closer
            // than FixedUpdate would). Additive per rower is a deliberate co-op choice (the brief
            // mandates it); the mod itself only ever applies one local player's input.
            if (Plugin.IsHost && combined != 0 && _oarController != null)
            {
                var cutter = Compat.LeopardCompat.CutterBoat;
                // Cache the cutter rigidbody (Fix 4): only refresh when the cached reference is
                // null/destroyed or points at a different GameObject than the current CutterBoat,
                // instead of a GetComponent every frame.
                if (cutter != null && (_cutterRb == null || _cutterRb.gameObject != cutter))
                    _cutterRb = cutter.GetComponent<Rigidbody>();
                var rb = cutter != null ? _cutterRb : null;
                if (rb != null && cutter.activeInHierarchy)
                {
                    float force = Compat.LeopardCompat.GetOarForceAmount(_oarController);
                    float turn = Compat.LeopardCompat.GetOarTurnForce(_oarController);
                    foreach (var kv in _remoteOars)
                    {
                        if (Time.unscaledTime - kv.Value.at > OarFreshSeconds) continue;
                        byte b = kv.Value.bits;
                        if ((b & OarUp) != 0) rb.AddForce(rb.transform.forward * force, ForceMode.Force);
                        if ((b & OarDown) != 0) rb.AddForce(-rb.transform.forward * force, ForceMode.Force);
                        if ((b & OarLeft) != 0) rb.AddTorque(Vector3.up * -force * turn, ForceMode.Force);
                        if ((b & OarRight) != 0) rb.AddTorque(Vector3.up * force * turn, ForceMode.Force);
                    }
                }
            }

            // EVERY non-rower machine: animate the oars from the bits (mirrors OarController's math;
            // rowing phase is cosmetic so an approximation is fine). The local rower's own vanilla
            // animation already owns the transforms while _lastSentBits != 0, so skip here.
            if (_lastSentBits == 0) AnimateRemoteOars(combined);
        }

        private void AnimateRemoteOars(byte bits)
        {
            if (_oarController == null) return;
            var left = Compat.LeopardCompat.GetOarLeft(_oarController);
            var right = Compat.LeopardCompat.GetOarRight(_oarController);
            if (left == null || right == null) return;

            bool rowing = bits != 0;
            // Reflected private SetOars(bool): swaps the static paddle mesh for the animated oars.
            // Invoke the mod's SetOars only on a state CHANGE: it opens with a Debug.Log, so a
            // per-frame invoke floods the playtest logs and allocates every frame.
            if (rowing != _oarsShownForRemote)
            {
                Compat.LeopardCompat.InvokeSetOars(_oarController, rowing);
                _oarsShownForRemote = rowing;
            }
            if (!rowing) return;

            // Same constants as OarController (timeIncrease=3, forwardAngle=30, upAngle=20).
            _oarAnimTime += Time.deltaTime * 3f * (((bits & OarDown) != 0 && (bits & OarUp) == 0) ? -1f : 1f);
            float zAngle = Mathf.Sin(_oarAnimTime) * 30f;
            float xAngle = -(Mathf.Sin(_oarAnimTime + 1.5f) * 20f);
            left.transform.localRotation = Quaternion.Euler(xAngle, 0f, zAngle);
            right.transform.localRotation = Quaternion.Euler(-xAngle, 0f, -zAngle);
        }

        // === Bell (219) ===

        public void OnBellRing(ulong authorId, SteamId sender)
        {
            if (!Compat.LeopardCompat.SyncEnabled) return;
            if (authorId == SteamClient.SteamId.Value) return; // relay echo of our own ring
            if (Plugin.IsHost)
                Plugin.NetworkManager.SendToAllExcept(sender, PacketType.BellRing, w => w.Write(authorId));
            Compat.LeopardCompat.PlayBell();
        }
    }
}
