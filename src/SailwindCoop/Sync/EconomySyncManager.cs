using UnityEngine;
using HarmonyLib;
using SailwindCoop.Debug;
using SailwindCoop.Networking.Packets;
using Steamworks;

namespace SailwindCoop.Sync
{
    /// <summary>
    /// Manages synchronization of currency and reputation between host and guest.
    /// Host broadcasts on change, guest receives and applies.
    /// </summary>
    public class EconomySyncManager : MonoBehaviour
    {
        public static EconomySyncManager Instance { get; private set; }

        // Cache to detect changes
        private int[] _lastCurrency = new int[4];
        private int[] _lastReputation = new int[4];

        // JOIN-ROBUSTNESS: true once a guest has applied at least one authoritative CurrencySync since the
        // current join started. Plugin's guest-side retry loop resends EconomySyncRequest until this sets:
        // a silently-dropped join packet would otherwise leave the guest on their solo-save wallet, so a
        // starved join self-heals instead of keeping the stale local balance.
        public bool FirstCurrencyAppliedSinceJoin { get; private set; }

        /// <summary>Guest-side: a new join is starting - forget any previously applied currency sync.</summary>
        public void MarkJoinStarted()
        {
            FirstCurrencyAppliedSinceJoin = false;
        }

        private void Awake()
        {
            if (Instance != null)
            {
                Destroy(this);
                return;
            }
            Instance = this;
        }

        private void Update()
        {
            if (!Plugin.IsMultiplayer || !Plugin.IsHost) return;

            Plugin.Profiler?.StartMeasure();

            // Check for currency/reputation changes and broadcast
            CheckAndSyncCurrency();
            CheckAndSyncReputation();

            Plugin.Profiler?.EndMeasure("Economy");
        }

        #region Host Methods

        private void CheckAndSyncCurrency()
        {
            bool changed = false;
            for (int i = 0; i < 4; i++)
            {
                if (PlayerGold.currency[i] != _lastCurrency[i])
                {
                    changed = true;
                    _lastCurrency[i] = PlayerGold.currency[i];
                }
            }

            if (changed)
            {
                SendCurrencySync();
            }
        }

        private void CheckAndSyncReputation()
        {
            var repData = PlayerReputation.GetSaveData();
            if (repData == null) return;

            bool changed = false;
            for (int i = 0; i < 4; i++)
            {
                if (repData[i] != _lastReputation[i])
                {
                    changed = true;
                    _lastReputation[i] = repData[i];
                }
            }

            if (changed)
            {
                SendReputationSync();
            }
        }

        // N-player (Phase 3): an optional `target` routes the send to ONE joining guest instead of all.
        // null => broadcast (unchanged on-change behavior). At N=1 a targeted send hits the only peer, so
        // SendXSync(theOneGuest) == SendXSync() broadcast - identical.
        private void SendCurrencySync(SteamId? target = null)
        {
            var packet = new CurrencySyncPacket
            {
                Currency = (int[])PlayerGold.currency.Clone()
            };

            VerboseLogger.Log("ECONOMY", "SEND", $"Currency, values=[{string.Join(",", packet.Currency)}]{(target.HasValue ? $" (to {target.Value})" : "")}");

            if (target.HasValue)
                Plugin.NetworkManager.SendReliable(target.Value, PacketType.CurrencySync, w =>
                    PacketSerializer.WriteCurrencySync(w, packet));
            else
                Plugin.NetworkManager.SendToAllReliable(PacketType.CurrencySync, w =>
                    PacketSerializer.WriteCurrencySync(w, packet));
        }

        /// <summary>
        /// Re-assert the authoritative wallet to ONE guest WITHOUT touching the
        /// on-change cache (_lastCurrency), so the normal CheckAndSyncCurrency broadcast still diffs correctly
        /// for everyone else. Used after a shop trade so a rejected/no-op request still corrects the buyer's
        /// optimistic local deduction (the on-delta broadcast sends nothing when the host wallet didn't change).
        /// </summary>
        public void ResyncCurrencyTo(SteamId target)
        {
            if (!Plugin.IsHost) return;
            SendCurrencySync(target);
        }

        private void SendReputationSync(SteamId? target = null)
        {
            var packet = new ReputationSyncPacket
            {
                Reputation = (int[])PlayerReputation.GetSaveData().Clone()
            };

            VerboseLogger.Log("ECONOMY", "SEND", $"Reputation, values=[{string.Join(",", packet.Reputation)}]{(target.HasValue ? $" (to {target.Value})" : "")}");

            if (target.HasValue)
                Plugin.NetworkManager.SendReliable(target.Value, PacketType.ReputationSync, w =>
                    PacketSerializer.WriteReputationSync(w, packet));
            else
                Plugin.NetworkManager.SendToAllReliable(PacketType.ReputationSync, w =>
                    PacketSerializer.WriteReputationSync(w, packet));
        }

        /// <summary>
        /// Send full economy state immediately (on guest join). Broadcasts to all peers.
        /// </summary>
        public void SendFullStateImmediate()
        {
            if (!Plugin.IsHost) return;

            // Update cache
            for (int i = 0; i < 4; i++)
            {
                _lastCurrency[i] = PlayerGold.currency[i];
                _lastReputation[i] = PlayerReputation.GetSaveData()[i];
            }

            SendCurrencySync();
            SendReputationSync();
        }

        /// <summary>
        /// Send full economy state to ONE joining guest (N-player Phase 3 targeted join resync).
        /// Same payload as SendFullStateImmediate, targeted so already-settled guests aren't re-synced.
        /// Updates the change-cache too (so a subsequent on-change broadcast still diffs correctly).
        /// At N=1 the target is the only peer, so this == the broadcast.
        /// </summary>
        public void SendFullStateTo(SteamId target)
        {
            if (!Plugin.IsHost) return;

            for (int i = 0; i < 4; i++)
            {
                _lastCurrency[i] = PlayerGold.currency[i];
                _lastReputation[i] = PlayerReputation.GetSaveData()[i];
            }

            SendCurrencySync(target);
            SendReputationSync(target);
            SendNonBoatOwnershipTo(target);
        }

        #endregion

        #region Guest Methods

        public void OnCurrencySyncReceived(CurrencySyncPacket packet)
        {
            if (Plugin.IsHost) return;

            VerboseLogger.Log("ECONOMY", "RECV", $"Currency, values=[{string.Join(",", packet.Currency)}]");

            for (int i = 0; i < 4; i++)
            {
                PlayerGold.currency[i] = packet.Currency[i];
            }

            FirstCurrencyAppliedSinceJoin = true; // stops the guest-side EconomySyncRequest retry loop
            VerboseLogger.Log("ECONOMY", "APPLY", "Currency applied from host");
        }

        public void OnReputationSyncReceived(ReputationSyncPacket packet)
        {
            if (Plugin.IsHost) return;

            VerboseLogger.Log("ECONOMY", "RECV", $"Reputation, values=[{string.Join(",", packet.Reputation)}]");

            PlayerReputation.LoadReputation(packet.Reputation);

            VerboseLogger.Log("ECONOMY", "APPLY", "Reputation applied from host");
        }

        #endregion

        #region Trading Handlers

        /// <summary>
        /// Handle guest currency exchange request.
        /// </summary>
        public void OnExchangeRequestReceived(SteamId sender, ExchangeRequestPacket packet)
        {
            if (!Plugin.IsHost) return;

            VerboseLogger.Log("ECONOMY", "RECV", $"ExchangeRequest from {sender}, sell={packet.SellCurrency}, buy={packet.BuyCurrency}, amount={packet.Amount}");

            // Validate same currency
            if (packet.SellCurrency == packet.BuyCurrency)
            {
                VerboseLogger.Log("ECONOMY", "REJECT", "Cannot exchange same currency");
                return;
            }

            // Validate currency indices
            if (packet.SellCurrency < 0 || packet.SellCurrency >= 4 ||
                packet.BuyCurrency < 0 || packet.BuyCurrency >= 4)
            {
                VerboseLogger.Log("ECONOMY", "REJECT", $"Invalid currency index: sell={packet.SellCurrency}, buy={packet.BuyCurrency}");
                return;
            }

            // Validate enough currency
            if (PlayerGold.currency[packet.SellCurrency] < packet.Amount)
            {
                VerboseLogger.Log("ECONOMY", "REJECT", $"Not enough currency: have {PlayerGold.currency[packet.SellCurrency]}, need {packet.Amount}");
                return;
            }

            // Calculate exchange rate
            float rate = CurrencyMarket.instance.GetExchangeRate(packet.SellCurrency, packet.BuyCurrency, withConversionFee: true);
            int buyAmount = UnityEngine.Mathf.FloorToInt(packet.Amount * rate);

            // Execute exchange
            PlayerGold.currency[packet.SellCurrency] -= packet.Amount;
            PlayerGold.currency[packet.BuyCurrency] += buyAmount;

            // Update market (affects future rates)
            CurrencyMarket.instance.SellCurrency(packet.SellCurrency, packet.Amount);
            CurrencyMarket.instance.BuyCurrency(packet.BuyCurrency, buyAmount);

            VerboseLogger.Log("ECONOMY", "EXEC", $"Exchange: sold {packet.Amount} of {packet.SellCurrency}, got {buyAmount} of {packet.BuyCurrency}");

            // Currency changes will sync via normal polling
        }

        #endregion

        #region Boat Ownership

        /// <summary>
        /// HOST: broadcast that a boat's ownership changed at runtime (a player bought it). Already-connected
        /// peers apply it live; a later joiner gets ownership via the join snapshot instead. Mirrors the
        /// AnchorEvent broadcast (BoatName-keyed, SendToAllReliable).
        /// </summary>
        public void SendBoatOwnershipChanged(string boatName, bool isOwned, SteamId? target = null)
        {
            if (!Plugin.IsHost || !Plugin.IsMultiplayer) return;
            if (string.IsNullOrEmpty(boatName)) return;

            var packet = new BoatOwnershipChangedPacket
            {
                BoatName = boatName,
                IsOwned = isOwned
            };

            VerboseLogger.Log("ECONOMY", "SEND", $"BoatOwnershipChanged, boat={boatName}, owned={isOwned}{(target.HasValue ? $" (to {target.Value})" : "")}");

            if (target.HasValue)
                Plugin.NetworkManager.SendReliable(target.Value, PacketType.BoatOwnershipChanged, w =>
                    PacketSerializer.WriteBoatOwnershipChanged(w, packet));
            else
                Plugin.NetworkManager.SendToAllReliable(PacketType.BoatOwnershipChanged, w =>
                    PacketSerializer.WriteBoatOwnershipChanged(w, packet));
        }

        /// <summary>
        /// HOST: replay ownership of every NON-boat purchasable, owned or not, to one joiner. The join snapshot
        /// is built from the BoatRefs-keyed boat set, so a Sailwind 0.39 house the crew already owns is not
        /// in it and a joiner would arrive to a locked door and a FOR SALE sign on a house that is paid for.
        /// Reuses the live ownership packet, so the snapshot format is untouched.
        /// </summary>
        private void SendNonBoatOwnershipTo(SteamId target)
        {
            foreach (var obj in BoatUtility.FindNonBoatPurchasables())
            {
                var purchasable = obj.GetComponent<PurchasableBoat>();
                if (purchasable == null) continue;
                // Assert the host's value either way, the same as the boat snapshot does (it carries
                // IsOwned = extraSetting for every boat). Only pushing "owned" would leave a joiner whose own
                // phantom save had bought the house able to open a door the crew never paid for: vanilla's
                // SaveableObject.Load restores extraSetting from that save and nothing else clears it.
                // ApplyOwnership handles false by writing extraSetting back off, which re-locks the door.
                SendBoatOwnershipChanged(obj.gameObject.name, purchasable.isPurchased(), target);
            }
        }

        /// <summary>
        /// PEER: apply a runtime ownership change broadcast by the host. Reuses the shared snapshot
        /// ownership-apply helper so a live purchase touches identical fields. Host ignores (it is the source).
        /// </summary>
        public void OnBoatOwnershipChangedReceived(BoatOwnershipChangedPacket packet)
        {
            if (Plugin.IsHost) return;

            VerboseLogger.Log("ECONOMY", "RECV", $"BoatOwnershipChanged, boat={packet.BoatName}, owned={packet.IsOwned}");

            // Purchasable, not just boats: Sailwind 0.39 sells houses through PurchasableBoat, and a house
            // has no BoatRefs, so the boat-keyed lookup dropped every house ownership packet on the floor
            // and the guest's house door stayed locked (GPButtonHouseDoor only opens on extraSetting).
            var boat = BoatUtility.FindPurchasableByName(packet.BoatName);
            if (boat == null)
            {
                VerboseLogger.Log("ECONOMY", "APPLY", $"BoatOwnershipChanged: boat '{packet.BoatName}' not found");
                return;
            }

            BoatStateApplicator.ApplyOwnership(boat, packet.IsOwned);
            VerboseLogger.Log("ECONOMY", "APPLY", $"BoatOwnershipChanged applied to {packet.BoatName}");
        }

        /// <summary>
        /// HOST: a guest requested to buy a boat. Validate against the SHARED wallet (gold = currency[3], the
        /// same slot vanilla PurchasableBoat.PurchaseBoat charges) and perform the authoritative purchase. The
        /// vanilla PurchaseBoat() postfix (host branch) broadcasts BoatOwnershipChanged, and the currency
        /// deduction reaches every peer via the normal CurrencySync polling.
        /// </summary>
        public void OnBoatPurchaseRequestReceived(SteamId sender, BoatPurchaseRequestPacket packet)
        {
            if (!Plugin.IsHost) return;

            VerboseLogger.Log("ECONOMY", "RECV", $"BoatPurchaseRequest from {sender}, boat={packet.BoatName}");

            // Purchasable, not just boats - a guest buying a 0.39 house lands here too.
            var boat = BoatUtility.FindPurchasableByName(packet.BoatName);
            if (boat == null)
            {
                VerboseLogger.Log("ECONOMY", "REJECT", $"BoatPurchaseRequest: boat '{packet.BoatName}' not found");
                return;
            }

            var purchasable = boat.GetComponent<PurchasableBoat>();
            if (purchasable == null)
            {
                VerboseLogger.Log("ECONOMY", "REJECT", $"BoatPurchaseRequest: '{packet.BoatName}' is not purchasable");
                return;
            }

            if (purchasable.isPurchased())
            {
                // Already owned (e.g. host bought it first, or duplicate request). Re-assert ownership to the
                // requester so their suppressed-but-still-for-sale UI corrects, then bail.
                VerboseLogger.Log("ECONOMY", "INFO", $"BoatPurchaseRequest: '{packet.BoatName}' already owned; re-asserting");
                SendBoatOwnershipChanged(packet.BoatName, true);
                return;
            }

            if (PlayerGold.currency[3] < purchasable.price)
            {
                VerboseLogger.Log("ECONOMY", "REJECT", $"BoatPurchaseRequest: not enough gold (have {PlayerGold.currency[3]}, need {purchasable.price})");
                // Correct the requester's optimistic state: re-assert the authoritative wallet so any local
                // deduction is undone. (Vanilla guest deduction is suppressed by our patch, but this is cheap
                // insurance and matches the shop-trade reject path.)
                ResyncCurrencyTo(sender);
                return;
            }

            // Authoritative purchase. PurchaseBoat() deducts currency[3], sets extraSetting, logs + plays the
            // gold sound on the HOST, and our PurchaseBoatPatch postfix (host branch) broadcasts ownership.
            VerboseLogger.Log("ECONOMY", "EXEC", $"BoatPurchaseRequest: buying '{packet.BoatName}' for {purchasable.price}");
            // Sailwind 0.39's PurchaseBoat walks BoatRefs.masts to reset the jib hinges, but that array is a
            // SPARSE Mast[48] (BoatRefs.cs:15, filled by orderIndex) and the loop has no null check, so it
            // throws on the first empty slot - AFTER the purchase has fully completed. Never let that escape
            // into the packet handler: the ownership broadcast is sent from PurchaseBoatPatch's finalizer.
            try
            {
                purchasable.PurchaseBoat();
            }
            catch (System.Exception e)
            {
                // The finalizer already swallowed the known 0.39 NRE, so this is something else.
                Plugin.Log.LogWarning($"[ECONOMY] vanilla PurchaseBoat threw for '{packet.BoatName}', " +
                    $"owned now={purchasable.isPurchased()}: {e.GetType().Name}: {e.Message}");
            }
        }

        #endregion

        public void Reset()
        {
            _lastCurrency = new int[4];
            _lastReputation = new int[4];
            FirstCurrencyAppliedSinceJoin = false;
        }
    }
}
