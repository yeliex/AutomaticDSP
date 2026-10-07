using System;
using System.Globalization;
using AutomaticDSP.Serialization;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace AutomaticDSP.Tasks
{
    internal sealed partial class TaskCommandExecutor
    {
        private static int InventoryCount(int itemId)
        {
            if (itemId <= 0)
            {
                return 0;
            }

            return GameMain.mainPlayer?.package?.GetItemCount(itemId) ?? 0;
        }

        private static bool TryGetVector(CommandState command, string name, out Vector3 value, out string errorMessage)
        {
            value = Vector3.zero;
            if (!TryGetToken(command, name, out var token))
            {
                errorMessage = $"{command.Type} requires {name}.";
                return false;
            }

            return TryReadVectorToken(token, name, out value, out errorMessage);
        }

        private static bool TryGetVectorAny(CommandState command, string[] names, out Vector3 value, out string errorMessage)
        {
            foreach (var name in names)
            {
                if (TryGetToken(command, name, out var token))
                {
                    return TryReadVectorToken(token, name, out value, out errorMessage);
                }
            }

            value = Vector3.zero;
            errorMessage = $"{command.Type} requires {names[0]}.";
            return false;
        }

        private static bool TryReadVectorToken(JToken token, string name, out Vector3 value, out string errorMessage)
        {
            value = Vector3.zero;
            JToken x = null, y = null, z = null;
            if (token is JArray array && array.Count == 3)
            {
                x = array[0];
                y = array[1];
                z = array[2];
            }
            else if (token is JObject obj)
            {
                obj.TryGetValue("x", StringComparison.OrdinalIgnoreCase, out x);
                obj.TryGetValue("y", StringComparison.OrdinalIgnoreCase, out y);
                obj.TryGetValue("z", StringComparison.OrdinalIgnoreCase, out z);
            }

            if (TryReadVectorComponent(x, out var vx) &&
                TryReadVectorComponent(y, out var vy) &&
                TryReadVectorComponent(z, out var vz))
            {
                value = new Vector3(vx, vy, vz);
                errorMessage = null;
                return true;
            }

            errorMessage = $"{name} must be an array [x,y,z] or object {{x,y,z}} with finite numeric coordinates within the float range.";
            return false;
        }

        private static bool TryReadVectorComponent(JToken token, out float value)
        {
            value = 0;
            return token != null && (token.Type == JTokenType.Integer || token.Type == JTokenType.Float) &&
                float.TryParse(token.ToString(Newtonsoft.Json.Formatting.None), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out value) && !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static bool TryGetString(CommandState command, string name, out string value)
        {
            value = null;
            if (!TryGetToken(command, name, out var token))
            {
                return false;
            }

            value = token.Value<string>();
            return !string.IsNullOrWhiteSpace(value);
        }

        private static string GetString(CommandState command, string name, string defaultValue)
        {
            return TryGetString(command, name, out var value) ? value : defaultValue;
        }

        private static bool TryGetInt(CommandState command, string name, out int value)
        {
            value = 0;
            if (!TryGetToken(command, name, out var token))
            {
                return false;
            }

            value = token.Value<int>();
            return true;
        }

        private static int GetInt(CommandState command, string name, int defaultValue)
        {
            return TryGetInt(command, name, out var value) ? value : defaultValue;
        }

        private static double GetDouble(CommandState command, string name, double defaultValue)
        {
            if (!TryGetToken(command, name, out var token))
            {
                return defaultValue;
            }

            return token.Value<double>();
        }

        private static bool GetBool(CommandState command, string name, bool defaultValue)
        {
            if (!TryGetToken(command, name, out var token))
            {
                return defaultValue;
            }

            return token.Value<bool>();
        }

        private static bool TryGetToken(CommandState command, string name, out JToken value)
        {
            value = null;
            return command.Request.ExtensionData != null &&
                command.Request.ExtensionData.TryGetValue(name, out value);
        }

        private static JsonObject Vector(Vector3 vector)
        {
            return new JsonObject
            {
                ["x"] = vector.x,
                ["y"] = vector.y,
                ["z"] = vector.z
            };
        }

        private static long? CurrentGameTick()
        {
            try
            {
                return GameMain.gameTick;
            }
            catch
            {
                return null;
            }
        }
    }
}
