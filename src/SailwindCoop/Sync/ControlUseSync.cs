using System.Collections.Generic;
using SailwindPlayerModel;
using Steamworks;
using UnityEngine;
using SailwindCoop.Debug;
using SailwindCoop.Networking.Packets;

namespace SailwindCoop.Sync
{
    /// <summary>
    /// Shows which crewmate is operating the ship's wheel, a rope winch, a bilge pump or a sail pusher, so
    /// their body is posed on it for everyone else. The pose itself lives in the Sailwind Player Model mod;
    /// this only tells each machine who is using what.
    ///
    /// WHY IT NEEDS ITS OWN PACKET. The rope, helm and push packets carry the control's STATE (length, angle,
    /// force) and none of them name the player doing it, so a receiver cannot tell whose hands belong on the
    /// wheel. Carried items and mooring ropes do not need this: their packets already name the holder.
    ///
    /// ADDRESSING. A control is its boat's root name plus its index among that boat's controls of the same
    /// type, in GetComponentsInChildren order - the same scheme the sail pusher and rope packets use. The
    /// order is the prefab hierarchy, so it matches on every machine running the same boats.
    ///
    /// Sent reliably on every change, and re-sent while held so a crewmate who joins mid-steer sees it. It is
    /// purely visual on the receiving end: nothing here touches the control itself.
    /// </summary>
    public static class ControlUseSync
    {
        private const byte TypeNone = 0, TypeWheel = 1, TypeWinch = 2, TypePump = 3, TypePusher = 4;
        private const float RefreshSeconds = 4f;

        private struct InUse
        {
            public InteractionKind Kind;
            public Transform Control;
        }

        // Crewmate -> the control they are operating (remote players only).
        private static readonly Dictionary<ulong, InUse> _inUse = new Dictionary<ulong, InUse>();

        // What we last told the crew about our own hands.
        private static byte _sentType;
        private static string _sentBoat = "";
        private static int _sentIndex = -1;
        private static float _nextRefresh;

        public static void Clear()
        {
            _inUse.Clear();
            _sentType = TypeNone;
            _sentBoat = "";
            _sentIndex = -1;
            _nextRefresh = 0f;
        }

        public static void OnPeerLeft(ulong peerId)
        {
            _inUse.Remove(peerId);
        }

        /// <summary>Per frame, from Plugin.LateUpdate: report our own control on change, and pose crewmates on theirs.</summary>
        public static void LateUpdate()
        {
            if (!Plugin.IsMultiplayer || Plugin.NetworkManager == null) return;
            try
            {
                SendLocal();
                ApplyRemote();
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("[ControlUse] " + e.Message);
            }
        }

        // ---- send ---------------------------------------------------------------------------------------

        private static void SendLocal()
        {
            Transform control;
            var kind = PlayerModel.GetLocalControl(out control);

            byte type = TypeNone;
            string boatName = "";
            int index = -1;
            if (kind != InteractionKind.None && control != null && !Identify(control, out type, out boatName, out index))
                type = TypeNone;

            bool changed = type != _sentType || index != _sentIndex || boatName != _sentBoat;
            if (!changed && (type == TypeNone || Time.unscaledTime < _nextRefresh)) return;

            _sentType = type;
            _sentBoat = boatName;
            _sentIndex = index;
            _nextRefresh = Time.unscaledTime + RefreshSeconds;

            ulong me = SteamClient.SteamId.Value;
            Plugin.NetworkManager.SendToAllReliable(PacketType.ControlInUse, w => Write(w, me, type, boatName, index));
            if (changed)
                VerboseLogger.ControlLocal($"[ControlUse] now using {(type == TypeNone ? "nothing" : TypeName(type) + " " + index + " on " + boatName)}");
        }

        private static void Write(System.IO.BinaryWriter w, ulong actor, byte type, string boatName, int index)
        {
            w.Write(actor);
            w.Write(type);
            if (type == TypeNone) return;
            w.Write(boatName ?? "");
            w.Write((ushort)Mathf.Clamp(index, 0, ushort.MaxValue));
        }

        // ---- receive ------------------------------------------------------------------------------------

        public static void OnReceived(SteamId sender, System.IO.BinaryReader reader)
        {
            try
            {
                ulong actor = reader.ReadUInt64();
                byte type = reader.ReadByte();
                string boatName = "";
                int index = -1;
                if (type != TypeNone)
                {
                    boatName = reader.ReadString();
                    index = reader.ReadUInt16();
                }

                if (actor == SteamClient.SteamId.Value) return;   // relay echo of our own

                if (Plugin.IsHost)
                {
                    // The actor id is sender-supplied. Without this a guest could put any crewmate's hands on
                    // any control, and the host would relay it to everyone.
                    if (actor != sender.Value) return;
                    Plugin.NetworkManager.SendToAllExcept(sender, PacketType.ControlInUse, w => Write(w, actor, type, boatName, index));
                }

                if (type == TypeNone) { _inUse.Remove(actor); return; }

                var control = Resolve(type, boatName, index);
                if (control == null) { _inUse.Remove(actor); return; }   // boat not streamed in here

                _inUse[actor] = new InUse { Kind = KindFor(type), Control = control };
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("[ControlUse] Could not read a control-use packet: " + e.Message);
            }
        }

        private static void ApplyRemote()
        {
            if (_inUse.Count == 0) return;
            List<ulong> stale = null;
            foreach (var kvp in _inUse)
            {
                if (kvp.Value.Control == null)
                {
                    (stale ?? (stale = new List<ulong>())).Add(kvp.Key);
                    continue;
                }
                var avatar = Plugin.RemotePlayerManager?.GetAvatar(kvp.Key);
                if (avatar != null) avatar.SetInteraction(kvp.Value.Kind, kvp.Value.Control);
            }
            if (stale != null) foreach (var id in stale) _inUse.Remove(id);
        }

        // ---- identity -----------------------------------------------------------------------------------

        private static bool Identify(Transform control, out byte type, out string boatName, out int index)
        {
            type = TypeNone; boatName = ""; index = -1;
            var boat = control.GetComponentInParent<SaveableObject>();
            if (boat == null) return false;

            if (control.GetComponent<GPButtonSteeringWheel>() != null) { type = TypeWheel; index = IndexOf<GPButtonSteeringWheel>(boat, control); }
            else if (control.GetComponent<GPButtonRopeWinch>() != null) { type = TypeWinch; index = IndexOf<GPButtonRopeWinch>(boat, control); }
            else if (control.GetComponent<BilgePump>() != null) { type = TypePump; index = IndexOf<BilgePump>(boat, control); }
            else if (control.GetComponent<GPButtonSailPusher>() != null) { type = TypePusher; index = IndexOf<GPButtonSailPusher>(boat, control); }

            if (type == TypeNone || index < 0) { type = TypeNone; return false; }
            boatName = boat.gameObject.name;
            return true;
        }

        private static int IndexOf<T>(SaveableObject boat, Transform control) where T : Component
        {
            var all = boat.GetComponentsInChildren<T>(true);
            for (int i = 0; i < all.Length; i++)
                if (all[i].transform == control) return i;
            return -1;
        }

        private static Transform Resolve(byte type, string boatName, int index)
        {
            if (string.IsNullOrEmpty(boatName) || index < 0) return null;
            var boat = BoatUtility.FindBoatByName(boatName);
            if (boat == null) return null;
            switch (type)
            {
                case TypeWheel: return At(boat.GetComponentsInChildren<GPButtonSteeringWheel>(true), index);
                case TypeWinch: return At(boat.GetComponentsInChildren<GPButtonRopeWinch>(true), index);
                case TypePump: return At(boat.GetComponentsInChildren<BilgePump>(true), index);
                case TypePusher: return At(boat.GetComponentsInChildren<GPButtonSailPusher>(true), index);
                default: return null;
            }
        }

        private static Transform At<T>(T[] all, int index) where T : Component
        {
            return all != null && index < all.Length && all[index] != null ? all[index].transform : null;
        }

        private static InteractionKind KindFor(byte type)
        {
            switch (type)
            {
                case TypeWheel: return InteractionKind.Helm;
                case TypeWinch:
                case TypePump: return InteractionKind.Crank;
                case TypePusher: return InteractionKind.Push;
                default: return InteractionKind.None;
            }
        }

        private static string TypeName(byte type)
        {
            switch (type)
            {
                case TypeWheel: return "wheel";
                case TypeWinch: return "winch";
                case TypePump: return "pump";
                case TypePusher: return "sail pusher";
                default: return "nothing";
            }
        }
    }
}
