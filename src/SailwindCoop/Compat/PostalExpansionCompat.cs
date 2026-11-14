using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using SailwindCoop.Networking.Packets;
using UnityEngine;

namespace SailwindCoop.Compat
{
    /// <summary>
    /// Postal Expansion (DogEggz, com.DogEggz.postalexpansion) adds a Mail tab with express, urgent and golden mail,
    /// registered and anonymous letters delivered to a place within delivery hours, its own reward rules and two new
    /// cargo items. It keeps what kind of mail a mission is in tables beside the Mission object, and builds its boards
    /// fresh every time one is shown. Co-op's boards came from the host's vanilla lists and rebuilt each mission from its
    /// plain fields, so guests never saw the Mail tab's offers, saw the host's special mail as plain express mail, could
    /// not deliver a letter at its place, and could hand a letter in at the office for its full reward.
    ///
    /// Now the host builds every tab with Postal Expansion's own lists, the kind of mail and the letter's id travel with
    /// each mission and are attached on the guest through Postal Expansion's own marking code, an accept names the offer
    /// by what it is rather than by its place on the page, and only the host completes a letter, at its place, in its
    /// hours. Built against 1.1.3; every member is reflected and a miss turns this off with a warning.
    /// </summary>
    public static class PostalExpansionCompat
    {
        public const string Guid = "com.DogEggz.postalexpansion";

        public const byte KindNone = 0, KindUrgent = 1, KindGolden = 2, KindRegistered = 3, KindAnonymous = 4;

        public static bool IsInstalled { get; private set; }
        public static bool SyncEnabled { get; private set; }
        private static string _version = "";

        private static Type _tabType, _kindType;
        private static MethodInfo _available, _currentTab, _boardPage, _updatePageCount, _updatePageText;
        private static MethodInfo _isLetter, _isUrgent, _markUrgent, _isGolden, _markGolden;
        private static MethodInfo _regTryGet, _anonTryGet, _letterTryGet, _letterDelivered, _isOpen;
        private static MethodInfo _getKind, _getDisplayName;
        private static object _regSet, _anonSet;
        private static MethodInfo _regAttach, _anonAttach;
        private static PropertyInfo _regAll, _anonAll;
        private static readonly List<string> _missing = new List<string>();

        public static void Init()
        {
            IsInstalled = false;
            SyncEnabled = false;
            _missing.Clear();
            try
            {
                if (!BepInEx.Bootstrap.Chainloader.PluginInfos.TryGetValue(Guid, out var info) || info == null)
                {
                    Plugin.Log.LogInfo("[PostalCompat] Postal Expansion not installed.");
                    return;
                }
                IsInstalled = true;
                _version = info.Metadata.Version.ToString();
                var asm = info.Instance != null ? info.Instance.GetType().Assembly
                    : AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "PostalExpansion");

                Type ui = T(asm, "PostalExpansion.PostalMissionUi"), mail = T(asm, "PostalExpansion.PostalMail");
                Type urgent = T(asm, "PostalExpansion.UrgentExpressMail"), golden = T(asm, "PostalExpansion.GoldenDeliveryMissions");
                Type reg = T(asm, "PostalExpansion.RegisteredLetterMissions"), anon = T(asm, "PostalExpansion.AnonymousLetterMissions");
                Type regDef = T(asm, "PostalExpansion.RegisteredLetterMissionDefinition");
                Type anonDef = T(asm, "PostalExpansion.AnonymousLetterMissionDefinition");
                Type letterDef = T(asm, "PostalExpansion.LetterMissionDefinition");
                Type letters = T(asm, "PostalExpansion.LetterMissions"), hours = T(asm, "PostalExpansion.LetterDeliveryHours");
                Type window = T(asm, "PostalExpansion.LetterDeliveryWindow");
                Type regRegistry = T(asm, "PostalExpansion.RegisteredLetterMissionRegistry");
                Type anonRegistry = T(asm, "PostalExpansion.AnonymousLetterMissionRegistry");
                Type display = T(asm, "PostalExpansion.SpecialMailDisplay");
                _tabType = T(asm, "PostalExpansion.PostalMissionTab");
                _kindType = T(asm, "PostalExpansion.SpecialMailKind");

                if (_missing.Count == 0)
                {
                    _available = M(ui, "GetAvailableMissions", typeof(Port), _tabType);
                    _currentTab = M(ui, "GetCurrentTab", typeof(MissionListUI));
                    _boardPage = M(ui, "GetMissions", typeof(MissionListUI), typeof(int));
                    _updatePageCount = M(ui, "UpdatePageCount", typeof(MissionListUI));
                    _updatePageText = M(ui, "UpdatePageCountText", typeof(MissionListUI));
                    _isLetter = M(mail, "IsRegisteredLetter", typeof(SaveablePrefab));
                    _isUrgent = M(urgent, "IsUrgent", typeof(Mission));
                    _markUrgent = M(urgent, "MarkRuntime", typeof(Mission));
                    _isGolden = M(golden, "IsGolden", typeof(Mission));
                    _markGolden = M(golden, "MarkRuntime", typeof(Mission));
                    _regTryGet = M(reg, "TryGetDefinition", typeof(Mission), regDef.MakeByRefType());
                    _anonTryGet = M(anon, "TryGetDefinition", typeof(Mission), anonDef.MakeByRefType());
                    _letterTryGet = M(letters, "TryGetDefinition", typeof(Mission), letterDef.MakeByRefType());
                    _letterDelivered = M(letters, "MissionDelivered", typeof(Mission));
                    _isOpen = M(hours, "IsOpen", window);
                    _getKind = M(display, "GetKind", typeof(Mission));
                    _getDisplayName = M(display, "GetDisplayName", _kindType);
                    _regSet = AccessTools.Field(reg, "Missions")?.GetValue(null);
                    _anonSet = AccessTools.Field(anon, "Missions")?.GetValue(null);
                    _regAttach = _regSet != null ? AccessTools.Method(_regSet.GetType(), "Attach", new[] { typeof(Mission), regDef }) : null;
                    _anonAttach = _anonSet != null ? AccessTools.Method(_anonSet.GetType(), "Attach", new[] { typeof(Mission), anonDef }) : null;
                    if (_regAttach == null) _missing.Add("RegisteredLetterMissions.Missions.Attach");
                    if (_anonAttach == null) _missing.Add("AnonymousLetterMissions.Missions.Attach");
                    _regAll = AccessTools.Property(regRegistry, "All");
                    _anonAll = AccessTools.Property(anonRegistry, "All");
                    if (_regAll == null || _anonAll == null) _missing.Add("LetterMissionRegistry.All");
                }

                SyncEnabled = _missing.Count == 0;
                if (SyncEnabled)
                    Plugin.Log.LogInfo($"[PostalCompat] Postal Expansion v{_version} detected; the host's mail boards and letters are shared.");
                else
                    Plugin.Log.LogWarning($"[PostalCompat] Postal Expansion v{_version} detected, but {_missing.Count} member(s) did not resolve " +
                        $"({string.Join(", ", _missing.Take(6).ToArray())}). Postal sync disabled: guests see vanilla boards. " +
                        "Check for a Sailwind Co-op update.");
            }
            catch (Exception e)
            {
                SyncEnabled = false;
                Plugin.Log.LogWarning("[PostalCompat] Detection threw. Postal sync disabled. " + e);
            }
        }

        /// <summary>Mod-parity segment: Postal Expansion adds cargo items and mission rules, so the crew must match.</summary>
        public static string ModSignature => IsInstalled ? "PE=" + _version : "";

        private static Type T(Assembly asm, string name)
        {
            var t = asm?.GetType(name);
            if (t == null) _missing.Add(name);
            return t;
        }

        private static MethodInfo M(Type t, string name, params Type[] args)
        {
            var m = t != null && args.All(a => a != null) ? AccessTools.Method(t, name, args) : null;
            if (m == null) _missing.Add((t?.Name ?? "?") + "." + name);
            return m;
        }

        // ================================================================== mission metadata

        /// <summary>What kind of Postal Expansion mail a mission is, and the letter's id.</summary>
        public static void Describe(Mission mission, out byte kind, out string letterId)
        {
            kind = KindNone;
            letterId = "";
            if (!SyncEnabled || mission == null) return;
            try
            {
                var args = new object[] { mission, null };
                if ((bool)_anonTryGet.Invoke(null, args)) { kind = KindAnonymous; letterId = DefinitionId(args[1]); return; }
                args = new object[] { mission, null };
                if ((bool)_regTryGet.Invoke(null, args)) { kind = KindRegistered; letterId = DefinitionId(args[1]); return; }
                if ((bool)_isGolden.Invoke(null, new object[] { mission })) { kind = KindGolden; return; }
                if ((bool)_isUrgent.Invoke(null, new object[] { mission })) kind = KindUrgent;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("[PostalCompat] Could not read a mission's mail kind: " + e.Message);
            }
        }

        /// <summary>Marks a mission rebuilt from the wire the way Postal Expansion marks its own, without changing its numbers.</summary>
        public static void Attach(Mission mission, byte kind, string letterId)
        {
            if (!SyncEnabled || mission == null || kind == KindNone) return;
            try
            {
                switch (kind)
                {
                    case KindUrgent: _markUrgent.Invoke(null, new object[] { mission }); break;
                    case KindGolden: _markGolden.Invoke(null, new object[] { mission }); break;
                    case KindRegistered:
                    case KindAnonymous:
                    {
                        bool anon = kind == KindAnonymous;
                        var def = FindDefinition(anon ? _anonAll : _regAll, letterId);
                        if (def == null)
                        {
                            Plugin.Log.LogWarning($"[PostalCompat] No letter '{letterId}' here; the host may run another Postal Expansion version.");
                            return;
                        }
                        // Attach also sets the letter's name, fixed gold and route distance, which match the host's.
                        int price = mission.totalPrice, due = mission.dueDay;
                        float distance = mission.distance;
                        (anon ? _anonAttach : _regAttach).Invoke(anon ? _anonSet : _regSet, new[] { mission, def });
                        mission.totalPrice = price;
                        mission.dueDay = due;
                        mission.distance = distance;
                        break;
                    }
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[PostalCompat] Could not mark a mission as mail kind {kind}: {e.Message}");
            }
        }

        private static object FindDefinition(PropertyInfo all, string id)
        {
            if (all == null || string.IsNullOrEmpty(id)) return null;
            if (!(all.GetValue(null, null) is IEnumerable list)) return null;
            foreach (var def in list)
                if (def != null && DefinitionId(def) == id) return def;
            return null;
        }

        private static string DefinitionId(object def)
        {
            if (def == null) return "";
            return AccessTools.Property(def.GetType(), "Id")?.GetValue(def, null) as string ?? "";
        }

        /// <summary>The name the host's delivery toast should use for special mail, or null for anything else.</summary>
        public static string DisplayName(Mission mission)
        {
            if (!SyncEnabled || mission == null) return null;
            try
            {
                var kind = _getKind.Invoke(null, new object[] { mission });
                if (Convert.ToInt32(kind) == 0) return null;
                return _getDisplayName.Invoke(null, new[] { kind }) as string;
            }
            catch { return null; }
        }

        public static bool IsLetter(SaveablePrefab prefab)
        {
            if (!IsInstalled || _isLetter == null || prefab == null) return false;
            try { return (bool)_isLetter.Invoke(null, new object[] { prefab }); }
            catch { return false; }
        }

        // ================================================================== boards

        /// <summary>Guest: which of Postal Expansion's tabs the open board shows (0 local, 1 world, 2 mail).</summary>
        public static byte CurrentTab(bool isWorld)
        {
            if (SyncEnabled && MissionListUI.instance != null)
            {
                try { return (byte)Convert.ToInt32(_currentTab.Invoke(null, new object[] { MissionListUI.instance })); }
                catch { }
            }
            return isWorld ? (byte)1 : (byte)0;
        }

        private static List<Mission> Offers(Port port, byte tab)
        {
            var list = _available.Invoke(null, new[] { port, Enum.ToObject(_tabType, (int)tab) }) as List<Mission>;
            return list ?? new List<Mission>();
        }

        /// <summary>Host: one page of a port's board for a tab, built by Postal Expansion itself.</summary>
        public static bool TryBuildBoard(Port port, byte tab, int page, out Mission[] missions, out int total)
        {
            missions = null;
            total = 0;
            if (!SyncEnabled || port == null) return false;
            try
            {
                var list = Offers(port, tab);
                total = list.Count;
                missions = new Mission[5];
                for (int i = 0; i < 5; i++)
                {
                    int at = page * 5 + i;
                    missions[i] = at < list.Count ? list[at] : null;
                }
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("[PostalCompat] Could not build a board for a crewmate: " + e.Message);
                missions = null;
                return false;
            }
        }

        /// <summary>Host: the offer a guest accepted, found in a freshly built list by what it is.</summary>
        public static Mission FindOffer(Port port, byte tab, NetworkMissionData want)
        {
            try
            {
                Mission best = null;
                int bestScore = int.MaxValue;
                foreach (var m in Offers(port, tab))
                {
                    if (m == null || m.originPort == null || m.destinationPort == null || m.goodPrefab == null) continue;
                    if (m.originPort.portIndex != want.OriginPortIndex || m.destinationPort.portIndex != want.DestinationPortIndex) continue;
                    var sp = m.goodPrefab.GetComponent<SaveablePrefab>();
                    if (sp == null || sp.prefabIndex != want.GoodPrefabIndex) continue;
                    Describe(m, out byte kind, out string id);
                    if (kind != want.PostalKind || (id ?? "") != (want.PostalLetterId ?? "")) continue;
                    int score = Math.Abs(m.totalPrice - want.TotalPrice) + Math.Abs(m.dueDay - want.DueDay) * 1000
                                + Math.Abs(m.goodCount - want.GoodCount) * 100000;
                    if (score < bestScore) { bestScore = score; best = m; }
                }
                return best;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("[PostalCompat] Could not find a crewmate's offer: " + e.Message);
                return null;
            }
        }

        /// <summary>Host: redraw the host's own open board after a guest's accept, through Postal Expansion's paging.</summary>
        public static bool RefreshHostBoard(MissionListUI ui)
        {
            if (!SyncEnabled || ui == null) return false;
            try
            {
                int page = Traverse.Create(ui).Field("currentPage").GetValue<int>();
                var missions = _boardPage.Invoke(null, new object[] { ui, page }) as Mission[];
                if (missions == null) return false;
                _updatePageCount.Invoke(null, new object[] { ui });
                _updatePageText.Invoke(null, new object[] { ui });
                ui.DisplayMissions(missions);
                if (MissionDetailsUI.instance != null)
                    Traverse.Create(MissionDetailsUI.instance).Field("UI").GetValue<GameObject>()?.SetActive(false);
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("[PostalCompat] Could not redraw the board: " + e.Message);
                return false;
            }
        }

        // ================================================================== delivery

        /// <summary>
        /// Host, before completing a guest's delivery: a letter is only taken at its own place (deliveryKind 1) and within
        /// its hours; anything else only at an office. A letter delivered here is claimed the way Postal Expansion claims it.
        /// </summary>
        public static bool HostMayDeliver(Mission mission, byte deliveryKind)
        {
            if (!SyncEnabled || mission == null) return deliveryKind == 0;
            try
            {
                var args = new object[] { mission, null };
                bool letter = (bool)_letterTryGet.Invoke(null, args);
                if (!letter)
                {
                    if (deliveryKind != 0) Plugin.Log.LogWarning("[PostalCompat] A crewmate's drop-off delivery was not a letter; refused.");
                    return deliveryKind == 0;
                }
                if (deliveryKind == 0)
                {
                    Plugin.Log.LogWarning("[PostalCompat] A crewmate handed a letter in at an office; refused.");
                    return false;
                }
                var window = AccessTools.Property(args[1].GetType(), "DeliveryWindow")?.GetValue(args[1], null);
                if (window != null && !(bool)_isOpen.Invoke(null, new[] { window }))
                {
                    Plugin.Log.LogWarning("[PostalCompat] A crewmate's letter arrived outside its hours here; refused.");
                    return false;
                }
                _letterDelivered.Invoke(null, new object[] { mission });
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("[PostalCompat] Could not check a letter delivery: " + e.Message);
                return false;
            }
        }
    }
}
