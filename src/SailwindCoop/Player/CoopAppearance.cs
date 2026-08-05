using System;
using System.Collections.Generic;
using System.Text;
using PsychoticLab;
using Steamworks;
using UnityEngine;

namespace SailwindCoop.Player
{
    /// <summary>
    /// (v0.2.39) Per-player avatar appearance, built on the Synty POLYGON modular character system that
    /// Sailwind already ships.
    ///
    /// WHY THIS IS EVEN POSSIBLE. Every shopkeeper NPC in the game is a "Modular NPC" carrying a LIVE
    /// PsychoticLab.CharacterCustomizer, with the ENTIRE part library still present as disabled child
    /// objects - the editor-time Apply() bake that would have destroyed the unused parts was never run on
    /// any shipped NPC. So the avatar clone the mod already makes has every head, hair, torso and leg
    /// variant sitting inside it, switched off. Roughly 720 skinned meshes across 41 group nodes.
    ///
    /// WHY EVERYONE CURRENTLY LOOKS IDENTICAL, and how that becomes the mechanism. RemotePlayerManager's
    /// StripForAvatar does NOT remove CharacterCustomizer, and both avatar paths call SetActive(true) on the
    /// clone. That fires CharacterCustomizer.Start(), which runs BuildLists() + ClearItems() + UpdateModel()
    /// and rebuilds the model from the SOURCE shopkeeper's serialized indices - overwriting anything set
    /// beforehand and stamping every player with the face of whichever shopkeeper happened to load first.
    /// The selection fields are all public ints, so the fix is not to fight that pass but to feed it: write
    /// the indices BEFORE activation and vanilla's own Start() applies the whole outfit, materials included.
    ///
    /// THEREFORE: Apply() MUST be called while the clone is still inactive. Calling it afterwards does
    /// nothing visible until something else re-runs UpdateModel.
    ///
    /// SAFETY. Indices arriving from the network are never trusted to index anything. They are clamped at
    /// apply time against the LIVE child count of the group on THIS machine, which is the only count that
    /// can actually be indexed here - deliberately not against a compiled-in table, because the body
    /// template is cloned from whichever shopkeeper loaded and a hardcoded count could drift past it. An
    /// unknown or out-of-range value degrades to that group's variant 0, so the worst case is a plain
    /// sailor rather than an invisible or T-posing one.
    /// </summary>
    public struct CoopAppearance
    {
        /// <summary>One selectable group. Getter/setter are direct field access on the vanilla component.</summary>
        public sealed class Slot
        {
            public readonly string Key;          // stable wire/config token - NEVER renamed once shipped
            public readonly string Label;        // shown in the UI
            public readonly Func<CharacterCustomizer, int> Get;
            public readonly Action<CharacterCustomizer, int> Set;
            // Live variant list on THIS machine's clone. The customizer keeps male/female/allGender part
            // lists as serialized List<GameObject>; counting them is how an index gets validated.
            public readonly Func<CharacterCustomizer, List<GameObject>> List;
            /// <summary>Whether "wear nothing" is a legal choice. TRUE for accessories (a bald sailor with
            /// no hat is a person); FALSE for anatomy, where it deletes the body part and leaves a floating
            /// head or an invisible character.</summary>
            public readonly bool AllowsNone;
            public Slot(string key, string label, Func<CharacterCustomizer, int> get,
                Action<CharacterCustomizer, int> set, Func<CharacterCustomizer, List<GameObject>> list,
                bool allowsNone)
            { Key = key; Label = label; Get = get; Set = set; List = list; AllowsNone = allowsNone; }
        }

        // Tier 1 slot set: the groups that visibly change WHO the character is, in the order a player would
        // reasonably work down a character screen. The remaining vanilla fields (per-limb arms/hands, the
        // attachment points) are deliberately left on whatever the source shopkeeper had - they are mostly
        // armour hardpoints, and exposing 44 sliders would bury the ones that matter.
        //
        // Male and female variants of the same concept share ONE slot: which underlying field is written
        // depends on the Gender slot, so a player switching gender keeps a coherent outfit instead of
        // carrying indices into a list of a different length.
        public static readonly Slot[] Slots = new[]
        {
            new Slot("gender", "Gender",
                c => c.isFemale ? 2 : 1,
                (c, v) => c.isFemale = v == 2,
                c => null, allowsNone: false),               // 2 fixed options, no list to count
            new Slot("head", "Head",
                c => c.isFemale ? c.femaleHeadAllElements : c.maleHeadAllElements,
                (c, v) => { if (c.isFemale) c.femaleHeadAllElements = v; else c.maleHeadAllElements = v; },
                c => Group(c).headAllElements, allowsNone: false),   // anatomy: none = invisible player
            new Slot("hair", "Hair",
                c => c.allGenderAll_Hair,
                (c, v) => c.allGenderAll_Hair = v,
                c => c.allGender != null ? c.allGender.all_Hair : null, allowsNone: true),   // bald is a look
            new Slot("eyebrow", "Eyebrows",
                c => c.isFemale ? c.femaleEyebrow : c.maleEyebrow,
                (c, v) => { if (c.isFemale) c.femaleEyebrow = v; else c.maleEyebrow = v; },
                c => Group(c).eyebrow, allowsNone: false),
            new Slot("facialhair", "Facial hair",
                c => c.maleFacialHair,
                (c, v) => c.maleFacialHair = v,
                c => c.male != null ? c.male.facialHair : null, allowsNone: false),   // male-only in the vanilla rig
            // Hats live in headCoverings_Base_Hair, NOT all_Head_Attachment. The latter holds two raw
            // import folders ("Hair", "Helmet") with no meshes of their own - every shopkeeper in the game
            // ships with it set to -1, and pointing the UI at it produced a thicket of eleven overlapping
            // helmet crests plus an NRE inside vanilla's ActivateItem. headCoverings_Base_Hair is the
            // variant set meant to be worn WITH hair, which is the sane default for a crew of sailors.
            new Slot("headgear", "Hat",
                c => c.allGenderHeadCoverings_Base_Hair,
                (c, v) => c.allGenderHeadCoverings_Base_Hair = v,
                c => c.allGender != null ? c.allGender.headCoverings_Base_Hair : null, allowsNone: true),   // bare-headed is a look
            new Slot("torso", "Torso",
                c => c.isFemale ? c.femaleTorso : c.maleTorso,
                (c, v) => { if (c.isFemale) c.femaleTorso = v; else c.maleTorso = v; },
                c => Group(c).torso, allowsNone: false),     // anatomy: none left a floating head
            new Slot("hips", "Hips",
                c => c.isFemale ? c.femaleHips : c.maleHips,
                (c, v) => { if (c.isFemale) c.femaleHips = v; else c.maleHips = v; },
                c => Group(c).hips, allowsNone: false),
            new Slot("legs", "Legs",
                c => c.isFemale ? c.femaleLeg_Right : c.maleLeg_Right,
                (c, v) => {
                    // Both legs move together - a mismatched pair reads as a bug, never as a choice.
                    if (c.isFemale) { c.femaleLeg_Right = v; c.femaleLeg_Left = v; }
                    else { c.maleLeg_Right = v; c.maleLeg_Left = v; }
                },
                c => Group(c).leg_Right, allowsNone: false),
        };

        /// <summary>The male or female part-list bundle, whichever this customizer is currently set to.
        /// Never null - an empty stand-in keeps the slot table free of null checks, and a slot whose list
        /// is empty is simply skipped (count 0) rather than crashing the build.</summary>
        private static CharacterObjectGroups Group(CharacterCustomizer c)
        {
            var g = c.isFemale ? c.female : c.male;
            return g ?? _emptyGroups;
        }
        private static readonly CharacterObjectGroups _emptyGroups = new CharacterObjectGroups();

        public static int SlotCount { get { return Slots.Length; } }

        /// <summary>Per-slot chosen index. Null/short arrays are treated as all-zero (the default sailor).</summary>
        public byte[] Values;

        public static CoopAppearance Default()
        {
            return new CoopAppearance { Values = new byte[Slots.Length] };
        }

        public byte this[int i]
        {
            get { return (Values != null && i >= 0 && i < Values.Length) ? Values[i] : (byte)0; }
            set
            {
                if (Values == null || Values.Length < Slots.Length) Resize();
                if (i >= 0 && i < Values.Length) Values[i] = value;
            }
        }

        private void Resize()
        {
            var next = new byte[Slots.Length];
            if (Values != null) Array.Copy(Values, next, Math.Min(Values.Length, next.Length));
            Values = next;
        }

        /// <summary>
        /// How many variants this machine actually has for a slot. Counted from the LIVE group children,
        /// because that is the only list an index can be applied against here. 0 means "not countable" and
        /// callers should treat the slot as unavailable rather than guessing a range.
        /// </summary>
        public static int VariantCount(CharacterCustomizer c, int slotIndex)
        {
            if (c == null || slotIndex < 0 || slotIndex >= Slots.Length) return 0;
            if (Slots[slotIndex].Key == "gender") return 2;
            try
            {
                var list = Slots[slotIndex].List(c);
                if (list == null) return 0;
                // Count only entries that are actually WEARABLE. Some groups contain raw import folders
                // with no renderer of their own; vanilla dereferences that renderer unguarded, and even
                // with our guard in place a folder is not a thing a player can meaningfully choose. Only
                // entries carrying a mesh are offered, and the index space is theirs alone.
                int n = 0;
                for (int i = 0; i < list.Count; i++)
                    if (IsWearable(list[i])) n++;
                return n;
            }
            catch { return 0; }
        }

        /// <summary>
        /// Force every group the UI does not expose into a DETERMINISTIC state, so an appearance string
        /// means the same thing on every machine.
        ///
        /// Without this, unexposed groups keep whatever the locally-cloned shopkeeper wore - and each
        /// machine clones whichever shopkeeper happened to load first - so two players would see the same
        /// crewmate wearing different gloves, pauldrons and sleeves. That silently defeats the point of
        /// putting appearance on the wire.
        ///
        /// The split matters: pure ATTACHMENTS (pauldrons, chest/back kit, knee pads, elf ears) go to -1,
        /// "wear nothing", which is the state every shipped NPC uses. LIMBS do not - setting an arm or a
        /// hand to -1 deletes it and leaves a character with stumps - so those follow the TORSO choice,
        /// which is also how Synty rigs keep sleeve and glove styling continuous with the jacket.
        /// </summary>
        private void PinUnexposedGroups(CharacterCustomizer c)
        {
            var g = Group(c);

            // Accessories: nothing.
            c.allGenderAll_Head_Attachment = -1;          // the folder group - never valid to select
            c.allGenderHeadCoverings_No_FacialHair = -1;  // alternates for the hat slot we DO expose
            c.allGenderHeadCoverings_No_Hair = -1;
            c.allGenderChest_Attachment = -1;
            c.allGenderBack_Attachment = -1;
            c.allGenderShoulder_Attachment_Right = -1;
            c.allGenderShoulder_Attachment_Left = -1;
            c.allGenderElbow_Attachment_Right = -1;
            c.allGenderElbow_Attachment_Left = -1;
            c.allGenderHips_Attachment = -1;
            c.allGenderKnee_Attachement_Right = -1;
            c.allGenderKnee_Attachement_Left = -1;
            c.allGenderElf_Ear = -1;

            // The alternate head variant: we drive headAllElements, so this one must stand down or two
            // heads are enabled at once.
            if (c.isFemale) c.femaleHeadNoElements = -1; else c.maleHeadNoElements = -1;

            // Limbs: match the torso so sleeves and gloves stay stylistically continuous, and stay equal
            // across machines. Each is wrapped into its OWN list length - the groups are not all the same
            // size, so reusing the torso number raw would go out of range.
            int torso = c.isFemale ? c.femaleTorso : c.maleTorso;
            if (torso < 0) torso = 0;
            SetLimb(c, g.arm_Upper_Right, torso, v => { if (c.isFemale) c.femaleArm_Upper_Right = v; else c.maleArm_Upper_Right = v; });
            SetLimb(c, g.arm_Upper_Left,  torso, v => { if (c.isFemale) c.femaleArm_Upper_Left  = v; else c.maleArm_Upper_Left  = v; });
            SetLimb(c, g.arm_Lower_Right, torso, v => { if (c.isFemale) c.femaleArm_Lower_Right = v; else c.maleArm_Lower_Right = v; });
            SetLimb(c, g.arm_Lower_Left,  torso, v => { if (c.isFemale) c.femaleArm_Lower_Left  = v; else c.maleArm_Lower_Left  = v; });
            SetLimb(c, g.hand_Right,      torso, v => { if (c.isFemale) c.femaleHand_Right      = v; else c.maleHand_Right      = v; });
            SetLimb(c, g.hand_Left,       torso, v => { if (c.isFemale) c.femaleHand_Left       = v; else c.maleHand_Left       = v; });
        }

        private static void SetLimb(CharacterCustomizer c, List<GameObject> list, int preferred, Action<int> set)
        {
            if (list == null || list.Count == 0) { set(-1); return; }
            // Never -1 for a limb: that would remove the arm or hand entirely.
            set(Mathf.Abs(preferred) % list.Count);
        }

        /// <summary>
        /// Fold a value into the legal range for a slot, wrapping at both ends. THE one place that decides
        /// what a slot's values mean, so the arrows, Randomize, the on-open tidy and Apply cannot disagree.
        ///
        /// 0 means "wear nothing" and exists only where AllowsNone. Making it universal was a real bug with
        /// two faces: picking None on Torso left a floating head and on Head made the player vanish
        /// entirely, and Gender - which has exactly two options - ended up cycling through THREE values, so
        /// the phantom None rendered as Male and going Female -> Male took two clicks.
        /// </summary>
        public static byte NormalizeValue(int slotIndex, int count, int value)
        {
            if (slotIndex < 0 || slotIndex >= Slots.Length || count <= 0) return 0;
            int lo = Slots[slotIndex].AllowsNone ? 0 : 1;   // 1-based when "nothing" is not on offer
            int span = count + 1 - lo;
            if (span <= 0) return (byte)lo;
            int v = (value - lo) % span;
            if (v < 0) v += span;
            return (byte)(v + lo);
        }

        private static bool IsWearable(GameObject go)
        {
            if (go == null) return false;
            return go.GetComponent<SkinnedMeshRenderer>() != null;
        }

        /// <summary>
        /// Translate a UI/wire value into the index vanilla wants. Value 0 means NOTHING - vanilla spells
        /// that -1, which a byte cannot carry - and 1..n select the nth WEARABLE entry, skipping any
        /// non-mesh folders so our indices never drift from what the player was shown.
        /// </summary>
        private static int ToVanillaIndex(CharacterCustomizer c, int slotIndex, int value)
        {
            if (value <= 0) return -1;
            var list = Slots[slotIndex].List(c);
            if (list == null) return -1;
            int wanted = value;                    // 1-based among wearables
            for (int i = 0; i < list.Count; i++)
            {
                if (!IsWearable(list[i])) continue;
                if (--wanted == 0) return i;
            }
            return -1;
        }

        /// <summary>
        /// Write these indices onto a body clone. MUST run while the clone is INACTIVE - see the class doc:
        /// vanilla's Start() is what actually rebuilds the mesh, and it reads these fields. Never throws.
        /// </summary>
        public void Apply(GameObject bodyClone)
        {
            if (bodyClone == null) return;
            try
            {
                var c = bodyClone.GetComponentInChildren<CharacterCustomizer>(true);
                if (c == null) return; // not a modular NPC (a mod-added or baked body) - leave it alone

                // Gender first: every other slot's target field and variant count depends on it, so writing
                // it late would clamp against the wrong list. But do NOT switch to female unless this rig
                // actually HAS female parts - gender is the one slot with no list of its own, and flipping
                // it redirects vanilla into female.* lists that an all-male NPC rig may leave empty,
                // producing a bodiless character rather than a different one.
                bool wantFemale = this[0] == 2;
                if (wantFemale && (c.female == null || c.female.torso == null || c.female.torso.Count == 0))
                    wantFemale = false;
                Slots[0].Set(c, wantFemale ? 2 : 1);

                for (int i = 1; i < Slots.Length; i++)
                {
                    int count = VariantCount(c, i);
                    // Nothing wearable in this group. Only write -1 where vanilla GUARDS it: UpdateModel
                    // indexes the BODY groups unguarded (male.eyebrow[maleEyebrow] and friends), so -1 on
                    // one of those throws mid-build and every part after it never activates. Leaving the
                    // source value alone is strictly safer than writing one that aborts the model.
                    if (count <= 0)
                    {
                        if (Slots[i].AllowsNone) Slots[i].Set(c, -1);
                        continue;
                    }

                    // Value space is 0 = nothing, 1..count = the nth wearable entry. WRAP rather than
                    // clamp-to-zero: the deterministic per-SteamId look draws from a wide range and most
                    // groups hold only a handful of variants, so clamping collapsed the majority of slots
                    // to one value and made crews look near-identical - exactly what that look exists to
                    // prevent. Wrapping keeps the variation and is equally safe.
                    int v = NormalizeValue(i, count, this[i]);
                    Slots[i].Set(c, ToVanillaIndex(c, i, v));
                }

                // Pin every group we do NOT expose to "nothing". Without this they keep whatever the local
                // machine's cloned shopkeeper happened to be wearing - so the same appearance string would
                // render differently on every client, which defeats the point of syncing it at all.
                PinUnexposedGroups(c);
            }
            catch (Exception e)
            {
                // An appearance is cosmetic; never let it be the reason a body fails to build.
                Plugin.Log.LogWarning("[Appearance] Could not apply appearance: " + e.Message);
            }
        }

        /// <summary>
        /// Restyle a body that is ALREADY ACTIVE, in place, with no destroy-and-rebuild.
        ///
        /// Vanilla's Start() does BuildLists() + ClearItems() + UpdateModel(); those are private, but the
        /// last two are all that a re-dress needs once the lists exist, and Harmony's Traverse reaches them
        /// (the same mechanism this codebase already uses for StartMenu's private fields).
        ///
        /// WHY THIS MATTERS RATHER THAN REBUILDING. The obvious implementation - destroy the clone and let
        /// the lazy builder make a new one - is wrong in three separate ways that all bite at once. The
        /// rebuild is throttled to ~1.5s, so a player clicking through variants gets a body that is absent
        /// more often than present. The retry gate is scaled Time.time, which is FROZEN while the pause
        /// menu holds timeScale at 0, so in a paused menu the rebuild may never happen at all. And each
        /// rebuild re-runs the leg-IK bind capture, which is documented as unsafe to re-run because it
        /// re-reads a foot rotation the IK has already written. Restyling in place avoids all three.
        /// Never throws: a cosmetic change must not disturb a session.
        /// </summary>
        public void ApplyLive(CharacterCustomizer c)
        {
            if (c == null) return;
            try
            {
                Apply(c.gameObject);
                var t = HarmonyLib.Traverse.Create(c);
                t.Method("ClearItems").GetValue();
                t.Method("UpdateModel").GetValue();
            }
            catch (Exception e)
            {
                // UNWRAP. Traverse invokes through reflection, so a throw inside vanilla arrives as
                // TargetInvocationException whose Message is the useless "Exception has been thrown by the
                // target of an invocation." That string appeared 40+ times in a live log - once per arrow
                // click, a 100% failure rate - while hiding the actual NullReferenceException inside
                // ActivateItem that was the real fault. Never let that happen twice.
                var inner = e is System.Reflection.TargetInvocationException && e.InnerException != null
                    ? e.InnerException : e;
                Plugin.Log.LogWarning("[Appearance] Live restyle failed: " + inner);
            }
        }

        // ---- persistence / wire ------------------------------------------------------------------------
        // Format: "key=value;key=value". KEY-ADDRESSED on purpose rather than positional, so adding a slot
        // in a later version cannot silently reinterpret an existing player's saved choices as a different
        // body part. Unknown keys are ignored; missing keys keep their default.

        public string Serialize()
        {
            var sb = new StringBuilder();
            for (int i = 0; i < Slots.Length; i++)
            {
                if (sb.Length > 0) sb.Append(';');
                sb.Append(Slots[i].Key).Append('=').Append(this[i]);
            }
            return sb.ToString();
        }

        public static CoopAppearance Deserialize(string s)
        {
            var a = Default();
            if (string.IsNullOrEmpty(s)) return a;
            try
            {
                var index = new Dictionary<string, int>(StringComparer.Ordinal);
                for (int i = 0; i < Slots.Length; i++) index[Slots[i].Key] = i;

                foreach (var pair in s.Split(';'))
                {
                    if (string.IsNullOrEmpty(pair)) continue;
                    int eq = pair.IndexOf('=');
                    if (eq <= 0 || eq >= pair.Length - 1) continue;
                    var key = pair.Substring(0, eq).Trim();
                    int slot;
                    if (!index.TryGetValue(key, out slot)) continue; // slot from a newer build: ignore
                    int v;
                    if (!int.TryParse(pair.Substring(eq + 1).Trim(), out v)) continue;
                    a[slot] = (byte)Mathf.Clamp(v, 0, 255);
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("[Appearance] Could not parse an appearance string, using the default: " + e.Message);
                return Default();
            }
            return a;
        }

        /// <summary>
        /// A stable, arbitrary-but-varied look derived from a SteamId, used when a player has never opened
        /// the character screen. Better than everyone defaulting to slot 0 and the crew looking like
        /// identical triplets, and deterministic so the same person is recognisably themselves each session.
        /// Deliberately does NOT randomise gender.
        /// </summary>
        public static CoopAppearance DeterministicFor(ulong steamId)
        {
            var a = Default();
            if (steamId == 0UL) return a;
            // Cheap decorrelation: a different odd multiplier per slot, so neighbouring ids do not produce
            // near-identical characters the way a single hash reused across slots would.
            for (int i = 1; i < Slots.Length; i++)
            {
                ulong h = steamId * (0x9E3779B97F4A7C15UL + ((ulong)i * 2654435761UL));
                h ^= h >> 29;
                a[i] = (byte)(h % 251); // clamped down to the live count at Apply time
            }
            return a;
        }
    }

    /// <summary>
    /// (v0.2.39) Who looks like what, keyed by SteamId. The single source of truth for appearance on this
    /// machine: the network layer writes into it, the avatar builders read from it.
    ///
    /// It is deliberately a REGISTRY rather than a field on the avatar, because the two events - "we learned
    /// what this player looks like" and "we managed to build a body for them" - arrive in either order and
    /// neither is guaranteed. A body cannot be built at all until a shopkeeper has loaded to clone, which
    /// out at sea may be never; meanwhile the appearance packet arrives on join. Keeping the data here and
    /// having the (lazily retrying) body builder read it means no re-send is ever needed and no ordering
    /// has to be enforced.
    ///
    /// An unknown player is NOT an error: they resolve to a deterministic look derived from their SteamId,
    /// so a peer on an older build, or one whose packet has not arrived yet, still shows up as a plausible
    /// individual rather than a clone of everyone else.
    /// </summary>
    public static class AppearanceRegistry
    {
        private static readonly Dictionary<ulong, CoopAppearance> _byPlayer = new Dictionary<ulong, CoopAppearance>();

        public static void Set(ulong steamId, CoopAppearance appearance)
        {
            if (steamId == 0UL) return;
            _byPlayer[steamId] = appearance;
        }

        public static bool Has(ulong steamId)
        {
            return steamId != 0UL && _byPlayer.ContainsKey(steamId);
        }

        /// <summary>Never fails: falls back to the deterministic per-id look.</summary>
        public static CoopAppearance For(ulong steamId)
        {
            CoopAppearance a;
            if (steamId != 0UL && _byPlayer.TryGetValue(steamId, out a)) return a;
            return CoopAppearance.DeterministicFor(steamId);
        }

        /// <summary>Drop everything on session teardown - appearances are per-session knowledge, and a
        /// stale entry would make a rejoining player wear the look of whoever last held that id slot.</summary>
        public static void Clear() { _byPlayer.Clear(); }

        /// <summary>Every id we hold an EXPLICIT appearance for (not the deterministic fallback). Used by
        /// the host to replay the roster to a joiner.</summary>
        public static List<ulong> KnownPlayers()
        {
            return new List<ulong>(_byPlayer.Keys);
        }
    }

    /// <summary>
    /// (v0.2.39) Wire layer for <see cref="CoopAppearance"/> - packet 220.
    ///
    /// SHAPE: authorId (u64), slotCount (byte), slotCount variant bytes. Author-keyed rather than
    /// sender-keyed because the host RELAYS other players' appearances, so "who sent this" and "who this
    /// describes" are genuinely different questions on a guest's receive path.
    ///
    /// TRUST: none. Two independent guards, because either alone would be a hole.
    ///   - The host refuses to relay a packet whose author is not its sender. Without this any guest could
    ///     restyle any other player, since the author id is just a number in the body. This is a real gap
    ///     in the existing author-keyed packets, not a hypothetical one.
    ///   - Every receiver clamps indices against its OWN live part lists at apply time, so even an
    ///     appearance that got through cannot index out of range - the worst it can do is look silly.
    /// The host relays the SANITIZED struct it parsed, never the bytes it received, so a malformed body
    /// cannot be laundered through the host to the rest of the crew.
    /// </summary>
    public static class AppearanceSync
    {
        /// <summary>Tell the crew what we look like. Safe to call when not in a session (no-op).</summary>
        public static void BroadcastLocal()
        {
            try
            {
                if (!Plugin.IsMultiplayer || Plugin.NetworkManager == null) return;
                ulong me = SteamClient.SteamId.Value;
                var mine = Plugin.LocalAppearance;
                AppearanceRegistry.Set(me, mine);   // be authoritative about ourselves locally too
                Plugin.NetworkManager.SendToAllReliable(Networking.Packets.PacketType.PlayerAppearance,
                    w => Write(w, me, mine));
            }
            catch (Exception e) { Plugin.Log.LogWarning("[Appearance] Broadcast failed: " + e.Message); }
        }

        /// <summary>
        /// HOST ONLY: replay every known crew appearance to a joining guest, so a late joiner does not see
        /// a boat full of fallback faces. Mirrors the chart-session replay in the join-state sequence.
        /// </summary>
        public static void SendRosterTo(SteamId target)
        {
            try
            {
                if (!Plugin.IsHost || Plugin.NetworkManager == null) return;
                int sent = 0;
                foreach (var id in AppearanceRegistry.KnownPlayers())
                {
                    if (id == target.Value) continue;   // they know their own
                    var a = AppearanceRegistry.For(id);
                    Plugin.NetworkManager.SendReliable(target, Networking.Packets.PacketType.PlayerAppearance,
                        w => Write(w, id, a));
                    sent++;
                }
                Plugin.Log.LogInfo($"[Appearance] Replayed {sent} crew appearance(s) to the joiner.");
            }
            catch (Exception e) { Plugin.Log.LogWarning("[Appearance] Roster replay failed: " + e.Message); }
        }

        private static void Write(System.IO.BinaryWriter w, ulong authorId, CoopAppearance a)
        {
            w.Write(authorId);
            byte n = (byte)CoopAppearance.SlotCount;
            w.Write(n);
            for (int i = 0; i < n; i++) w.Write(a[i]);
        }

        /// <summary>Receive path. Never throws; a bad appearance packet must not disturb a session.</summary>
        public static void OnReceived(SteamId sender, System.IO.BinaryReader reader)
        {
            try
            {
                ulong authorId = reader.ReadUInt64();
                int n = reader.ReadByte();
                var a = CoopAppearance.Default();
                for (int i = 0; i < n; i++)
                {
                    byte v = reader.ReadByte();
                    // Slots beyond what THIS build knows about are read (to keep the stream aligned) and
                    // discarded. A peer on a newer build with more slots is not an error.
                    if (i < CoopAppearance.SlotCount) a[i] = v;
                }

                if (authorId == SteamClient.SteamId.Value) return;   // relay echo of our own

                if (Plugin.IsHost)
                {
                    // AUTHOR SPOOFING GUARD. The author id is attacker-controlled data; without this a
                    // guest could dress any other crew member however it liked, and the host would
                    // faithfully relay it to everyone.
                    if (authorId != sender.Value)
                    {
                        Plugin.Log.LogWarning($"[Appearance] Dropped an appearance from {sender.Value} " +
                            $"claiming to be {authorId} (author does not match sender).");
                        return;
                    }
                    // Relay what we PARSED, not what we received.
                    Plugin.NetworkManager.SendToAllExcept(sender, Networking.Packets.PacketType.PlayerAppearance,
                        w => Write(w, authorId, a));
                }
                else
                {
                    // GUEST SIDE. The host's author==sender check protects the relay, but a guest must not
                    // simply trust whatever reaches it either. In this star topology a guest peers ONLY
                    // with the host, so an appearance arriving from anyone else did not come through the
                    // host's validation and has no business being applied.
                    if (sender.Value != Plugin.LobbyManager.HostSteamId.Value)
                    {
                        Plugin.Log.LogWarning($"[Appearance] Ignored an appearance from non-host peer {sender.Value}.");
                        return;
                    }
                }

                AppearanceRegistry.Set(authorId, a);
                // The look is baked when a body clone is activated, so an already-built avatar has to be
                // rebuilt to show a change. Cheap and rare: this fires on join and when someone edits.
                Plugin.RemotePlayerManager?.RefreshAppearance(authorId);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("[Appearance] Could not read an appearance packet: " + e.Message);
            }
        }
    }
}
