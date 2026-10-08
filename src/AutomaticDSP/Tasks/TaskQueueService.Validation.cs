using System;
using System.Globalization;
using Newtonsoft.Json.Linq;

namespace AutomaticDSP.Tasks
{
    internal sealed partial class TaskQueueService
    {
        private static void ValidateEntityReferenceParameters(CommandState command)
        {
            if (command.Request.ExtensionData == null) return;
            foreach (var pair in command.Request.ExtensionData)
            {
                if (string.Equals(pair.Key, "entityId", StringComparison.OrdinalIgnoreCase))
                    ValidateReferenceInteger(pair.Value, "entityId", 1);
                foreach (var name in new[] { "target", "start", "end", "input", "output" })
                {
                    if (!string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!(pair.Value is JObject reference)) continue;
                    if (reference.TryGetValue("entityId", StringComparison.OrdinalIgnoreCase, out var entityId))
                        ValidateReferenceInteger(entityId, name + ".entityId", 1);
                    if (reference.TryGetValue("entityIndex", StringComparison.OrdinalIgnoreCase, out var index))
                        ValidateReferenceInteger(index, name + ".entityIndex", 0);
                    if (reference.TryGetValue("commandId", StringComparison.OrdinalIgnoreCase, out var id) &&
                        (id.Type != JTokenType.String || string.IsNullOrWhiteSpace(id.Value<string>())))
                        throw new TaskQueueException("invalid_command", name + ".commandId 必须是非空字符串。");
                }
                foreach (var name in new[] { "start", "end", "input", "output" })
                {
                    if (string.Equals(pair.Key, name + "EntityIndex", StringComparison.OrdinalIgnoreCase))
                        ValidateReferenceInteger(pair.Value, pair.Key, 0);
                    if (string.Equals(pair.Key, name + "CommandId", StringComparison.OrdinalIgnoreCase) &&
                        (pair.Value.Type != JTokenType.String || string.IsNullOrWhiteSpace(pair.Value.Value<string>())))
                        throw new TaskQueueException("invalid_command", pair.Key + " 必须是非空字符串。");
                }
            }
        }

        private static void ValidateReferenceInteger(JToken token, string name, int minimum)
        {
            if (token == null || token.Type != JTokenType.Integer ||
                !int.TryParse(token.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value < minimum)
                throw new TaskQueueException("invalid_command", name + " 必须是有效范围内的整数；命令引用请使用 target:{commandId:...}。");
        }
    }
}
