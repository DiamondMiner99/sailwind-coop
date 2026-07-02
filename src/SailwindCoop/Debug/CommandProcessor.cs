using System;
using System.IO;
using System.Linq;
using BepInEx.Logging;
using SailwindCoop.Debug.Handlers;

namespace SailwindCoop.Debug
{
    public class CommandProcessor
    {
        private readonly ManualLogSource _log;
        private readonly string _commandsPath;
        private readonly string _responsePath;
        private readonly CommandRegistry _registry;

        public CommandProcessor(ManualLogSource log)
        {
            _log = log;

            // Cross-platform path
            string homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string sailwindDir = Path.Combine(homeDir, ".sailwind-coop");

            // Ensure directory exists
            if (!Directory.Exists(sailwindDir))
            {
                Directory.CreateDirectory(sailwindDir);
            }

            _commandsPath = Path.Combine(sailwindDir, "commands.txt");
            _responsePath = Path.Combine(sailwindDir, "response.txt");

            // Clear stale commands on startup
            if (File.Exists(_commandsPath))
            {
                File.Delete(_commandsPath);
            }

            _registry = new CommandRegistry();
            TimeHandlers.Register(_registry);
            WeatherHandlers.Register(_registry);
            BoatHandlers.Register(_registry);
            PlayerHandlers.Register(_registry);

            _log.LogInfo($"[CMD] CommandProcessor initialized. Commands: {_commandsPath}");
        }

        // Expose registry for handler registration
        public CommandRegistry Registry => _registry;

        public void Update()
        {
            try
            {
                if (!File.Exists(_commandsPath))
                    return;

                string content = File.ReadAllText(_commandsPath);
                if (string.IsNullOrWhiteSpace(content))
                    return;

                // Clear commands file immediately to prevent re-execution
                File.WriteAllText(_commandsPath, "");

                // Process each line
                var responses = new System.Collections.Generic.List<string>();
                var lines = content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

                foreach (var line in lines)
                {
                    string trimmed = line.Trim();
                    if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("#"))
                        continue;

                    string result = ExecuteCommand(trimmed);
                    responses.Add(result);
                    _log.LogInfo($"[CMD] {result}");
                }

                // Write all responses
                if (responses.Count > 0)
                {
                    File.WriteAllText(_responsePath, string.Join(Environment.NewLine, responses));
                }
            }
            catch (Exception ex)
            {
                _log.LogError($"[CMD] Error processing commands: {ex.Message}");
            }
        }

        private string ExecuteCommand(string commandLine)
        {
            string[] tokens = commandLine.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0)
                return "[ERR] Empty command";

            string cmd = tokens[0].ToLowerInvariant();
            string[] args = tokens.Skip(1).ToArray();

            // Handle 'get' command
            if (cmd == "get")
            {
                if (args.Length < 1)
                    return "[ERR] get requires path argument";

                var (value, error) = ReflectionEngine.GetValue(args[0]);
                if (error != null)
                    return $"[ERR] get {args[0]} -> {error}";

                return $"[OK] get {args[0]} -> {ReflectionEngine.FormatValue(value)}";
            }

            // Handle 'set' command
            if (cmd == "set")
            {
                if (args.Length < 2)
                    return "[ERR] set requires path and value arguments";

                string path = args[0];
                string value = string.Join(" ", args.Skip(1));

                string error = ReflectionEngine.SetValue(path, value);
                if (error != null)
                    return $"[ERR] set {path} {value} -> {error}";

                // Read back the value to confirm
                var (newValue, _) = ReflectionEngine.GetValue(path);
                return $"[OK] set {path} -> {ReflectionEngine.FormatValue(newValue)}";
            }

            // Try handler registry
            var (result, found) = _registry.TryExecute(tokens[0], args);
            if (found)
            {
                if (result.StartsWith("Error:"))
                    return $"[ERR] {tokens[0]} -> {result}";
                return $"[OK] {tokens[0]} -> {result}";
            }

            return $"[ERR] Unknown command '{tokens[0]}'. Use 'help' for available commands.";
        }
    }
}
