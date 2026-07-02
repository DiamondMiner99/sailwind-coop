using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SailwindCoop.Debug
{
    public class CommandRegistry
    {
        private readonly Dictionary<string, Func<string[], string>> _handlers =
            new Dictionary<string, Func<string[], string>>(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<string, string> _descriptions =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public CommandRegistry()
        {
            // Built-in help command
            Register("help", "List all available commands", args =>
            {
                var sb = new StringBuilder();
                sb.AppendLine("Available commands:");
                sb.AppendLine();
                sb.AppendLine("  set <path> <value>  - Set field/property via reflection");
                sb.AppendLine("  get <path>          - Get field/property value");
                sb.AppendLine();

                foreach (var kvp in _descriptions.OrderBy(k => k.Key))
                {
                    sb.AppendLine($"  {kvp.Key,-25} - {kvp.Value}");
                }

                return sb.ToString();
            });
        }

        public void Register(string command, string description, Func<string[], string> handler)
        {
            _handlers[command] = handler;
            _descriptions[command] = description;
        }

        public (string result, bool found) TryExecute(string command, string[] args)
        {
            if (_handlers.TryGetValue(command, out var handler))
            {
                try
                {
                    string result = handler(args);
                    return (result, true);
                }
                catch (Exception ex)
                {
                    return ($"Error: {ex.Message}", true);
                }
            }

            return (null, false);
        }

        public IEnumerable<string> GetCommandNames() => _handlers.Keys;
    }
}
