using System;

namespace SailwindCoop.Networking.Packets
{
    [Flags]
    public enum ActivityFlags : byte
    {
        None = 0,
        Running = 1 << 0,
        Swimming = 1 << 1,
        Pumping = 1 << 2,
        Smoking = 1 << 3
    }

    public enum TobaccoType : byte
    {
        None = 0,
        Cigarette = 1,
        Cigar = 2,
        Pipe = 3,
        Hookah = 4
    }

    [Serializable]
    public struct SurvivalStatsPacket
    {
        public float Food;
        public float Water;
        public float Sleep;
        public float FoodDebt;
        public float SleepDebt;
        public float Alcohol;
        public float Vitamins;
        public float Protein;
    }

    [Serializable]
    public struct ActivityStatePacket
    {
        public ActivityFlags Flags;
        public TobaccoType TobaccoType;
        public float MovementSqrMagnitude;  // Player velocity squared for drain calculation
        public float PumpIntensity;          // Bilge pump currentInput (0-1)
    }

    [Serializable]
    public struct ConsumptionDeltaPacket
    {
        public float DeltaFood;
        public float DeltaWater;
        public float DeltaSleep;
        public float DeltaFoodDebt;
        public float DeltaSleepDebt;
        public float DeltaAlcohol;
        public float DeltaVitamins;
        public float DeltaProtein;
    }
}
