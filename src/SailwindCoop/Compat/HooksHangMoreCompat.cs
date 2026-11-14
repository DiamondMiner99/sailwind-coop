using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using SailwindCoop.Debug;
using SailwindCoop.Networking.Packets;
using SailwindCoop.Patches;
using SailwindCoop.Sync;
using Steamworks;
using UnityEngine;

namespace SailwindCoop.Compat
{
    /// <summary>
    /// HooksHangMore (bryon82, com.raddude.hookshangmore) lets a fishing rod, broom, chip log, quadrant, knife,
    /// hammer, oar, anemometer or a boat's anchor hang on a lamp hook. It does not use HangableItem, so the ItemHung
    /// path never sees it: AttachableItemHolder keeps the item in a dictionary local to each machine and sets
    /// `attached` on the item's rigidbody. Everyone else only got an ordinary drop at the hand, so the rod fell to the
    /// deck on their screens.
    ///
    /// Built against 2.2.3. All reflected, because the mod may be absent, and a miss turns this off with a warning.
    /// Its buckets, kettles, pots, mugs and fish do use HangableItem and sync through ItemHung already.
    /// </summary>
    public static class HooksHangMoreCompat
    {
        public const string Guid = "com.raddude.hookshangmore";
        private const string AssemblyName = "HooksHangMore";

        private const float PendingPollInterval = 0.25f;
        private const float PendingTimeout = 15f;

        public static bool IsInstalled { get; private set; }
        public static bool SyncEnabled { get; private set; }

        private static Type _holderType;
        private static Type _attachableType;
        private static MethodInfo _attachItem;     // internal void AttachItem(PickupableItem)
        private static MethodInfo _detachItem;     // internal void DetachItem()
        private static PropertyInfo _attachedItem; // public PickupableItem AttachedItem
        private static PropertyInfo _holder;       // internal AttachableItemHolder Holder (on AttachableItem)
        private static PropertyInfo _attachedItems; // internal static Dictionary<PickupableItem, AttachableItemHolder>

        // While this machine applies a crewmate's change, HooksHangMore's own methods must not broadcast it back.
        private static bool _applying;

        // An attach is sent while the item is still in the hanger's hand, ahead of the drop, and it may name an item
        // this machine has not spawned yet. It waits here until the item is at rest.
        private class PendingAttach
        {
            public HookAttachmentPacket Packet;
            public float Time;
        }

        private static readonly Dictionary<string, PendingAttach> _pending = new Dictionary<string, PendingAttach>();
        private static readonly List<string> _pendingScratch = new List<string>();
        private static float _nextPendingPoll;

        public static void Init()
        {
            IsInstalled = false;
            SyncEnabled = false;
            try
            {
                if (!BepInEx.Bootstrap.Chainloader.PluginInfos.TryGetValue(Guid, out var info) || info == null)
                {
                    Plugin.Log.LogInfo("[HHMCompat] HooksHangMore not installed.");
                    return;
                }
                IsInstalled = true;
                string version = info.Metadata.Version.ToString();

                var asm = info.Instance != null ? info.Instance.GetType().Assembly : null;
                asm = asm ?? AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == AssemblyName);

                _holderType = asm?.GetType("HooksHangMore.AttachableItemHolder");
                _attachableType = asm?.GetType("HooksHangMore.AttachableItem");
                var pluginType = asm?.GetType("HooksHangMore.HHM_Plugin");

                _attachItem = _holderType != null ? AccessTools.Method(_holderType, "AttachItem", new[] { typeof(PickupableItem) }) : null;
                _detachItem = _holderType != null ? AccessTools.Method(_holderType, "DetachItem") : null;
                _attachedItem = _holderType != null ? AccessTools.Property(_holderType, "AttachedItem") : null;
                _holder = _attachableType != null ? AccessTools.Property(_attachableType, "Holder") : null;
                _attachedItems = pluginType != null ? AccessTools.Property(pluginType, "AttachedItems") : null;

                SyncEnabled = _attachItem != null && _detachItem != null && _attachedItem != null && _holder != null && _attachedItems != null;
                if (SyncEnabled)
                    Plugin.Log.LogInfo($"[HHMCompat] HooksHangMore v{version} detected; hook sync enabled.");
                else
                    Plugin.Log.LogWarning($"[HHMCompat] HooksHangMore v{version} detected, but its members did not resolve. " +
                        "Hook sync disabled, so items on its hooks will not show for crewmates. Check for a Sailwind Co-op update.");
            }
            catch (Exception e)
            {
                SyncEnabled = false;
                Plugin.Log.LogWarning("[HHMCompat] Detection threw. Hook sync disabled. " + e);
            }
        }

        public static void ApplyPatches(HarmonyLib.Harmony harmony)
        {
            if (!SyncEnabled) return;
            try
            {
                harmony.Patch(_attachItem, postfix: new HarmonyMethod(typeof(HooksHangMoreCompat), nameof(AttachPostfix)));
                harmony.Patch(_detachItem, prefix: new HarmonyMethod(typeof(HooksHangMoreCompat), nameof(DetachPrefix)));
            }
            catch (Exception e)
            {
                SyncEnabled = false;
                Plugin.Log.LogWarning("[HHMCompat] Patching threw. Hook sync disabled. " + e);
            }
        }

        public static void Reset()
        {
            _pending.Clear();
            _applying = false;
        }

        // === Sending ===

        // HooksHangMore's own trigger path re-attaches items while a save loads, on every machine from its own data,
        // and its OnDestroy paths detach what goes out of range. Only a hand starts or ends a real change: the click
        // path runs with the item held, and OnPickup runs after GoPointer.PickUpItem has set held.

        private static void AttachPostfix(Component __instance, PickupableItem item)
        {
            if (!Plugin.IsMultiplayer || _applying || item == null || item.held == null) return;
            Send(__instance, item, attached: true);
        }

        private static void DetachPrefix(Component __instance)
        {
            if (!Plugin.IsMultiplayer || _applying) return;
            var item = _attachedItem.GetValue(__instance, null) as PickupableItem;
            if (item == null) return;

            var hook = __instance.GetComponent<ShipItem>();
            if (item.held == null && (hook == null || hook.held == null)) return;

            Send(__instance, item, attached: false);
        }

        private static void Send(Component holder, PickupableItem item, bool attached)
        {
            if (!TryDescribe(holder, item, attached, out var packet)) return;
            VerboseLogger.ItemSend($"HookAttachment, kind={packet.Kind}, attached={attached}, hook={packet.HookInstanceId}, item={packet.ItemInstanceId}, boat={packet.BoatName}");
            Plugin.NetworkManager.SendToAllReliable(PacketType.HookAttachment, w => PacketSerializer.WriteHookAttachment(w, packet));
        }

        private static bool TryDescribe(Component holder, PickupableItem item, bool attached, out HookAttachmentPacket packet)
        {
            packet = new HookAttachmentPacket { Attached = attached, BoatName = "" };

            var hookPrefab = holder != null ? holder.GetComponent<SaveablePrefab>() : null;
            if (hookPrefab == null || hookPrefab.instanceId == 0) return false;
            packet.HookInstanceId = hookPrefab.instanceId;

            if (item is Anchor anchor)
            {
                var boat = ControlPatches.ResolveAnchorBoat(anchor);
                if (boat == null) return false;
                packet.Kind = HookAttachmentKind.Anchor;
                packet.BoatName = boat.gameObject.name;
                return true;
            }

            var itemPrefab = item.GetComponent<SaveablePrefab>();
            if (itemPrefab == null || itemPrefab.instanceId == 0) return false;
            packet.Kind = HookAttachmentKind.Item;
            packet.ItemInstanceId = itemPrefab.instanceId;
            return true;
        }

        /// <summary>Host: the save data for a hook's anchor is not in the join snapshot, so tell the joiner everything.</summary>
        public static void ResyncTo(SteamId peer)
        {
            if (!Plugin.IsHost || !SyncEnabled) return;

            var clear = new HookAttachmentPacket { Kind = HookAttachmentKind.ClearAll, BoatName = "" };
            Plugin.NetworkManager.SendReliable(peer, PacketType.HookAttachment, w => PacketSerializer.WriteHookAttachment(w, clear));

            int sent = 0;
            foreach (DictionaryEntry entry in AttachedDictionary())
            {
                if (!TryDescribe(entry.Value as Component, entry.Key as PickupableItem, true, out var packet)) continue;
                Plugin.NetworkManager.SendReliable(peer, PacketType.HookAttachment, w => PacketSerializer.WriteHookAttachment(w, packet));
                sent++;
            }
            VerboseLogger.ItemSend($"Replayed {sent} hook attachments to {peer}");
        }

        // === Receiving ===

        public static void OnRemoteAttachment(HookAttachmentPacket packet, SteamId sender)
        {
            if (Plugin.IsHost)
                Plugin.NetworkManager.SendToAllExcept(sender, PacketType.HookAttachment, w => PacketSerializer.WriteHookAttachment(w, packet));

            if (!SyncEnabled) return;

            VerboseLogger.ItemRecv($"HookAttachment, kind={packet.Kind}, attached={packet.Attached}, hook={packet.HookInstanceId}, item={packet.ItemInstanceId}, boat={packet.BoatName}");

            if (packet.Kind == HookAttachmentKind.ClearAll)
            {
                _pending.Clear();
                DetachEverything();
                return;
            }

            string key = PendingKey(packet);
            if (!packet.Attached)
            {
                _pending.Remove(key);
                var item = Resolve(packet);
                if (item != null) DetachLocally(item);
                return;
            }

            if (!TryApply(packet))
                _pending[key] = new PendingAttach { Packet = packet, Time = Time.unscaledTime };
        }

        public static void Tick()
        {
            if (_pending.Count == 0 || Time.unscaledTime < _nextPendingPoll) return;
            _nextPendingPoll = Time.unscaledTime + PendingPollInterval;

            _pendingScratch.Clear();
            foreach (var kvp in _pending)
            {
                if (Time.unscaledTime - kvp.Value.Time > PendingTimeout)
                {
                    Plugin.Log.LogWarning($"[HHMCompat] Gave up hanging {kvp.Key} on hook {kvp.Value.Packet.HookInstanceId}");
                    _pendingScratch.Add(kvp.Key);
                }
                else if (TryApply(kvp.Value.Packet))
                {
                    _pendingScratch.Add(kvp.Key);
                }
            }
            foreach (var key in _pendingScratch) _pending.Remove(key);
            _pendingScratch.Clear();
        }

        private static bool TryApply(HookAttachmentPacket packet)
        {
            var item = Resolve(packet);
            if (item == null) return false;

            // Still on its way out of a crewmate's hands: the drop that follows the attach would undo this.
            if (item is Anchor anchor)
            {
                if (AnchorPoseSync.IsRemotelyCarried(anchor)) return false;
            }
            else if (ItemSyncManager.Instance == null || ItemSyncManager.Instance.IsItemHeld(packet.ItemInstanceId))
            {
                return false;
            }
            if (item.held != null) return false;

            var hook = ItemSyncManager.FindItemByInstanceId(packet.HookInstanceId);
            var holder = hook != null ? hook.GetComponent(_holderType) : null;
            var attachable = item.GetComponent(_attachableType);
            if (holder == null || attachable == null) return false;

            var dictionary = AttachedDictionary();
            if (ReferenceEquals(dictionary[item], holder)) return true;

            _applying = true;
            try
            {
                // AttachItem adds to a dictionary and throws on a second add, and a hook holds one thing.
                DetachLocally(item);
                var occupant = _attachedItem.GetValue(holder, null) as PickupableItem;
                if (occupant != null) DetachLocally(occupant);

                _attachItem.Invoke(holder, new object[] { item });
                _holder.SetValue(attachable, holder, null);
                VerboseLogger.ItemApply($"Hung {item.name} on hook {packet.HookInstanceId}");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[HHMCompat] Could not hang {item.name} on hook {packet.HookInstanceId}: {e.Message}");
            }
            finally
            {
                _applying = false;
            }
            return true;
        }

        private static void DetachLocally(PickupableItem item)
        {
            var holder = AttachedDictionary()[item];
            if (holder == null) return;

            bool wasApplying = _applying;
            _applying = true;
            try { _detachItem.Invoke(holder, null); }
            catch (Exception e) { Plugin.Log.LogWarning($"[HHMCompat] Could not take {item.name} off its hook: {e.Message}"); }
            finally { _applying = wasApplying; }
        }

        private static void DetachEverything()
        {
            var items = AttachedDictionary().Keys.Cast<PickupableItem>().ToList();
            foreach (var item in items)
                if (item != null) DetachLocally(item);
        }

        private static PickupableItem Resolve(HookAttachmentPacket packet)
        {
            if (packet.Kind == HookAttachmentKind.Anchor)
            {
                var boat = BoatUtility.FindBoatByName(packet.BoatName);
                return boat != null ? BoatUtility.GetAnchor(boat) : null;
            }
            return ItemSyncManager.FindItemByInstanceId(packet.ItemInstanceId);
        }

        private static IDictionary AttachedDictionary()
        {
            return (IDictionary)_attachedItems.GetValue(null, null);
        }

        private static string PendingKey(HookAttachmentPacket packet)
        {
            return packet.Kind == HookAttachmentKind.Anchor ? "anchor of " + packet.BoatName : "item " + packet.ItemInstanceId;
        }
    }
}
