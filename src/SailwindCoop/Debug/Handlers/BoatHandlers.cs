using UnityEngine;
using SailwindCoop.Sync;

namespace SailwindCoop.Debug.Handlers
{
    public static class BoatHandlers
    {
        public static void Register(CommandRegistry registry)
        {
            registry.Register("boat.info", "Show current boat info", _ =>
            {
                var boat = BoatUtility.GetCurrentBoat();
                if (boat == null)
                    return "Not on a boat";

                var pos = boat.transform.position;
                var rb = boat.GetComponent<Rigidbody>();

                return $"Boat: {boat.name}, pos={ReflectionEngine.FormatValue(pos)}, kinematic={rb?.isKinematic}";
            });

            registry.Register("boat.teleport", "Teleport boat to (x, y, z)", args =>
            {
                if (args.Length < 3)
                    return "Usage: boat.teleport <x> <y> <z>";

                var boat = BoatUtility.GetCurrentBoat();
                if (boat == null)
                    return "Not on a boat";

                Vector3 targetPos = new Vector3(
                    float.Parse(args[0]),
                    float.Parse(args[1]),
                    float.Parse(args[2])
                );

                // Unmoor first
                var mooringRopes = boat.GetComponent<BoatMooringRopes>();
                if (mooringRopes != null)
                    mooringRopes.UnmoorAllRopes();

                // Set kinematic during teleport
                var rb = boat.GetComponent<Rigidbody>();
                bool wasKinematic = rb?.isKinematic ?? true;
                if (rb != null)
                {
                    rb.isKinematic = true;
                    rb.velocity = Vector3.zero;
                    rb.angularVelocity = Vector3.zero;
                }

                // Apply position (accounting for FOM)
                boat.transform.position = targetPos;

                return $"Teleported to {ReflectionEngine.FormatValue(targetPos)}";
            });

            registry.Register("boat.teleport-to-storm", "Teleport boat near nearest storm", args =>
            {
                var boat = BoatUtility.GetCurrentBoat();
                if (boat == null)
                    return "Not on a boat";

                float safeDistance = 200f; // Stay outside storm but close
                if (args.Length > 0)
                    safeDistance = float.Parse(args[0]);

                // Get storm position (same pattern as storm.spawn in WeatherHandlers)
                var storms = WeatherStorms.instance;
                if (storms == null)
                    return "Error: WeatherStorms not found";

                var regionStormsField = typeof(WeatherStorms).GetField("regionStorms",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (regionStormsField == null)
                    return "Error: regionStorms field not found";

                var regionStorms = regionStormsField.GetValue(storms) as WanderingStorm[][];
                if (regionStorms == null || regionStorms.Length == 0)
                    return "Error: No regionStorms available";

                // Find first storm across all regions
                WanderingStorm targetStorm = null;
                foreach (var stormArray in regionStorms)
                {
                    if (stormArray != null && stormArray.Length > 0 && stormArray[0] != null)
                    {
                        targetStorm = stormArray[0];
                        break;
                    }
                }

                if (targetStorm == null)
                    return "Error: No storms available in any region";

                Vector3 stormPos = targetStorm.transform.position;

                // Position boat at safe distance from storm center
                Vector3 dir = (boat.transform.position - stormPos).normalized;
                if (dir == Vector3.zero)
                    dir = Vector3.forward;

                Vector3 targetPos = stormPos + dir * safeDistance;
                targetPos.y = boat.transform.position.y; // Keep current height

                // Unmoor
                var mooringRopes = boat.GetComponent<BoatMooringRopes>();
                if (mooringRopes != null)
                    mooringRopes.UnmoorAllRopes();

                // Teleport
                var rb = boat.GetComponent<Rigidbody>();
                if (rb != null)
                {
                    rb.isKinematic = true;
                    rb.velocity = Vector3.zero;
                    rb.angularVelocity = Vector3.zero;
                }

                boat.transform.position = targetPos;

                return $"Teleported {safeDistance}m from storm at {ReflectionEngine.FormatValue(targetPos)}";
            });

            registry.Register("boat.kinematic", "Toggle boat kinematic state", args =>
            {
                var boat = BoatUtility.GetCurrentBoat();
                if (boat == null)
                    return "Not on a boat";

                var rb = boat.GetComponent<Rigidbody>();
                if (rb == null)
                    return "Boat has no Rigidbody";

                if (args.Length > 0)
                    rb.isKinematic = bool.Parse(args[0]);
                else
                    rb.isKinematic = !rb.isKinematic;

                return $"isKinematic={rb.isKinematic}";
            });
        }
    }
}
