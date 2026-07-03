using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using SailwindCoop.Networking.Packets;
using Steamworks;
using UnityEngine;
using UnityEngine.UI;

namespace SailwindCoop.UI
{
    /// <summary>
    /// Crew "spending feed": a killfeed line for EVERY crew trade, so the whole crew can watch the
    /// shared wallet move. Vanilla feedback (gold sound + MoneyNotification) only ever plays on the
    /// acting client, and the mod's MarketTradeResult/ShopTradeResult packets are deliberately
    /// TARGETED to the requester - uninvolved crew see nothing. The HOST already observes every trade
    /// (its own vanilla trades plus every guest trade it executes host-authoritatively), so Report()
    /// is host-only: it broadcasts a TradeFeedEvent to all guests and renders the host's own copy
    /// locally. Receivers show the feed line and, for OTHER crew members' trades, play a quiet coin
    /// cue (the actor already heard the vanilla full-volume gold sound). Both are gated receiver-side
    /// by Coop.SpendingFeed.
    /// </summary>
    public static class TradeFeed
    {
        /// <summary>
        /// Host-only: broadcast one feed line to all guests and render it locally. Guests never send
        /// this packet - every guest trade is executed (and therefore reported) on the host.
        /// </summary>
        public static void Report(ulong actor, bool isBuying, string itemName, int price, int currency)
        {
            if (!Plugin.IsMultiplayer || !Plugin.IsHost) return;

            var packet = new TradeFeedEventPacket
            {
                ActorSteamId = actor,
                Flags = (byte)(isBuying ? 1 : 0),
                CurrencyIndex = (byte)Mathf.Clamp(currency, 0, 3),
                Price = price,
                ItemName = CleanItemName(itemName)
            };

            Debug.VerboseLogger.Log("TRADING", "SEND", $"TradeFeedEvent: actor={actor}, buying={isBuying}, item={packet.ItemName}, price={price}, currency={currency}");

            Plugin.NetworkManager?.SendToAllReliable(PacketType.TradeFeedEvent, w =>
                PacketSerializer.WriteTradeFeedEvent(w, packet));

            // The host never receives its own broadcast - render the local copy directly.
            ShowLocal(packet);
        }

        /// <summary>Guest: a feed line from the host. Pure UI/audio - no game state is touched.</summary>
        public static void OnRemoteTradeFeedEvent(TradeFeedEventPacket packet)
        {
            if (Plugin.IsHost) return; // host already rendered its copy inside Report
            ShowLocal(packet);
        }

        private static void ShowLocal(TradeFeedEventPacket packet)
        {
            // Receiver-side gate: hides the feed AND the coin cue on THIS machine only.
            if (Plugin.SpendingFeedConfig != null && !Plugin.SpendingFeedConfig.Value) return;

            bool isSelf = SteamClient.IsValid && packet.ActorSteamId == SteamClient.SteamId;
            string actorName = isSelf ? SteamClient.Name : new Friend(packet.ActorSteamId).Name;
            if (string.IsNullOrEmpty(actorName)) actorName = "Crewmate";

            bool isBuying = (packet.Flags & 1) != 0;
            string itemName = string.IsNullOrEmpty(packet.ItemName) ? "an item" : packet.ItemName;
            TradeFeedUI.ShowLine($"{actorName} {(isBuying ? "bought" : "sold")} {itemName} for {packet.Price} {CurrencyName(packet.CurrencyIndex)}");

            // Quiet coin cue only for OTHER crew members' trades - the actor already heard the
            // vanilla full-volume gold sound locally.
            if (!isSelf) PlayQuietCoin();
        }

        private static readonly string[] CurrencyNames = { "al'ankh", "emeralds", "aestrin", "gold" };

        private static string CurrencyName(int index) =>
            index >= 0 && index < CurrencyNames.Length ? CurrencyNames[index] : "coins";

        /// <summary>Resolve a market good's display name via its item prefab (host-side).</summary>
        public static string GoodDisplayName(int goodIndex)
        {
            try
            {
                int itemIndex = PrefabsDirectory.GoodToItemIndex(goodIndex);
                var dir = PrefabsDirectory.instance?.directory;
                if (dir != null && itemIndex > 0 && itemIndex < dir.Length && dir[itemIndex] != null)
                    return CleanItemName(dir[itemIndex].name);
            }
            catch { }
            return "goods";
        }

        /// <summary>Resolve a raw prefab index's display name (host-side, stall trades).</summary>
        public static string PrefabDisplayName(int prefabIndex)
        {
            var dir = PrefabsDirectory.instance?.directory;
            if (dir != null && prefabIndex > 0 && prefabIndex < dir.Length && dir[prefabIndex] != null)
                return CleanItemName(dir[prefabIndex].name);
            return "an item";
        }

        /// <summary>Strip the Unity "(Clone)" suffix and a trailing "(N)" copy number off an item name.</summary>
        public static string CleanItemName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "an item";
            name = name.Replace("(Clone)", "").Trim();
            int open = name.LastIndexOf('(');
            if (open > 0 && name.EndsWith(")") && open + 1 < name.Length - 1)
            {
                bool allDigits = true;
                for (int i = open + 1; i < name.Length - 1; i++)
                {
                    if (!char.IsDigit(name[i])) { allDigits = false; break; }
                }
                if (allDigits) name = name.Substring(0, open).Trim();
            }
            return name.Length > 0 ? name : "an item";
        }

        // Quiet coin cue: the vanilla gold clip (UISoundPlayer's private [SerializeField] AudioClip)
        // replayed through a MOD-OWNED 2D AudioSource at Coop.SpendingFeedVolume. Never call
        // PlayGoldSound (fixed volume 1) or mutate UISoundPlayer's pooled sources.
        private static AudioSource _coinSource;
        private static AudioClip _goldClip;
        private static bool _goldClipLookupFailed;

        private static void PlayQuietCoin()
        {
            try
            {
                if (_goldClip == null && !_goldClipLookupFailed)
                {
                    if (UISoundPlayer.instance == null) return; // not loaded yet; retry on the next event
                    _goldClip = AccessTools.Field(typeof(UISoundPlayer), "gold")
                        ?.GetValue(UISoundPlayer.instance) as AudioClip;
                    if (_goldClip == null)
                    {
                        _goldClipLookupFailed = true; // field missing/renamed: don't re-reflect every event
                        Debug.VerboseLogger.Log("TRADING", "WARN", "TradeFeed: UISoundPlayer.gold clip not found; coin cue disabled");
                        return;
                    }
                }
                if (_goldClip == null) return;

                if (_coinSource == null)
                {
                    if (Plugin.Instance == null) return;
                    _coinSource = Plugin.Instance.gameObject.AddComponent<AudioSource>();
                    _coinSource.spatialBlend = 0f; // 2D UI cue, never positional
                    _coinSource.playOnAwake = false;
                }

                float volume = Plugin.SpendingFeedVolumeConfig != null
                    ? Mathf.Clamp01(Plugin.SpendingFeedVolumeConfig.Value)
                    : 0.35f;
                _coinSource.PlayOneShot(_goldClip, volume);
            }
            catch (System.Exception ex)
            {
                Debug.VerboseLogger.Log("TRADING", "WARN", $"TradeFeed coin cue failed: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Bottom-right killfeed overlay for the spending feed: a mod-owned ScreenSpaceOverlay canvas,
    /// max 5 entries, each held 4s then faded out over 1s (realtime, so pause/timewarp don't stall
    /// it). No GraphicRaycaster and raycastTarget=false everywhere, so it never eats clicks. Falls
    /// back to Plugin.Notify if the canvas can't be built.
    /// </summary>
    public class TradeFeedUI : MonoBehaviour
    {
        private const int MaxEntries = 5;
        private const float HoldSeconds = 4f;
        private const float FadeSeconds = 1f;

        private static TradeFeedUI _instance;
        private static bool _canvasFailed;

        private RectTransform _container;
        private readonly List<GameObject> _entries = new List<GameObject>();

        public static void ShowLine(string line)
        {
            if (_canvasFailed)
            {
                Plugin.Notify(line, 4f);
                return;
            }
            try
            {
                if (_instance == null) _instance = Create();
                _instance.AddEntry(line);
            }
            catch (System.Exception ex)
            {
                _canvasFailed = true;
                Debug.VerboseLogger.Log("TRADING", "WARN", $"TradeFeed canvas failed ({ex.Message}); falling back to notifications");
                Plugin.Notify(line, 4f);
            }
        }

        private static TradeFeedUI Create()
        {
            var go = new GameObject("CoopTradeFeed");
            DontDestroyOnLoad(go);
            var canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 5000; // above the vanilla UI
            var ui = go.AddComponent<TradeFeedUI>();

            var containerGo = new GameObject("Entries");
            containerGo.transform.SetParent(go.transform, false);
            var rect = containerGo.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(1f, 0f);
            rect.anchorMax = new Vector2(1f, 0f);
            rect.pivot = new Vector2(1f, 0f);
            rect.anchoredPosition = new Vector2(-12f, 12f);
            rect.sizeDelta = new Vector2(460f, 130f);
            var layout = containerGo.AddComponent<VerticalLayoutGroup>();
            layout.childAlignment = TextAnchor.LowerRight;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            layout.spacing = 2f;
            ui._container = rect;
            return ui;
        }

        private void AddEntry(string line)
        {
            // Faded-out entries destroy themselves; drop the stale refs before enforcing the cap.
            _entries.RemoveAll(e => e == null);
            while (_entries.Count >= MaxEntries)
            {
                Destroy(_entries[0]);
                _entries.RemoveAt(0);
            }

            var entryGo = new GameObject("FeedLine");
            entryGo.transform.SetParent(_container, false);
            var text = entryGo.AddComponent<Text>();
            text.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            text.fontSize = 14;
            text.alignment = TextAnchor.MiddleRight;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.color = new Color(1f, 0.95f, 0.8f, 1f); // warm parchment tone, matching the game's UI
            text.raycastTarget = false;
            text.text = line;
            var shadow = entryGo.AddComponent<Shadow>();
            shadow.effectDistance = new Vector2(1f, -1f);
            shadow.effectColor = new Color(0f, 0f, 0f, 0.8f);
            var group = entryGo.AddComponent<CanvasGroup>();
            group.alpha = 1f;
            group.blocksRaycasts = false;
            group.interactable = false;

            _entries.Add(entryGo);
            StartCoroutine(HoldThenFade(entryGo, group));
        }

        private IEnumerator HoldThenFade(GameObject entry, CanvasGroup group)
        {
            yield return new WaitForSecondsRealtime(HoldSeconds);
            float t = 0f;
            while (t < FadeSeconds)
            {
                if (entry == null) yield break; // evicted early by the entry cap
                t += Time.unscaledDeltaTime;
                group.alpha = 1f - Mathf.Clamp01(t / FadeSeconds);
                yield return null;
            }
            if (entry != null) Destroy(entry);
        }
    }
}
