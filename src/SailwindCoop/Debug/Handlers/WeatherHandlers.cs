using UnityEngine;
using Random = UnityEngine.Random;

namespace SailwindCoop.Debug.Handlers
{
    public static class WeatherHandlers
    {
        public static void Register(CommandRegistry registry)
        {
            registry.Register("wind.set", "Set wind vector (x, y, z)", args =>
            {
                if (args.Length < 3)
                    return "Usage: wind.set <x> <y> <z>";

                var wind = new Vector3(
                    float.Parse(args[0]),
                    float.Parse(args[1]),
                    float.Parse(args[2])
                );

                Wind.currentWind = wind;
                return $"wind={ReflectionEngine.FormatValue(Wind.currentWind)}";
            });

            registry.Register("wind.speed", "Set wind speed, keep direction", args =>
            {
                if (args.Length < 1)
                    return "Usage: wind.speed <magnitude>";

                float speed = float.Parse(args[0]);
                Vector3 dir = Wind.currentWind.normalized;
                if (dir == Vector3.zero)
                    dir = Vector3.forward;

                Wind.currentWind = dir * speed;
                return $"wind={ReflectionEngine.FormatValue(Wind.currentWind)}, speed={Wind.currentWind.magnitude:F1}";
            });

            registry.Register("wind.calm", "Set wind to zero", _ =>
            {
                Wind.currentWind = Vector3.zero;
                return "wind=(0, 0, 0)";
            });

            registry.Register("wind.strong", "Set strong wind (15 m/s)", _ =>
            {
                Vector3 dir = Wind.currentWind.normalized;
                if (dir == Vector3.zero)
                    dir = new Vector3(1, 0, 1).normalized;

                Wind.currentWind = dir * 15f;
                return $"wind={ReflectionEngine.FormatValue(Wind.currentWind)}";
            });

            registry.Register("wind.gale", "Set gale force wind (20 m/s)", _ =>
            {
                Vector3 dir = Wind.currentWind.normalized;
                if (dir == Vector3.zero)
                    dir = new Vector3(1, 0, 1).normalized;

                Wind.currentWind = dir * 20f;
                return $"wind={ReflectionEngine.FormatValue(Wind.currentWind)}";
            });

            registry.Register("storm.spawn", "Move nearest storm to distance from player", args =>
            {
                if (args.Length < 1)
                    return "Usage: storm.spawn <distance>";

                float distance = float.Parse(args[0]);

                var storms = WeatherStorms.instance;
                if (storms == null)
                    return "Error: WeatherStorms not found";

                // Get player position
                var player = Refs.charController;
                if (player == null)
                    return "Error: Player not found";

                Vector3 playerPos = player.transform.position;

                // Get regionStorms array via reflection (same as WeatherSyncManager)
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

                // Move storm to specified distance in a random direction
                float angle = Random.Range(0f, 360f) * Mathf.Deg2Rad;
                Vector3 offset = new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle)) * distance;
                Vector3 stormPos = playerPos + offset;

                targetStorm.transform.position = stormPos;

                return $"Storm moved to {distance}m away at {ReflectionEngine.FormatValue(stormPos)}";
            });

            registry.Register("storm.info", "Show nearest storm distance", _ =>
            {
                float dist = WeatherStorms.currentStormDistance;
                return $"Nearest storm: {dist:F0}m";
            });

            registry.Register("weather.set", "Set weather state (clear, cloudy, rain, storm)", args =>
            {
                if (args.Length < 1)
                    return "Usage: weather.set <clear|cloudy|rain|storm>";

                string state = args[0].ToLower();

                var weather = Weather.instance;
                if (weather == null)
                    return "Error: Weather instance not found";

                var region = RegionBlender.instance?.blendedRegion;
                if (region == null)
                    return "Error: No current region";

                WeatherSet targetSet;
                switch (state)
                {
                    case "clear":
                        targetSet = region.clearWeather;
                        break;
                    case "cloudy":
                        targetSet = region.cloudyWeather;
                        break;
                    case "rain":
                        targetSet = region.rainWeather;
                        break;
                    case "storm":
                        targetSet = region.stormWeather;
                        break;
                    default:
                        return $"Unknown weather state '{state}'. Use: clear, cloudy, rain, storm";
                }

                if (targetSet == null)
                    return $"Error: Region has no {state} weather set";

                weather.ChangeWeather(targetSet);
                return $"Weather changing to: {state}";
            });
        }
    }
}
