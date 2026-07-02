using HarmonyLib;
using UnityEngine;

namespace SailwindCoop.Patches
{
    /// <summary>
    /// Player-related patches.
    /// Note: Position sync moved to PlayerSyncManager (timer-based Update loop)
    /// so the remote capsule does not stay in place during helm/capstan/bed interaction.
    /// CharacterController.Move hook was unreliable - doesn't fire when player is stationary.
    /// </summary>
    [HarmonyPatch]
    public static class PlayerPatches
    {
        // Position sync is now handled by PlayerSyncManager.Update() at 20Hz
        // This ensures positions are sent even when player is stationary (using helm, capstan, bed)
    }
}
