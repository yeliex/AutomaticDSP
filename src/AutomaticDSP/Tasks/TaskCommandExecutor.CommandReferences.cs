using System;
using System.Collections.Generic;
using AutomaticDSP.Serialization;
using Newtonsoft.Json.Linq;
using static AutomaticDSP.Tasks.TaskStatusNames;

namespace AutomaticDSP.Tasks
{
    internal sealed partial class TaskCommandExecutor
    {
        public bool AreEntityReferencesReady(TaskState task, CommandState command, DateTimeOffset now)
        {
            if (now < command.NextEntityReferenceCheck) return false;
            var references = new List<KeyValuePair<string, int>>();
            foreach (var name in new[] { "target", "start", "end", "input", "output" })
            {
                if (TryGetToken(command, name, out var token) && token is JObject obj &&
                    obj.TryGetValue("commandId", StringComparison.OrdinalIgnoreCase, out var id))
                    references.Add(new KeyValuePair<string, int>(id.Value<string>(),
                        obj.TryGetValue("entityIndex", StringComparison.OrdinalIgnoreCase, out var index) ? index.Value<int>() : 0));
            }
            foreach (var name in new[] { "start", "end", "input", "output" })
                if (TryGetToken(command, name + "CommandId", out var id))
                    references.Add(new KeyValuePair<string, int>(id.Value<string>(), GetInt(command, name + "EntityIndex", 0)));
            foreach (var reference in references)
            {
                var source = task.Commands.Find(c => string.Equals(c.Id, reference.Key, StringComparison.OrdinalIgnoreCase));
                if (source?.BuildTargets == null) continue;
                TryResolveBuildTarget(source, reference.Value, out _, out var pending);
                if (!pending) continue;
                command.Phase = "waitingConstruction";
                command.NextEntityReferenceCheck = now.AddSeconds(1);
                return false;
            }
            return true;
        }

        private static bool TryGetTargetEntityId(TaskState task, CommandState command, out int entityId, out string errorMessage)
        {
            if (TryGetInt(command, "entityId", out entityId))
            {
                errorMessage = null;
                return true;
            }

            if (!TryGetToken(command, "target", out var target) || !(target is JObject targetObject))
            {
                errorMessage = $"{command.Type} requires entityId or target.entityId.";
                return false;
            }

            if (targetObject.TryGetValue("entityId", StringComparison.OrdinalIgnoreCase, out var entityToken))
            {
                entityId = entityToken.Value<int>();
                errorMessage = null;
                return true;
            }

            if (targetObject.TryGetValue("commandId", StringComparison.OrdinalIgnoreCase, out var commandIdToken))
            {
                var sourceCommandId = commandIdToken.Value<string>();
                if (string.IsNullOrWhiteSpace(sourceCommandId))
                {
                    errorMessage = "target.commandId must be a non-empty string.";
                    return false;
                }

                var entityIndex = 0;
                if (targetObject.TryGetValue("entityIndex", StringComparison.OrdinalIgnoreCase, out var entityIndexToken))
                {
                    entityIndex = entityIndexToken.Value<int>();
                }

                return TryResolveCommandEntityId(task, sourceCommandId, entityIndex, out entityId, out errorMessage);
            }

            errorMessage = $"{command.Type} target must include entityId or commandId.";
            return false;
        }

        private static bool TryGetEntityIdFromCommandResult(CommandState command, out int entityId)
        {
            return TryGetEntityIdFromCommandResult(command, 0, allowEntityList: false, out entityId);
        }

        private static bool TryResolveCommandEntityId(
            TaskState task,
            string commandId,
            int entityIndex,
            out int entityId,
            out string errorMessage)
        {
            entityId = 0;
            errorMessage = null;
            if (string.IsNullOrWhiteSpace(commandId))
            {
                errorMessage = "commandId must be a non-empty string.";
                return false;
            }

            if (entityIndex < 0)
            {
                errorMessage = "entityIndex must be zero or positive.";
                return false;
            }

            foreach (var sourceCommand in task.Commands)
            {
                if (!string.Equals(sourceCommand.Id, commandId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (sourceCommand.Status != CommandSucceeded)
                {
                    errorMessage = $"commandId has not succeeded: {commandId}";
                    return false;
                }

                if (sourceCommand.BuildTargets != null)
                {
                    if (sourceCommand.BuildPlanetId != GameMain.localPlanet?.id)
                    {
                        errorMessage = "引用的建筑不在当前行星。";
                        return false;
                    }
                    if (TryResolveBuildTarget(sourceCommand, entityIndex, out entityId, out _)) return true;
                    errorMessage = "引用的预建尚未落成或对应建筑已不存在。";
                    return false;
                }

                if (TryGetEntityIdFromCommandResult(sourceCommand, entityIndex, allowEntityList: true, out entityId))
                {
                    return true;
                }

                errorMessage = $"commandId result does not include the requested entity: {commandId}[{entityIndex}]";
                return false;
            }

            errorMessage = $"commandId was not found in this task: {commandId}";
            return false;
        }

        private static bool TryGetEntityIdFromCommandResult(
            CommandState command,
            int entityIndex,
            bool allowEntityList,
            out int entityId)
        {
            entityId = 0;
            if (!(command.Result is JsonObject jsonObject))
            {
                return false;
            }

            if (entityIndex == 0 &&
                jsonObject.TryGetValue("entityId", out var value) &&
                value is int intValue)
            {
                entityId = intValue;
                return entityId > 0;
            }

            if (!allowEntityList || !jsonObject.TryGetValue("entityIds", out var listValue))
            {
                return false;
            }

            if (listValue is IList<int> intList &&
                entityIndex < intList.Count)
            {
                entityId = intList[entityIndex];
                return entityId > 0;
            }

            if (listValue is IList<object> objectList &&
                entityIndex < objectList.Count &&
                objectList[entityIndex] is int objectIntValue)
            {
                entityId = objectIntValue;
                return entityId > 0;
            }

            return false;
        }
    }
}
