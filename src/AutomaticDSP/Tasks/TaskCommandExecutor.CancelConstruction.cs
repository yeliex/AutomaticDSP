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
        private void ExecuteCancelConstruction(TaskState task, CommandState command, DateTimeOffset now)
        {
            if (TryGetToken(command, "skipMissing", out var skip) && skip.Type != JTokenType.Boolean)
            {
                finishCommand(command, CommandFailed, "invalid_command", "skipMissing 必须为布尔值。", now, null);
                return;
            }
            if (!TryGetPlayer(out var player, out var errorCode, out var errorMessage) ||
                !TryValidateCurrentPlanet(command, player, out errorCode, out errorMessage))
            {
                finishCommand(command, CommandFailed, errorCode, errorMessage, now, null);
                return;
            }
            if (TryGetToken(command, "sourceCommandId", out var sourceId))
            {
                if (sourceId.Type != JTokenType.String || string.IsNullOrWhiteSpace(sourceId.Value<string>()) ||
                    (TryGetToken(command, "sourceTaskId", out var taskToken) && taskToken.Type != JTokenType.String) ||
                    TryGetToken(command, "prebuildId", out _))
                {
                    finishCommand(command, CommandFailed, "invalid_command", "sourceCommandId/sourceTaskId 必须为字符串，且不能同时指定 prebuildId。", now, null);
                    return;
                }
                var taskId = TryGetToken(command, "sourceTaskId", out var sourceTask) ? sourceTask.Value<string>() : task.Id;
                var source = FindCommand?.Invoke(taskId, sourceId.Value<string>());
                if (source == null || ReferenceEquals(source, command) ||
                    source.Status == CommandPending || source.Status == CommandRunning)
                {
                    finishCommand(command, CommandFailed, "source_not_ready", "需要当前会话中已结束的建造命令。", now, null);
                    return;
                }
                if (source.BuildPlanetId <= 0 || source.BuildPlanetId != GameMain.localPlanet?.id)
                {
                    finishCommand(command, CommandFailed, "wrong_planet", "必须在原建造命令所在星球取消施工。", now, null);
                    return;
                }
                if (!ReferenceEquals(source.BuildSession, constructionTracker.Session))
                {
                    finishCommand(command, CommandFailed, "source_session_changed", "读档后不能使用旧会话的建造命令取消施工，请重新查询预建。", now, null);
                    return;
                }
                var targets = source.BuildTargets?.Where(t => t.CreatedPrebuild).ToList() ?? new List<BuildWaitTarget>();
                if (targets.Count == 0)
                {
                    finishCommand(command, CommandSucceeded, null, null, now,
                        new JsonObject { ["count"] = 0, ["removedCount"] = 0 });
                    return;
                }
                command.Request.ExtensionData.Remove("sourceCommandId");
                command.Request.ExtensionData.Remove("sourceTaskId");
                command.CancellationTargets = targets;
                command.Request.ExtensionData["planetId"] = source.BuildPlanetId;
                // 源命令引用只取消尚未落成的预建；不会等待落成或转成实体拆除。
                command.Request.ExtensionData["batch"] = new JArray(targets.Select(t => new JObject
                {
                    ["prebuildId"] = -t.ObjectId, ["expectedItemId"] = t.ItemId,
                    ["expectedPosition"] = new JArray(t.Position.x, t.Position.y, t.Position.z),
                    ["skipMissing"] = true
                }));
                TryExecuteBatch(task, command, now);
                return;
            }
            if (command.CancellationTarget?.PrebuildRemoved == true)
            {
                finishCommand(command, CommandSucceeded, null, null, now, new JsonObject
                { ["prebuildId"] = -command.CancellationTarget.ObjectId, ["removed"] = false, ["state"] = "not_pending" });
                return;
            }
            var factory = GameMain.localPlanet?.factory;
            if (TryGetInt(command, "prebuildId", out var id) && id > 0 && factory != null)
            {
                var exists = id < factory.prebuildPool.Length && factory.prebuildPool[id].id == id;
                if (!exists && GetBool(command, "skipMissing", false))
                {
                    finishCommand(command, CommandSucceeded, null, null, now, new JsonObject
                    { ["prebuildId"] = id, ["removed"] = false, ["state"] = "not_pending" });
                    return;
                }
                if (exists && TryGetToken(command, "expectedItemId", out var expected))
                {
                    if (expected.Type != JTokenType.Integer || expected.Value<double>() <= 0 || expected.Value<double>() > int.MaxValue ||
                        expected.Value<int>() != factory.prebuildPool[id].protoId ||
                        !TryGetVector(command, "expectedPosition", out var position, out _) ||
                        (position - factory.prebuildPool[id].pos).sqrMagnitude > 0.0001f)
                    {
                        finishCommand(command, CommandFailed, "target_changed", "预建身份已变化，不能按旧建造记录取消。", now, null);
                        return;
                    }
                }
            }
            ExecuteCancelPrebuild(command, now);
        }
    }
}
