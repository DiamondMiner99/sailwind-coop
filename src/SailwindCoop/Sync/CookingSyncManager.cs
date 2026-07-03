using System.Collections.Generic;
using UnityEngine;
using HarmonyLib;
using Steamworks;
using SailwindCoop.Debug;
using SailwindCoop.Networking.Packets;

namespace SailwindCoop.Sync
{
    /// <summary>
    /// Manages cooking synchronization (stove, food state, soup, kettle).
    /// Host-authoritative with 2Hz polling.
    /// </summary>
    public class CookingSyncManager : MonoBehaviour
    {
        public static CookingSyncManager Instance { get; private set; }

        private const float SyncInterval = 0.5f; // 2Hz
        private float _lastSyncTime;
        private string _lastPollKey;
        private string _lastRecvKey;

        // Flag to prevent re-entrant calls during remote state application
        public bool IsApplyingRemoteState { get; private set; }

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
            if (!Plugin.IsMultiplayer) return;
            if (!Plugin.IsHost) return;

            if (Time.time - _lastSyncTime >= SyncInterval)
            {
                _lastSyncTime = Time.time;

                Plugin.Profiler?.StartMeasure();
                PollAndSendCookingState();
                Plugin.Profiler?.EndMeasure("Cooking");
            }
        }

        #region Host: State Collection & Broadcast

        private void PollAndSendCookingState()
        {
            var packet = CollectCookingState();

            // Only log when counts change to avoid spam
            var key = $"{packet.Foods.Count},{packet.Soups.Count},{packet.Kettles.Count}";
            if (key != _lastPollKey)
            {
                VerboseLogger.CookingPoll($"foods={packet.Foods.Count}, soups={packet.Soups.Count}, kettles={packet.Kettles.Count}");
                _lastPollKey = key;
            }

            Plugin.NetworkManager.SendToAllReliable(PacketType.CookingState, w =>
                PacketSerializer.WriteCookingState(w, packet));
        }

        private CookingStatePacket CollectCookingState()
        {
            var packet = new CookingStatePacket
            {
                Foods = new List<FoodCookingState>(),
                Soups = new List<SoupState>(),
                Kettles = new List<KettleState>()
            };

            // Collect food items (global search - supports land cooking)
            foreach (var cookable in Object.FindObjectsOfType<CookableFood>())
            {
                var prefab = cookable.GetComponent<SaveablePrefab>();
                if (prefab == null) continue;

                var item = cookable.GetComponent<ShipItem>();
                if (item == null || !item.sold) continue;

                var foodState = cookable.GetComponent<FoodState>();
                var trigger = cookable.GetCurrentCookTrigger();

                int stoveSlot = -1;
                int stoveId = 0;
                if (trigger != null)
                {
                    var stove = trigger.GetComponentInParent<ShipItemStove>();
                    if (stove != null)
                    {
                        var stovePrefab = stove.GetComponent<SaveablePrefab>();
                        if (stovePrefab != null)
                        {
                            stoveId = stovePrefab.instanceId;
                            // Find slot index
                            for (int i = 0; i < stove.slots.Length; i++)
                            {
                                if (stove.slots[i] == trigger)
                                {
                                    stoveSlot = i;
                                    break;
                                }
                            }
                        }
                    }
                }

                packet.Foods.Add(new FoodCookingState
                {
                    InstanceId = prefab.instanceId,
                    Amount = item.amount,
                    CurrentHeat = cookable.GetCurrentHeat(),
                    Spoiled = foodState?.spoiled ?? 0f,
                    Salted = foodState?.salted ?? 0f,
                    Smoked = foodState?.smoked ?? 0f,
                    Dried = foodState?.dried ?? 0f,
                    StoveSlotIndex = stoveSlot,
                    StoveInstanceId = stoveId
                });
            }

            // Collect soup pots (global search - supports land cooking)
            foreach (var soup in Object.FindObjectsOfType<ShipItemSoup>())
            {
                var prefab = soup.GetComponent<SaveablePrefab>();
                if (prefab == null) continue;
                if (!soup.sold) continue;

                var cookable = soup.GetComponent<CookableFoodSoup>();

                packet.Soups.Add(new SoupState
                {
                    InstanceId = prefab.instanceId,
                    CurrentWater = soup.currentWater,
                    CurrentEnergy = soup.currentEnergy,
                    CurrentUncookedEnergy = soup.currentUncookedEnergy,
                    CurrentSpoiled = soup.currentSpoiled,
                    CurrentVitamins = soup.currentVitamins,
                    CurrentProtein = soup.currentProtein,
                    CurrentSalted = soup.currentSalted,
                    CurrentHeat = cookable?.GetCurrentHeat() ?? 0f
                });
            }

            // Collect kettles (global search - supports land cooking)
            foreach (var kettle in Object.FindObjectsOfType<ShipItemKettle>())
            {
                var prefab = kettle.GetComponent<SaveablePrefab>();
                if (prefab == null) continue;
                if (!kettle.sold) continue;

                var cookable = kettle.GetComponent<CookableFood>();

                packet.Kettles.Add(new KettleState
                {
                    InstanceId = prefab.instanceId,
                    CurrentWater = kettle.currentWater,
                    CurrentTeaAmount = kettle.currentTeaAmount,
                    CurrentCookedTeaAmount = kettle.currentCookedTeaAmount,
                    CurrentTeaType = (int)kettle.currentTeaType,
                    CurrentHeat = cookable?.GetCurrentHeat() ?? 0f
                });
            }

            return packet;
        }

        #endregion

        #region Guest: State Application

        public void OnCookingStateReceived(CookingStatePacket packet)
        {
            if (Plugin.IsHost) return;

            // Only log when counts change to avoid spam
            var key = $"{packet.Foods.Count},{packet.Soups.Count},{packet.Kettles.Count}";
            if (key != _lastRecvKey)
            {
                VerboseLogger.CookingRecv($"foods={packet.Foods.Count}, soups={packet.Soups.Count}, kettles={packet.Kettles.Count}");
                _lastRecvKey = key;
            }

            IsApplyingRemoteState = true;
            try
            {
                ApplyFoodStates(packet.Foods);
                ApplySoupStates(packet.Soups);
                ApplyKettleStates(packet.Kettles);
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogError($"Exception in OnCookingStateReceived: {ex}");
            }
            finally
            {
                IsApplyingRemoteState = false;
            }
        }

        private void ApplyFoodStates(List<FoodCookingState> foods)
        {
            foreach (var state in foods)
            {
                var cookable = FindCookableByInstanceId(state.InstanceId);
                if (cookable == null) continue;

                var item = cookable.GetComponent<ShipItem>();
                var foodState = cookable.GetComponent<FoodState>();

                // Apply cooking state
                if (item != null)
                    item.amount = state.Amount;

                cookable.SetHeat(state.CurrentHeat);

                // Apply preservation state
                if (foodState != null)
                {
                    foodState.spoiled = state.Spoiled;
                    foodState.salted = state.Salted;
                    foodState.smoked = state.Smoked;
                    foodState.dried = state.Dried;

                    // Update description text (private method, use Traverse)
                    Traverse.Create(foodState).Method("UpdateLookText").GetValue();
                }

                // Update visuals (only for actual food items, not kettles/pots which lack FoodState)
                if (foodState != null)
                    cookable.UpdateMaterial();

                // Sync stove slot position
                var currentTrigger = cookable.GetCurrentCookTrigger();
                if (state.StoveSlotIndex >= 0)
                {
                    // Food should be on stove
                    var stove = FindStoveByInstanceId(state.StoveInstanceId);
                    if (stove != null && state.StoveSlotIndex < stove.slots.Length)
                    {
                        var targetSlot = stove.slots[state.StoveSlotIndex];
                        if (currentTrigger != targetSlot)
                        {
                            // Need to insert into correct slot
                            if (currentTrigger != null)
                            {
                                cookable.TakeOutOfCooker();
                            }
                            cookable.InsertIntoCookTrigger(targetSlot);
                            VerboseLogger.CookingApply($"Food {state.InstanceId}: inserted into stove {state.StoveInstanceId} slot {state.StoveSlotIndex}");
                        }
                    }
                }
                else
                {
                    // Food should NOT be on stove
                    if (currentTrigger != null)
                    {
                        cookable.TakeOutOfCooker();
                        VerboseLogger.CookingApply($"Food {state.InstanceId}: removed from stove");
                    }
                }
            }
        }

        private void ApplySoupStates(List<SoupState> soups)
        {
            foreach (var state in soups)
            {
                var soup = FindSoupByInstanceId(state.InstanceId);
                if (soup == null) continue;

                soup.currentWater = state.CurrentWater;
                soup.currentEnergy = state.CurrentEnergy;
                soup.currentUncookedEnergy = state.CurrentUncookedEnergy;
                soup.currentSpoiled = state.CurrentSpoiled;
                soup.currentVitamins = state.CurrentVitamins;
                soup.currentProtein = state.CurrentProtein;
                soup.currentSalted = state.CurrentSalted;

                var cookable = soup.GetComponent<CookableFoodSoup>();
                if (cookable != null)
                    cookable.SetHeat(state.CurrentHeat);

                soup.UpdateLookText();
            }
        }

        private void ApplyKettleStates(List<KettleState> kettles)
        {
            foreach (var state in kettles)
            {
                var kettle = FindKettleByInstanceId(state.InstanceId);
                if (kettle == null) continue;

                kettle.currentWater = state.CurrentWater;
                kettle.currentTeaAmount = state.CurrentTeaAmount;
                kettle.currentCookedTeaAmount = state.CurrentCookedTeaAmount;
                kettle.currentTeaType = (LiquidType)state.CurrentTeaType;

                var cookable = kettle.GetComponent<CookableFood>();
                if (cookable != null)
                    cookable.SetHeat(state.CurrentHeat);

                kettle.UpdateLookText();
            }
        }

        #endregion

        #region Lookups

        private CookableFood FindCookableByInstanceId(int instanceId)
        {
            // Global search - supports land cooking
            foreach (var cookable in Object.FindObjectsOfType<CookableFood>())
            {
                var prefab = cookable.GetComponent<SaveablePrefab>();
                if (prefab != null && prefab.instanceId == instanceId)
                    return cookable;
            }
            return null;
        }

        private ShipItemSoup FindSoupByInstanceId(int instanceId)
        {
            // Global search - supports land cooking
            foreach (var soup in Object.FindObjectsOfType<ShipItemSoup>())
            {
                var prefab = soup.GetComponent<SaveablePrefab>();
                if (prefab != null && prefab.instanceId == instanceId)
                    return soup;
            }
            return null;
        }

        private ShipItemKettle FindKettleByInstanceId(int instanceId)
        {
            // Global search - supports land cooking
            foreach (var kettle in Object.FindObjectsOfType<ShipItemKettle>())
            {
                var prefab = kettle.GetComponent<SaveablePrefab>();
                if (prefab != null && prefab.instanceId == instanceId)
                    return kettle;
            }
            return null;
        }

        #endregion

        #region Request Handlers (host-side)

        public void OnFoodPlaceOnStoveRequest(FoodPlaceOnStoveRequestPacket packet, SteamId sender)
        {
            if (!Plugin.IsHost) return;

            VerboseLogger.CookingRecv($"FoodPlaceOnStoveRequest, food={packet.FoodInstanceId}, stove={packet.StoveInstanceId}");

            // Validate food item
            if (!ItemSyncManager.Instance.ValidateItem(packet.FoodInstanceId, packet.FoodPrefabIndex, out int expectedFoodPrefab))
            {
                if (expectedFoodPrefab == -1)
                {
                    ItemSyncManager.Instance.SendItemDestroyed(packet.FoodInstanceId, sender);
                }
                else
                {
                    ItemSyncManager.Instance.SendItemResync(packet.FoodInstanceId, sender);
                }
                return;
            }

            // Validate stove item
            if (!ItemSyncManager.Instance.ValidateItem(packet.StoveInstanceId, packet.StovePrefabIndex, out int expectedStovePrefab))
            {
                if (expectedStovePrefab == -1)
                {
                    ItemSyncManager.Instance.SendItemDestroyed(packet.StoveInstanceId, sender);
                }
                else
                {
                    ItemSyncManager.Instance.SendItemResync(packet.StoveInstanceId, sender);
                }
                return;
            }

            var cookable = FindCookableByInstanceId(packet.FoodInstanceId);
            var stove = FindStoveByInstanceId(packet.StoveInstanceId);

            if (cookable == null || stove == null)
            {
                VerboseLogger.CookingEvent($"FoodPlaceOnStove rejected: food or stove not found");
                return;
            }

            // Idempotency guard: every guest's local physics echoes the host's own placement (and
            // re-sends while its local copy never enters a trigger), and vanilla InsertIntoCookTrigger
            // overwrites currentTrigger WITHOUT clearing the old slot's currentFood - so each duplicate
            // request permanently ate another cook slot (phantom-slot leak, "no free slot" with one item).
            if (cookable.isInTrigger())
            {
                var currentTrigger = cookable.GetCurrentCookTrigger();
                bool onThisStove = false;
                foreach (var slot in stove.slots)
                {
                    if (slot == currentTrigger) { onThisStove = true; break; }
                }
                if (onThisStove)
                {
                    VerboseLogger.CookingEvent($"FoodPlaceOnStove ignored: food {packet.FoodInstanceId} already on stove {packet.StoveInstanceId} (duplicate request)");
                    return;
                }
                // On a different stove - free that slot properly before re-inserting here
                cookable.TakeOutOfCooker();
            }

            // Find free slot, self-healing phantom slots leaked by pre-fix duplicates: a slot whose
            // currentFood no longer points back at it is unreachable by TakeOutOfCooker and would
            // otherwise stay occupied forever (and multi-cook that food in ShipItemStove.AddHeat).
            StoveCookTrigger freeSlot = null;
            foreach (var slot in stove.slots)
            {
                if (slot.currentFood != null && slot.currentFood.GetCurrentCookTrigger() != slot)
                {
                    VerboseLogger.CookingEvent($"FoodPlaceOnStove: cleared phantom slot on stove {packet.StoveInstanceId} (stale food ref)");
                    slot.currentFood = null;
                }
                if (freeSlot == null && slot.currentFood == null)
                    freeSlot = slot;
            }

            if (freeSlot == null)
            {
                VerboseLogger.CookingEvent($"FoodPlaceOnStove rejected: no free slot");
                return;
            }

            // Execute on host
            cookable.InsertIntoCookTrigger(freeSlot);
            VerboseLogger.CookingEvent($"FoodPlaceOnStove executed, food={packet.FoodInstanceId}, stove={packet.StoveInstanceId}");
        }

        public void OnFoodRemoveFromStoveRequest(FoodRemoveFromStoveRequestPacket packet, SteamId sender)
        {
            if (!Plugin.IsHost) return;

            VerboseLogger.CookingRecv($"FoodRemoveFromStoveRequest, food={packet.FoodInstanceId}");

            // Validate food item
            if (!ItemSyncManager.Instance.ValidateItem(packet.FoodInstanceId, packet.FoodPrefabIndex, out int expectedPrefab))
            {
                if (expectedPrefab == -1)
                {
                    ItemSyncManager.Instance.SendItemDestroyed(packet.FoodInstanceId, sender);
                }
                else
                {
                    ItemSyncManager.Instance.SendItemResync(packet.FoodInstanceId, sender);
                }
                return;
            }

            var cookable = FindCookableByInstanceId(packet.FoodInstanceId);
            if (cookable == null || !cookable.isInTrigger())
            {
                VerboseLogger.CookingEvent($"FoodRemoveFromStove rejected: food not found or not in stove");
                return;
            }

            cookable.TakeOutOfCooker();
            VerboseLogger.CookingEvent($"FoodRemoveFromStove executed, food={packet.FoodInstanceId}");
        }

        public void OnFoodCutRequest(FoodCutRequestPacket packet, SteamId sender)
        {
            if (!Plugin.IsHost) return;

            VerboseLogger.CookingRecv($"FoodCutRequest, knife={packet.KnifeInstanceId}, food={packet.FoodInstanceId}");

            // Validate knife item
            if (!ItemSyncManager.Instance.ValidateItem(packet.KnifeInstanceId, packet.KnifePrefabIndex, out int expectedKnifePrefab))
            {
                if (expectedKnifePrefab == -1)
                {
                    ItemSyncManager.Instance.SendItemDestroyed(packet.KnifeInstanceId, sender);
                }
                else
                {
                    ItemSyncManager.Instance.SendItemResync(packet.KnifeInstanceId, sender);
                }
                return;
            }

            // Validate food item
            if (!ItemSyncManager.Instance.ValidateItem(packet.FoodInstanceId, packet.FoodPrefabIndex, out int expectedFoodPrefab))
            {
                if (expectedFoodPrefab == -1)
                {
                    ItemSyncManager.Instance.SendItemDestroyed(packet.FoodInstanceId, sender);
                }
                else
                {
                    ItemSyncManager.Instance.SendItemResync(packet.FoodInstanceId, sender);
                }
                return;
            }

            var knife = FindKnifeByInstanceId(packet.KnifeInstanceId);
            var food = FindFoodStateByInstanceId(packet.FoodInstanceId);

            if (knife == null || food == null)
            {
                VerboseLogger.CookingEvent($"FoodCut rejected: knife or food not found");
                return;
            }

            if (food.slicePrefabIndex == 0)
            {
                VerboseLogger.CookingEvent($"FoodCut rejected: food cannot be sliced");
                return;
            }

            // Execute cutting and get slice IDs
            var sliceIds = ExecuteCutFood(food);

            // Send result to guest
            var result = new FoodCutResultPacket
            {
                OriginalFoodId = packet.FoodInstanceId,
                SliceInstanceIds = sliceIds
            };

            VerboseLogger.CookingSend($"FoodCutResult, original={packet.FoodInstanceId}, slices={sliceIds.Count}");

            Plugin.NetworkManager.SendToAllReliable(PacketType.FoodCutResult, w =>
                PacketSerializer.WriteFoodCutResult(w, result));
        }

        /// <summary>
        /// Execute food cutting with proper sync. Creates slices, destroys original, syncs to guest.
        /// Called by both host direct cuts and OnFoodCutRequest.
        /// </summary>
        public List<int> ExecuteCutFood(FoodState food)
        {
            if (food == null || food.slicePrefabIndex == 0)
            {
                VerboseLogger.CookingEvent($"ExecuteCutFood: invalid food or cannot slice");
                return new List<int>();
            }

            var sliceIds = new List<int>();
            float num = -0.01f * food.slicesCount;

            // Get original food's boat reference for proper sync positioning
            var originalFoodItem = food.GetComponent<ShipItem>();
            var foodBoat = originalFoodItem?.currentActualBoat;
            var foodPrefab = food.GetComponent<SaveablePrefab>();

            VerboseLogger.CookingEvent($"ExecuteCutFood: food={foodPrefab?.instanceId}, slices={food.slicesCount}, boat={(foodBoat != null ? "yes" : "no")}");

            for (int i = 0; i < food.slicesCount; i++)
            {
                var obj = Object.Instantiate(PrefabsDirectory.instance.directory[food.slicePrefabIndex]);
                obj.transform.position = food.transform.position + food.transform.right * num;
                obj.transform.rotation = food.transform.rotation * Quaternion.Euler(0f, 90f, 0f);
                num += 0.02f;

                var sliceFood = obj.GetComponent<ShipItemFood>();
                sliceFood.sold = true;
                sliceFood.amount = food.GetComponent<ShipItemFood>().amount;

                // Copy boat reference so slice syncs with boat-relative coords
                if (foodBoat != null)
                {
                    sliceFood.currentActualBoat = foodBoat;
                }

                var sliceFoodState = obj.GetComponent<FoodState>();
                sliceFoodState.smoked = food.smoked;
                sliceFoodState.salted = food.salted;
                sliceFoodState.dried = food.dried;
                sliceFoodState.spoiled = food.spoiled;

                var slicePrefab = obj.GetComponent<SaveablePrefab>();
                slicePrefab.RegisterToSave();
                sliceIds.Add(slicePrefab.instanceId);

                // Notify item sync
                ItemSyncManager.Instance?.OnLocalItemSpawned(sliceFood);
            }

            // Destroy original
            food.GetComponent<ShipItem>().DestroyItem();

            return sliceIds;
        }

        public void OnFoodSaltRequest(FoodSaltRequestPacket packet, SteamId sender)
        {
            if (!Plugin.IsHost) return;

            VerboseLogger.CookingRecv($"FoodSaltRequest, salt={packet.SaltInstanceId}, food={packet.FoodInstanceId}");

            // Validate salt item
            if (!ItemSyncManager.Instance.ValidateItem(packet.SaltInstanceId, packet.SaltPrefabIndex, out int expectedSaltPrefab))
            {
                if (expectedSaltPrefab == -1)
                {
                    ItemSyncManager.Instance.SendItemDestroyed(packet.SaltInstanceId, sender);
                }
                else
                {
                    ItemSyncManager.Instance.SendItemResync(packet.SaltInstanceId, sender);
                }
                return;
            }

            // Validate food item
            if (!ItemSyncManager.Instance.ValidateItem(packet.FoodInstanceId, packet.FoodPrefabIndex, out int expectedFoodPrefab))
            {
                if (expectedFoodPrefab == -1)
                {
                    ItemSyncManager.Instance.SendItemDestroyed(packet.FoodInstanceId, sender);
                }
                else
                {
                    ItemSyncManager.Instance.SendItemResync(packet.FoodInstanceId, sender);
                }
                return;
            }

            var salt = FindSaltByInstanceId(packet.SaltInstanceId);
            var food = FindFoodStateByInstanceId(packet.FoodInstanceId);

            if (salt == null || food == null)
            {
                VerboseLogger.CookingEvent($"FoodSalt rejected: salt or food not found");
                return;
            }

            salt.SaltFood(food);
            VerboseLogger.CookingEvent($"FoodSalt executed, salt={packet.SaltInstanceId}, food={packet.FoodInstanceId}");
        }

        public void OnSoupAddFoodRequest(SoupAddFoodRequestPacket packet, SteamId sender)
        {
            if (!Plugin.IsHost) return;

            VerboseLogger.CookingRecv($"SoupAddFoodRequest, soup={packet.SoupInstanceId}, food={packet.FoodInstanceId}");

            // Validate food item
            if (!ItemSyncManager.Instance.ValidateItem(packet.FoodInstanceId, packet.FoodPrefabIndex, out int expectedFoodPrefab))
            {
                if (expectedFoodPrefab == -1)
                {
                    ItemSyncManager.Instance.SendItemDestroyed(packet.FoodInstanceId, sender);
                }
                else
                {
                    ItemSyncManager.Instance.SendItemResync(packet.FoodInstanceId, sender);
                }
                return;
            }

            // Validate soup item
            if (!ItemSyncManager.Instance.ValidateItem(packet.SoupInstanceId, packet.SoupPrefabIndex, out int expectedSoupPrefab))
            {
                if (expectedSoupPrefab == -1)
                {
                    ItemSyncManager.Instance.SendItemDestroyed(packet.SoupInstanceId, sender);
                }
                else
                {
                    ItemSyncManager.Instance.SendItemResync(packet.SoupInstanceId, sender);
                }
                return;
            }

            var soup = FindSoupByInstanceId(packet.SoupInstanceId);
            var food = FindFoodByInstanceId(packet.FoodInstanceId);

            if (soup == null || food == null)
            {
                VerboseLogger.CookingEvent($"SoupAddFood rejected: soup or food not found");
                return;
            }

            soup.InsertFood(food);
            VerboseLogger.CookingEvent($"SoupAddFood executed, soup={packet.SoupInstanceId}, food={packet.FoodInstanceId}");
        }

        public void OnSoupAddWaterRequest(AddWaterRequestPacket packet, SteamId sender)
        {
            if (!Plugin.IsHost) return;

            VerboseLogger.CookingRecv($"SoupAddWaterRequest, bottle={packet.BottleInstanceId}, container={packet.ContainerInstanceId}");

            // J: the container may be a soup pot OR a kettle (the kettle guest prefix reuses this packet)
            var soup = FindSoupByInstanceId(packet.ContainerInstanceId);
            var kettle = soup == null ? FindKettleByInstanceId(packet.ContainerInstanceId) : null;
            var bottle = FindMugByInstanceId(packet.BottleInstanceId); // ShipItemBottle includes water bottles

            if ((soup == null && kettle == null) || bottle == null)
            {
                VerboseLogger.CookingEvent($"SoupAddWater rejected: container or bottle not found");
                return;
            }

            bool containerSold = soup != null ? soup.sold : kettle.sold;
            if (!containerSold || !bottle.sold)
            {
                VerboseLogger.CookingEvent($"SoupAddWater rejected: container or bottle not sold");
                return;
            }

            if (bottle.amount != 1f)
            {
                VerboseLogger.CookingEvent($"SoupAddWater rejected: bottle not full of water (amount={bottle.amount})");
                return;
            }

            // Execute the water fill - same as game's OnItemClick logic
            if (soup != null)
            {
                bottle.health = soup.FillWater(bottle.health);
                soup.UpdateLookText();
                soup.itemRigidbodyC?.UpdateMass();
            }
            else
            {
                // Mirrors vanilla ShipItemKettle.OnItemClick bottle branch. Kettle water propagates
                // to all guests via the existing 2Hz KettleState snapshot.
                bottle.health = kettle.FillWater(bottle.health);
                kettle.UpdateLookText();
                kettle.itemRigidbodyC?.UpdateMass();
            }

            // J: broadcast the drained bottle level, or every peer (including the requester) keeps a stale
            // full bottle - the host is the item-health authority and ItemHealthChanged is event-only.
            ItemSyncManager.Instance?.OnLocalItemHealthChanged(bottle, forceSync: true);

            VerboseLogger.CookingEvent($"SoupAddWater executed, bottle={packet.BottleInstanceId}, container={packet.ContainerInstanceId}, target={(soup != null ? "soup" : "kettle")}");
        }

        public void OnKettleAddTeaRequest(KettleAddTeaRequestPacket packet, SteamId sender)
        {
            if (!Plugin.IsHost) return;

            VerboseLogger.CookingRecv($"KettleAddTeaRequest, kettle={packet.KettleInstanceId}, tea={packet.TeaInstanceId}");

            // Validate tea item
            if (!ItemSyncManager.Instance.ValidateItem(packet.TeaInstanceId, packet.TeaPrefabIndex, out int expectedTeaPrefab))
            {
                if (expectedTeaPrefab == -1)
                {
                    ItemSyncManager.Instance.SendItemDestroyed(packet.TeaInstanceId, sender);
                }
                else
                {
                    ItemSyncManager.Instance.SendItemResync(packet.TeaInstanceId, sender);
                }
                return;
            }

            // Validate kettle item
            if (!ItemSyncManager.Instance.ValidateItem(packet.KettleInstanceId, packet.KettlePrefabIndex, out int expectedKettlePrefab))
            {
                if (expectedKettlePrefab == -1)
                {
                    ItemSyncManager.Instance.SendItemDestroyed(packet.KettleInstanceId, sender);
                }
                else
                {
                    ItemSyncManager.Instance.SendItemResync(packet.KettleInstanceId, sender);
                }
                return;
            }

            var kettle = FindKettleByInstanceId(packet.KettleInstanceId);
            var tea = FindTeaByInstanceId(packet.TeaInstanceId);

            if (kettle == null || tea == null)
            {
                VerboseLogger.CookingEvent($"KettleAddTea rejected: kettle or tea not found");
                return;
            }

            kettle.InsertDrink(tea);

            // J: echo the tea item's post-insert remainder (InsertDrink drains drink.amount) so the guest's
            // copy of the leaves matches host truth; OnLocalItemHealthChanged also broadcasts amount.
            ItemSyncManager.Instance?.OnLocalItemHealthChanged(tea, forceSync: true);

            VerboseLogger.CookingEvent($"KettleAddTea executed, kettle={packet.KettleInstanceId}, tea={packet.TeaInstanceId}");
        }

        public void OnKettlePourRequest(KettlePourRequestPacket packet)
        {
            if (!Plugin.IsHost) return;

            VerboseLogger.CookingRecv($"KettlePourRequest, kettle={packet.KettleInstanceId}, mug={packet.MugInstanceId}");

            var kettle = FindKettleByInstanceId(packet.KettleInstanceId);
            var mug = FindMugByInstanceId(packet.MugInstanceId);

            if (kettle == null || mug == null)
            {
                VerboseLogger.CookingEvent($"KettlePour rejected: kettle or mug not found");
                return;
            }

            kettle.PourTea(mug);

            // J: echo the mug's new health + liquid amount/type (PourTea sets mug.amount to the tea type or
            // water and adds health) so all peers see the authoritative pour result immediately.
            ItemSyncManager.Instance?.OnLocalItemHealthChanged(mug, forceSync: true);

            VerboseLogger.CookingEvent($"KettlePour executed, kettle={packet.KettleInstanceId}, mug={packet.MugInstanceId}");
        }

        public void OnFoodCutResultReceived(FoodCutResultPacket packet)
        {
            if (Plugin.IsHost) return; // Host already processed

            VerboseLogger.CookingRecv($"FoodCutResult, original={packet.OriginalFoodId}, slices={packet.SliceInstanceIds.Count}");

            // Guest receives this after host cut food
            // Items are spawned via ItemSyncManager, nothing extra needed here
            VerboseLogger.CookingEvent($"FoodCutResult received, slices will spawn via ItemSync");
        }

        /// <summary>
        /// Called when receiving FuelInserted packet - fuel was inserted on other player's machine.
        /// </summary>
        public void OnFuelInsertedReceived(FuelInsertedPacket packet, Steamworks.SteamId sender = default)
        {
            VerboseLogger.CookingRecv($"FuelInserted, fuel={packet.FuelInstanceId}, stove={packet.StoveInstanceId}");

            // CK1 star-relay: forward a guest's fuel insertion to the OTHER guests, or at 3+ players their stove
            // never lights (the 2Hz CookingState broadcast carries no stove fuel/fire state). Relay BEFORE the
            // lookups so it still forwards even if this host can't resolve the item locally. The InsertFuel apply
            // below is echo-guarded by IsApplyingRemoteState, so this does not loop.
            if (Plugin.IsHost)
                Plugin.NetworkManager.SendToAllExcept(sender, PacketType.FuelInsertedEvent, w =>
                    PacketSerializer.WriteFuelInserted(w, packet));

            // Find the fuel item
            var fuelItem = ItemSyncManager.FindItemByInstanceId(packet.FuelInstanceId);
            if (fuelItem == null)
            {
                Plugin.Log.LogWarning($"OnFuelInserted: fuel {packet.FuelInstanceId} not found");
                return;
            }

            // Find the stove
            var stove = FindStoveByInstanceId(packet.StoveInstanceId);
            if (stove == null)
            {
                Plugin.Log.LogWarning($"OnFuelInserted: stove {packet.StoveInstanceId} not found");
                return;
            }

            // Check if already inserted (idempotent)
            var stoveFuel = fuelItem.GetComponent<StoveFuel>();
            if (stoveFuel != null && stoveFuel.inserted)
            {
                VerboseLogger.CookingApply($"Fuel {packet.FuelInstanceId} already inserted, skipping");
                return;
            }

            // Get the fuel trigger from stove
            var fuelTrigger = stove.GetComponentInChildren<StoveFuelTrigger>();
            if (fuelTrigger == null)
            {
                Plugin.Log.LogWarning($"OnFuelInserted: stove {packet.StoveInstanceId} has no StoveFuelTrigger");
                return;
            }

            // Apply remote state - prevents echo
            IsApplyingRemoteState = true;
            try
            {
                fuelTrigger.InsertFuel(fuelItem);
                VerboseLogger.CookingApply($"Inserted fuel {packet.FuelInstanceId} into stove {packet.StoveInstanceId}");
            }
            finally
            {
                IsApplyingRemoteState = false;
            }
        }

        #endregion

        #region Additional Lookups

        private ShipItemStove FindStoveByInstanceId(int instanceId)
        {
            // Global search - supports land cooking
            foreach (var stove in Object.FindObjectsOfType<ShipItemStove>())
            {
                var prefab = stove.GetComponent<SaveablePrefab>();
                if (prefab != null && prefab.instanceId == instanceId)
                    return stove;
            }
            return null;
        }

        private ShipItemKnife FindKnifeByInstanceId(int instanceId)
        {
            // Knife might be held, search all
            foreach (var knife in Object.FindObjectsOfType<ShipItemKnife>())
            {
                var prefab = knife.GetComponent<SaveablePrefab>();
                if (prefab != null && prefab.instanceId == instanceId)
                    return knife;
            }
            return null;
        }

        private FoodState FindFoodStateByInstanceId(int instanceId)
        {
            foreach (var food in Object.FindObjectsOfType<FoodState>())
            {
                var prefab = food.GetComponent<SaveablePrefab>();
                if (prefab != null && prefab.instanceId == instanceId)
                    return food;
            }
            return null;
        }

        private ShipItemFood FindFoodByInstanceId(int instanceId)
        {
            foreach (var food in Object.FindObjectsOfType<ShipItemFood>())
            {
                var prefab = food.GetComponent<SaveablePrefab>();
                if (prefab != null && prefab.instanceId == instanceId)
                    return food;
            }
            return null;
        }

        private ShipItemSalt FindSaltByInstanceId(int instanceId)
        {
            foreach (var salt in Object.FindObjectsOfType<ShipItemSalt>())
            {
                var prefab = salt.GetComponent<SaveablePrefab>();
                if (prefab != null && prefab.instanceId == instanceId)
                    return salt;
            }
            return null;
        }

        private ShipItemTea FindTeaByInstanceId(int instanceId)
        {
            foreach (var tea in Object.FindObjectsOfType<ShipItemTea>())
            {
                var prefab = tea.GetComponent<SaveablePrefab>();
                if (prefab != null && prefab.instanceId == instanceId)
                    return tea;
            }
            return null;
        }

        private ShipItemBottle FindMugByInstanceId(int instanceId)
        {
            foreach (var bottle in Object.FindObjectsOfType<ShipItemBottle>())
            {
                var prefab = bottle.GetComponent<SaveablePrefab>();
                if (prefab != null && prefab.instanceId == instanceId)
                    return bottle;
            }
            return null;
        }

        #endregion

        public void Reset()
        {
            _lastSyncTime = 0f;
            Patches.CookingPatches.ResetRequestCooldowns(); // stale instanceId keys must not leak into the next lobby
        }
    }
}
