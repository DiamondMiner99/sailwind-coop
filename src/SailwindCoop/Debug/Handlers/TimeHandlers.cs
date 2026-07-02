namespace SailwindCoop.Debug.Handlers
{
    public static class TimeHandlers
    {
        public static void Register(CommandRegistry registry)
        {
            registry.Register("time.set", "Set time of day (0-24)", args =>
            {
                if (args.Length < 1)
                    return "Usage: time.set <hour>";

                float hour = float.Parse(args[0]);
                Sun.sun.globalTime = hour;
                return $"globalTime={Sun.sun.globalTime:F1}";
            });

            registry.Register("time.advance", "Advance time by hours", args =>
            {
                if (args.Length < 1)
                    return "Usage: time.advance <hours>";

                float hours = float.Parse(args[0]);
                Sun.sun.globalTime += hours;
                return $"globalTime={Sun.sun.globalTime:F1}";
            });

            registry.Register("time.dawn", "Set time to dawn (6:00)", _ =>
            {
                Sun.sun.globalTime = 6f;
                return "globalTime=6.0";
            });

            registry.Register("time.noon", "Set time to noon (12:00)", _ =>
            {
                Sun.sun.globalTime = 12f;
                return "globalTime=12.0";
            });

            registry.Register("time.dusk", "Set time to dusk (18:00)", _ =>
            {
                Sun.sun.globalTime = 18f;
                return "globalTime=18.0";
            });

            registry.Register("time.midnight", "Set time to midnight (0:00)", _ =>
            {
                Sun.sun.globalTime = 0f;
                return "globalTime=0.0";
            });

            registry.Register("time.scale", "Set timescale multiplier", args =>
            {
                if (args.Length < 1)
                    return "Usage: time.scale <multiplier>";

                float scale = float.Parse(args[0]);
                Sun.sun.timescale = scale;
                return $"timescale={Sun.sun.timescale:F1}";
            });
        }
    }
}
