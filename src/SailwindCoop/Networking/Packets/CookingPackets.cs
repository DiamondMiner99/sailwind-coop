using System;
using System.Collections.Generic;

namespace SailwindCoop.Networking.Packets
{
    /// <summary>
    /// State of a single food item for cooking sync.
    /// </summary>
    [Serializable]
    public struct FoodCookingState
    {
        public int InstanceId;
        public float Amount;        // Cooking progress (0-2.2)
        public float CurrentHeat;   // Heat level for steam particles
        public float Spoiled;       // Spoilage (0-1)
        public float Salted;        // Salt level (0-1)
        public float Smoked;        // Smoke level (0-1)
        public float Dried;         // Dried level (0-1)
        public int StoveSlotIndex;  // -1 if not on stove, else slot index
        public int StoveInstanceId; // Stove item ID if on stove
    }

    /// <summary>
    /// State of a soup pot.
    /// </summary>
    [Serializable]
    public struct SoupState
    {
        public int InstanceId;
        public float CurrentWater;
        public float CurrentEnergy;
        public float CurrentUncookedEnergy;
        public float CurrentSpoiled;
        public float CurrentVitamins;
        public float CurrentProtein;
        public float CurrentSalted;
        public float CurrentHeat;
    }

    /// <summary>
    /// State of a kettle.
    /// </summary>
    [Serializable]
    public struct KettleState
    {
        public int InstanceId;
        public float CurrentWater;
        public float CurrentTeaAmount;
        public float CurrentCookedTeaAmount;
        public int CurrentTeaType;   // LiquidType enum cast to int
        public float CurrentHeat;
    }

    /// <summary>
    /// Full cooking state packet (2Hz from host).
    /// </summary>
    [Serializable]
    public struct CookingStatePacket
    {
        public List<FoodCookingState> Foods;
        public List<SoupState> Soups;
        public List<KettleState> Kettles;
    }

    /// <summary>
    /// Request to place food on stove.
    /// </summary>
    [Serializable]
    public struct FoodPlaceOnStoveRequestPacket
    {
        public int FoodInstanceId;
        public int FoodPrefabIndex;
        public int StoveInstanceId;
        public int StovePrefabIndex;
    }

    /// <summary>
    /// Request to remove food from stove.
    /// </summary>
    [Serializable]
    public struct FoodRemoveFromStoveRequestPacket
    {
        public int FoodInstanceId;
        public int FoodPrefabIndex;
    }

    /// <summary>
    /// Request to cut food.
    /// </summary>
    [Serializable]
    public struct FoodCutRequestPacket
    {
        public int KnifeInstanceId;
        public int KnifePrefabIndex;
        public int FoodInstanceId;
        public int FoodPrefabIndex;
    }

    /// <summary>
    /// Result of cutting food (slice IDs).
    /// </summary>
    [Serializable]
    public struct FoodCutResultPacket
    {
        public int OriginalFoodId;
        public List<int> SliceInstanceIds;
    }

    /// <summary>
    /// Request to salt food.
    /// </summary>
    [Serializable]
    public struct FoodSaltRequestPacket
    {
        public int SaltInstanceId;
        public int SaltPrefabIndex;
        public int FoodInstanceId;
        public int FoodPrefabIndex;
    }

    /// <summary>
    /// Request to add food to soup.
    /// </summary>
    [Serializable]
    public struct SoupAddFoodRequestPacket
    {
        public int FoodInstanceId;
        public int FoodPrefabIndex;
        public int SoupInstanceId;
        public int SoupPrefabIndex;
    }

    /// <summary>
    /// Request to add water to soup or kettle.
    /// </summary>
    [Serializable]
    public struct AddWaterRequestPacket
    {
        public int BottleInstanceId;
        public int ContainerInstanceId;  // Soup or Kettle
    }

    /// <summary>
    /// Request to add tea to kettle.
    /// </summary>
    [Serializable]
    public struct KettleAddTeaRequestPacket
    {
        public int TeaInstanceId;
        public int TeaPrefabIndex;
        public int KettleInstanceId;
        public int KettlePrefabIndex;
    }

    /// <summary>
    /// Request to pour tea from kettle to mug.
    /// </summary>
    [Serializable]
    public struct KettlePourRequestPacket
    {
        public int KettleInstanceId;
        public int MugInstanceId;
    }

    /// <summary>
    /// Event: fuel was inserted into stove.
    /// Sent when either player inserts fuel locally.
    /// </summary>
    [Serializable]
    public struct FuelInsertedPacket
    {
        public int FuelInstanceId;
        public int StoveInstanceId;
    }
}
