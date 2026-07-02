using UnityEngine;

namespace SailwindCoop.Debug.Handlers
{
    public static class PlayerHandlers
    {
        public static void Register(CommandRegistry registry)
        {
            registry.Register("player.info", "Show player position and stats", _ =>
            {
                var player = Refs.charController;
                if (player == null)
                    return "Player not found";

                Vector3 pos = player.transform.position;
                float food = PlayerNeeds.food;
                float water = PlayerNeeds.water;
                float sleep = PlayerNeeds.sleep;
                bool god = PlayerNeeds.instance?.godMode ?? false;

                return $"pos={ReflectionEngine.FormatValue(pos)}, food={food:F0}, water={water:F0}, sleep={sleep:F0}, godMode={god}";
            });

            registry.Register("player.teleport", "Teleport player to (x, y, z)", args =>
            {
                if (args.Length < 3)
                    return "Usage: player.teleport <x> <y> <z>";

                var player = Refs.charController;
                if (player == null)
                    return "Player not found";

                Vector3 targetPos = new Vector3(
                    float.Parse(args[0]),
                    float.Parse(args[1]),
                    float.Parse(args[2])
                );

                player.transform.position = targetPos;

                return $"Teleported to {ReflectionEngine.FormatValue(targetPos)}";
            });

            registry.Register("player.godmode", "Toggle god mode", args =>
            {
                if (PlayerNeeds.instance == null)
                    return "PlayerNeeds not found";

                if (args.Length > 0)
                    PlayerNeeds.instance.godMode = bool.Parse(args[0]);
                else
                    PlayerNeeds.instance.godMode = !PlayerNeeds.instance.godMode;

                return $"godMode={PlayerNeeds.instance.godMode}";
            });

            registry.Register("player.feed", "Set food/water/sleep to 100", _ =>
            {
                PlayerNeeds.food = 100f;
                PlayerNeeds.water = 100f;
                PlayerNeeds.sleep = 100f;

                return "food=100, water=100, sleep=100";
            });

            registry.Register("player.starve", "Set food/water/sleep to 10", _ =>
            {
                PlayerNeeds.food = 10f;
                PlayerNeeds.water = 10f;
                PlayerNeeds.sleep = 10f;

                return "food=10, water=10, sleep=10";
            });
        }
    }
}
