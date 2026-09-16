using System.Collections.Generic;
using SailwindPlayerModel;
using Steamworks;
using UnityEngine;
using SailwindCoop.Networking.Packets;

namespace SailwindCoop.Sync
{
    /// <summary>
    /// Shows a crewmate sitting (on a chair, a rail, a crate), lying in a bed, or knocked down, so their body is posed
    /// that way for everyone else. Sitting, falling and the poses live in the Sailwind Player Model mod; this only
    /// carries where.
    ///
    /// A fall is simulated only on the machine of the player who fell. Everyone else is sent the result a few times a
    /// second while the body goes over, and the body mod eases between them.
    ///
    /// FRAME. Everything is sent relative to the FEET point the position packet already carries, in the same
    /// frame: the boat model's local axes aboard, world axes on land. The receiver adds it to the avatar's own
    /// feet in the boat model the avatar is placed through, so a seat rides with the boat and lags exactly as
    /// much as the avatar does.
    ///
    /// Sent reliably when the state changes or the seat moves, and re-sent every few seconds while it lasts so a
    /// crewmate who joins late sees it. Purely visual on the receiving end.
    /// </summary>
    public static class RestSync
    {
        private const byte StateNone = 0, StateSeated = 1, StateLying = 2, StateDowned = 3, StateGettingUp = 4;
        private const float RefreshSeconds = 4f;
        // A falling body moves every frame; this many updates a second is plenty to ease between.
        private const float DownedSendSeconds = 0.1f;
        // A ragdoll part turned this far since the last update is worth sending.
        private const float BoneSendDegrees = 5f;
        // A fallen body farther than this from its own feet is a bad frame, not a fall.
        private const float MaxDownedOffset = 6f;
        // Smoke, flames and steam from sitting on a lit stove, each sent in tenths: enough for the effect to rise and
        // fade, few enough that easing it does not send a packet every frame.
        private const int ScorchSteps = 10;

        private sealed class Rest
        {
            public byte State;
            public byte Smoke, Fire, Steam;   // 0 to ScorchSteps, in any state: the pants keep burning after standing up
            public SeatPose Pose;       // seated: how the body sits (see SeatPose)
            public Vector3 A, B, C;     // seated: hips, forward, (floor, 0, 0); lying: head, along, up; downed: pelvis
            public FallReaction Reaction;   // downed: what the arms are doing
            public readonly Quaternion[] Bones = new Quaternion[SyntyBody.RagdollParts];   // downed: each ragdoll part, in the frame
            public float Floor;         // downed: about where the floor is under the body, relative to the feet
            public byte Progress;       // getting up: 0 to 255

            public void CopyTo(Rest to)
            {
                to.State = State; to.Smoke = Smoke; to.Fire = Fire; to.Steam = Steam; to.Pose = Pose;
                to.A = A; to.B = B; to.C = C; to.Reaction = Reaction; to.Floor = Floor; to.Progress = Progress;
                for (int i = 0; i < Bones.Length; i++) to.Bones[i] = Bones[i];
            }
        }

        private static readonly Dictionary<ulong, Rest> _remote = new Dictionary<ulong, Rest>();
        private static readonly Rest _sent = new Rest();
        private static readonly Quaternion[] _bones = new Quaternion[SyntyBody.RagdollParts];
        private static float _nextRefresh;
        private static float _nextDownedSend;

        public static void Clear()
        {
            _remote.Clear();
            _sent.State = StateNone;
            _nextRefresh = 0f;
        }

        public static void OnPeerLeft(ulong peerId) { _remote.Remove(peerId); }

        /// <summary>Per frame, from Plugin.LateUpdate: report our own rest on change, and pose crewmates.</summary>
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
                Plugin.Log.LogWarning("[Rest] " + e.Message);
            }
        }

        // ---- send ---------------------------------------------------------------------------------------

        private static void SendLocal()
        {
            var now = new Rest();
            Transform frame;
            Vector3 feet;
            if (Refs.observerMirror != null && LocalFeet(out feet, out frame))
            {
                Vector3 a, b, c; SeatPose pose; float floorY;
                FallReaction reaction; float progress;
                if (PlayerModel.TryGetLocalRagdoll(out a, _bones, out floorY, out reaction))
                {
                    now.State = StateDowned;
                    now.Reaction = reaction;
                    now.A = ToFrame(frame, a - feet);
                    for (int i = 0; i < _bones.Length; i++) now.Bones[i] = frame != null ? Quaternion.Inverse(frame.rotation) * _bones[i] : _bones[i];
                    now.Floor = floorY - feet.y;
                }
                else if (PlayerModel.TryGetLocalGettingUp(out progress))
                {
                    now.State = StateGettingUp;
                    now.Progress = (byte)Mathf.RoundToInt(Mathf.Clamp01(progress) * 255f);
                }
                else if (PlayerModel.TryGetLocalSeat(out a, out b, out pose, out floorY))
                {
                    now.State = StateSeated;
                    now.Pose = pose;
                    now.A = ToFrame(frame, a - feet);
                    now.B = DirToFrame(frame, b);
                    now.C = new Vector3(floorY - feet.y, 0f, 0f);
                }
                else if (PlayerModel.TryGetLocalLying(out a, out b, out c))
                {
                    now.State = StateLying;
                    now.A = ToFrame(frame, a - feet);
                    now.B = DirToFrame(frame, b);
                    now.C = DirToFrame(frame, c);
                }
            }
            float smoke, fire, steam;
            PlayerModel.GetLocalScorch(out smoke, out fire, out steam);
            now.Smoke = (byte)Mathf.RoundToInt(Mathf.Clamp01(smoke) * ScorchSteps);
            now.Fire = (byte)Mathf.RoundToInt(Mathf.Clamp01(fire) * ScorchSteps);
            now.Steam = (byte)Mathf.RoundToInt(Mathf.Clamp01(steam) * ScorchSteps);

            bool switched = now.State != _sent.State || now.Pose != _sent.Pose || now.Reaction != _sent.Reaction
                || now.Smoke != _sent.Smoke || now.Fire != _sent.Fire || now.Steam != _sent.Steam;
            bool moved = false;
            if (now.State == StateDowned)
            {
                moved = (now.A - _sent.A).sqrMagnitude > 0.0009f;
                for (int i = 0; i < now.Bones.Length && !moved; i++) moved = Quaternion.Angle(now.Bones[i], _sent.Bones[i]) > BoneSendDegrees;
            }
            else if (now.State == StateGettingUp)
            {
                moved = now.Progress != _sent.Progress && (now.Progress == 255 || now.Progress - _sent.Progress >= 10);
            }
            else if (now.State != StateNone)
            {
                moved = (now.A - _sent.A).sqrMagnitude > 0.0009f || Vector3.Angle(now.B, _sent.B) > 4f;
            }
            bool quick = now.State == StateDowned || now.State == StateGettingUp;
            if (moved && !switched && quick && Time.unscaledTime < _nextDownedSend) moved = false;
            if (!switched && !moved && (now.State == StateNone || Time.unscaledTime < _nextRefresh)) return;

            now.CopyTo(_sent);
            _nextRefresh = Time.unscaledTime + RefreshSeconds;
            _nextDownedSend = Time.unscaledTime + DownedSendSeconds;
            ulong me = SteamClient.SteamId.Value;
            var snapshot = new Rest();
            now.CopyTo(snapshot);
            Plugin.NetworkManager.SendToAllReliable(PacketType.RestState, w => Write(w, me, snapshot));
        }

        /// <summary>
        /// The feet point the position packet sends, and its frame: the boat model aboard (null on land). Seated or
        /// in bed the physics controller stays where the player stood, so the feet come from the observer, which
        /// is what the position packet does in that case too.
        /// </summary>
        private static bool LocalFeet(out Vector3 feet, out Transform frame)
        {
            feet = Vector3.zero; frame = null;
            var cc = Refs.charController;
            if (cc == null) return false;
            float gap = VanillaPlayer.ControllerFeetGap();
            Vector3 body = Refs.observerMirror.transform.position;
            var parent = cc.transform.parent;
            if (parent != null && parent.name != "_shifting world" && GameState.currentBoat != null)
            {
                frame = GameState.currentBoat.transform;
                feet = frame.TransformPoint(frame.InverseTransformPoint(body) - Vector3.up * gap);
            }
            else
            {
                feet = body - Vector3.up * gap;
            }
            return true;
        }

        private static Vector3 ToFrame(Transform frame, Vector3 worldVector)
        {
            return frame != null ? frame.InverseTransformVector(worldVector) : worldVector;
        }

        private static Vector3 DirToFrame(Transform frame, Vector3 worldDir)
        {
            return frame != null ? frame.InverseTransformDirection(worldDir) : worldDir;
        }

        private static void Write(System.IO.BinaryWriter w, ulong actor, Rest r)
        {
            w.Write(actor);
            w.Write(r.State);
            w.Write(r.Smoke);
            w.Write(r.Fire);
            w.Write(r.Steam);
            if (r.State == StateNone) return;
            if (r.State == StateDowned)
            {
                w.Write((byte)r.Reaction);
                WriteV(w, r.A);
                for (int i = 0; i < r.Bones.Length; i++) WriteQ(w, r.Bones[i]);
                w.Write(r.Floor);
                return;
            }
            if (r.State == StateGettingUp)
            {
                w.Write(r.Progress);
                return;
            }
            w.Write((byte)r.Pose);
            WriteV(w, r.A); WriteV(w, r.B); WriteV(w, r.C);
        }

        private static void WriteV(System.IO.BinaryWriter w, Vector3 v) { w.Write(v.x); w.Write(v.y); w.Write(v.z); }
        private static Vector3 ReadV(System.IO.BinaryReader r) { return new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()); }

        /// <summary>A rotation in six bytes: x, y and z of the unit quaternion with w kept positive, which w is rebuilt from.</summary>
        private static void WriteQ(System.IO.BinaryWriter w, Quaternion q)
        {
            float n = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
            if (n < 1e-6f) q = Quaternion.identity;
            else { q.x /= n; q.y /= n; q.z /= n; q.w /= n; }
            if (q.w < 0f) { q.x = -q.x; q.y = -q.y; q.z = -q.z; }
            w.Write((short)Mathf.RoundToInt(Mathf.Clamp(q.x, -1f, 1f) * 32767f));
            w.Write((short)Mathf.RoundToInt(Mathf.Clamp(q.y, -1f, 1f) * 32767f));
            w.Write((short)Mathf.RoundToInt(Mathf.Clamp(q.z, -1f, 1f) * 32767f));
        }

        private static Quaternion ReadQ(System.IO.BinaryReader r)
        {
            float x = r.ReadInt16() / 32767f, y = r.ReadInt16() / 32767f, z = r.ReadInt16() / 32767f;
            float ww = 1f - x * x - y * y - z * z;
            var q = new Quaternion(x, y, z, ww > 0f ? Mathf.Sqrt(ww) : 0f);
            float n = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
            return n > 1e-6f ? new Quaternion(q.x / n, q.y / n, q.z / n, q.w / n) : Quaternion.identity;
        }

        // ---- receive ------------------------------------------------------------------------------------

        public static void OnReceived(SteamId sender, System.IO.BinaryReader reader)
        {
            try
            {
                ulong actor = reader.ReadUInt64();
                var rest = new Rest { State = reader.ReadByte() };
                rest.Smoke = (byte)Mathf.Min(reader.ReadByte(), ScorchSteps);
                rest.Fire = (byte)Mathf.Min(reader.ReadByte(), ScorchSteps);
                rest.Steam = (byte)Mathf.Min(reader.ReadByte(), ScorchSteps);
                if (rest.State == StateDowned)
                {
                    rest.Reaction = (FallReaction)Mathf.Min(reader.ReadByte(), (int)FallReaction.Flail);
                    rest.A = ReadV(reader);
                    for (int i = 0; i < rest.Bones.Length; i++) rest.Bones[i] = ReadQ(reader);
                    rest.Floor = reader.ReadSingle();
                }
                else if (rest.State == StateGettingUp)
                {
                    rest.Progress = reader.ReadByte();
                }
                else if (rest.State != StateNone)
                {
                    byte pose = reader.ReadByte();
                    rest.Pose = pose <= (byte)SeatPose.FloorKneesHugged ? (SeatPose)pose : SeatPose.FeetDown;
                    rest.A = ReadV(reader); rest.B = ReadV(reader); rest.C = ReadV(reader);
                }

                if (actor == SteamClient.SteamId.Value) return;   // relay echo of our own

                if (Plugin.IsHost)
                {
                    // The actor id is sender-supplied; without this a guest could sit any crewmate down.
                    if (actor != sender.Value) return;
                    Plugin.NetworkManager.SendToAllExcept(sender, PacketType.RestState, w => Write(w, actor, rest));
                }

                if (rest.State == StateNone && rest.Smoke == 0 && rest.Fire == 0 && rest.Steam == 0) _remote.Remove(actor);
                else _remote[actor] = rest;
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("[Rest] Could not read a rest packet: " + e.Message);
            }
        }

        private static void ApplyRemote()
        {
            if (_remote.Count == 0) return;
            foreach (var kvp in _remote)
            {
                var avatar = Plugin.RemotePlayerManager?.GetAvatar(kvp.Key);
                if (avatar == null) continue;
                var r = kvp.Value;
                if (r.Smoke > 0 || r.Fire > 0 || r.Steam > 0)
                    avatar.SetScorch(r.Smoke / (float)ScorchSteps, r.Fire / (float)ScorchSteps, r.Steam / (float)ScorchSteps);
                Vector3 feet;
                Transform frame;
                if (!avatar.TryGetFeet(out feet, out frame)) continue;
                if (r.State == StateSeated)
                {
                    avatar.SetSeat(feet + FromFrame(frame, r.A), DirFromFrame(frame, r.B), r.Pose, feet.y + r.C.x);
                }
                else if (r.State == StateLying)
                {
                    avatar.SetLying(feet + FromFrame(frame, r.A), DirFromFrame(frame, r.B), DirFromFrame(frame, r.C));
                }
                else if (r.State == StateDowned)
                {
                    if (r.A.sqrMagnitude > MaxDownedOffset * MaxDownedOffset) continue;
                    for (int i = 0; i < _bones.Length; i++) _bones[i] = frame != null ? frame.rotation * r.Bones[i] : r.Bones[i];
                    avatar.SetRagdoll(feet + FromFrame(frame, r.A), _bones, feet.y + r.Floor, r.Reaction);
                }
                else if (r.State == StateGettingUp)
                {
                    avatar.SetGettingUp(r.Progress / 255f);
                }
            }
        }

        private static Vector3 FromFrame(Transform frame, Vector3 v) { return frame != null ? frame.TransformVector(v) : v; }
        private static Vector3 DirFromFrame(Transform frame, Vector3 d) { return frame != null ? frame.TransformDirection(d) : d; }
    }
}
