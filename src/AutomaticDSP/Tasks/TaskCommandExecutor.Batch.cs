using System;
using System.Collections.Generic;
using System.Linq;
using AutomaticDSP.Serialization;
using Newtonsoft.Json.Linq;
using static AutomaticDSP.Tasks.TaskStatusNames;

namespace AutomaticDSP.Tasks
{
    internal sealed partial class TaskCommandExecutor
    {
        private bool TryExecuteBatch(TaskState task, CommandState command, DateTimeOffset now)
        {
            var idsName = command.NormalizedType == "dismantleentity" ? "entityIds" :
                command.NormalizedType == "cancelprebuild" ? "prebuildIds" : null;
            if (idsName != null && TryGetToken(command, idsName, out var ids))
            {
                if (TryGetToken(command, "batch", out _) || !(ids is JArray values) || values.Count == 0 ||
                    values.Any(id => id.Type != JTokenType.Integer || id.Value<double>() <= 0 || id.Value<double>() > int.MaxValue))
                {
                    finishCommand(command, CommandFailed, "invalid_command", idsName + " 必须是非空正整数数组，且不能与 batch 同时使用。", now, null);
                    return true;
                }
                command.Request.ExtensionData.Remove(idsName);
                command.Request.ExtensionData["batch"] = new JArray(values.Select(id => new JObject { [idsName == "entityIds" ? "entityId" : "prebuildId"] = id.DeepClone() }));
            }
            if (!TryGetToken(command, "batch", out var token)) return false;
            if (TryGetToken(command, "stopOnFailure", out var stop) && stop.Type != JTokenType.Boolean)
            {
                finishCommand(command, CommandFailed, "invalid_command", "stopOnFailure 必须为布尔值。", now, null);
                return true;
            }
            if (command.NormalizedType != "placebuilding" && command.NormalizedType != "placebelt" &&
                command.NormalizedType != "placesorter" && command.NormalizedType != "applyfactoryblueprint" &&
                command.NormalizedType != "dismantleentity" && command.NormalizedType != "cancelprebuild")
            {
                finishCommand(command, CommandFailed, "invalid_command", "该命令不支持 batch。", now, null);
                return true;
            }
            if (command.BatchCommands == null)
            {
                if (!(token is JArray batch) || batch.Count == 0 || batch.Any(x => !(x is JObject) ||
                    ((JObject)x).Properties().Any(p => new[] { "batch", "type", "id", "dependsOn", "timeoutSeconds" }.Contains(p.Name, StringComparer.OrdinalIgnoreCase))))
                {
                    finishCommand(command, CommandFailed, "invalid_command", "batch 必须是非空参数对象数组，不能嵌套或覆盖命令身份、依赖和超时。", now, null);
                    return true;
                }
                command.BatchCommands = new List<CommandState>();
                foreach (JObject entry in batch)
                {
                    var data = new Dictionary<string, JToken>(StringComparer.OrdinalIgnoreCase);
                    foreach (var pair in command.Request.ExtensionData)
                        if (!string.Equals(pair.Key, "batch", StringComparison.OrdinalIgnoreCase)) data[pair.Key] = pair.Value.DeepClone();
                    foreach (var pair in entry) data[pair.Key] = pair.Value.DeepClone();
                    var request = new TaskCommandRequest { Type = command.Type, ExtensionData = data };
                    command.BatchCommands.Add(new CommandState(command.Id + ":" + command.BatchCommands.Count, command.Type, request)
                    { TaskId = command.TaskId, Status = CommandPending, IsBatchChild = true,
                        CancellationTarget = command.CancellationTargets?[command.BatchCommands.Count] });
                }
            }
            // 子项复用同一执行器与完成处理，历史仅记录父命令。
            while (command.BatchIndex < command.BatchCommands.Count)
            {
                var child = command.BatchCommands[command.BatchIndex];
                if (!AreEntityReferencesReady(task, child, now))
                {
                    command.Phase = child.Phase;
                    command.Status = CommandPending;
                    command.OwnsPlayerOrders = false;
                    command.Result = BatchResult(command);
                    return true;
                }
                child.OwnsPlayerOrders = command.OwnsPlayerOrders;
                child.StartedAt = child.StartedAt ?? now;
                child.Status = CommandRunning;
                try { Execute(task, child, now); }
                catch (Exception ex)
                {
                    StopCommandEffects(child);
                    finishCommand(child, CommandFailed, "execution_error", ex.Message, now, child.Result);
                }
                if (child.Status == CommandRunning || child.Status == CommandPending)
                {
                    command.Phase = child.Phase;
                    command.Result = BatchResult(command);
                    return true;
                }
                child.OwnsPlayerOrders = false;
                if (child.BuildTargets != null)
                {
                    if (command.BuildTargets == null) command.BuildTargets = new List<BuildWaitTarget>();
                    command.BuildTargets.AddRange(child.BuildTargets);
                    command.BuildPlanetId = child.BuildPlanetId;
                    command.BuildSession = child.BuildSession;
                }
                command.BatchIndex++;
                if (child.Status == CommandFailed && GetBool(command, "stopOnFailure", task.StopOnFailure)) break;
            }
            var failed = command.BatchCommands.Any(c => c.Status == CommandFailed);
            foreach (var child in command.BatchCommands)
                if (child.Status == CommandPending) child.Status = CommandSkipped;
            finishCommand(command, failed ? CommandFailed : CommandSucceeded, failed ? "batch_failed" : null,
                failed ? "部分批量操作失败，请检查逐项结果。" : null, now, BatchResult(command));
            return true;
        }

        private static JsonObject BatchResult(CommandState command) => new JsonObject
        {
            ["count"] = command.BatchCommands.Count,
            ["processedCount"] = command.BatchIndex,
            ["succeededCount"] = command.BatchCommands.Count(c => c.Status == CommandSucceeded),
            ["failedCount"] = command.BatchCommands.Count(c => c.Status == CommandFailed),
            ["objectIds"] = command.BuildTargets?.Select(t => t.ObjectId).ToArray(),
            ["prebuildIds"] = command.BuildTargets?.Where(t => t.ObjectId < 0).Select(t => -t.ObjectId).ToArray(),
            ["entityIds"] = command.BuildTargets?.Where(t => t.ObjectId > 0).Select(t => t.ObjectId).ToArray(),
            ["results"] = command.BatchCommands.Select((c, i) => new JsonObject
            {
                ["index"] = i, ["status"] = c.Status, ["errorCode"] = c.ErrorCode,
                ["errorMessage"] = c.ErrorMessage, ["result"] = c.Result
            }).ToArray()
        };
    }
}
