using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using static AutomaticDSP.Tasks.TaskStatusNames;

namespace AutomaticDSP.Tasks
{
    internal sealed partial class TaskQueueService
    {
        private static string CommandQueue(CommandState command)
        {
            switch (command.NormalizedType)
            {
                case "craftinventory": return "craft";
                case "researchtech": return "research";
                case "placebuilding":
                case "placebelt":
                case "placesorter": return "construction";
                case "discardinventoryitem":
                case "pickuptrash":
                case "cleartrash":
                case "removeforgetask":
                case "removetechinqueue":
                case "dismissnotice": return "immediate";
                default: return "instruction";
            }
        }

        private static bool UsesPlayer(CommandState command)
        {
            var queue = CommandQueue(command);
            return queue == "instruction" || queue == "construction";
        }

        private static bool HasInstructionWork(TaskState task) => !IsTerminalTask(task.Status) &&
            task.Commands.Exists(c => UsesPlayer(c) && !c.Background && !IsTerminalCommand(c.Status));

        private static void ReadDependencies(CommandState command, List<CommandState> previous)
        {
            var ids = new List<string>(command.Request.DependsOn ?? new List<string>());
            // 现有实体引用隐含落成依赖；只允许前序命令，提交时排除环与拼写错误。
            if (command.Request.ExtensionData != null)
                foreach (var name in new[] { "target", "start", "end", "input", "output" })
                    if (command.Request.ExtensionData.TryGetValue(name, out var token) && token is JObject obj)
                    {
                        var property = obj.Properties().FirstOrDefault(p => string.Equals(p.Name, "commandId", StringComparison.OrdinalIgnoreCase));
                        if (property != null) ids.Add(property.Value.Value<string>());
                    }
            if (command.Request.ExtensionData != null)
                foreach (var name in new[] { "startCommandId", "endCommandId", "inputCommandId", "outputCommandId" })
                    if (command.Request.ExtensionData.TryGetValue(name, out var token)) ids.Add(token.Value<string>());
            foreach (var id in ids)
            {
                if (string.IsNullOrWhiteSpace(id) || !previous.Exists(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase)))
                    throw new TaskQueueException("invalid_dependency", "Dependencies must name earlier commands in the same task.");
                if (!command.Dependencies.Contains(id)) command.Dependencies.Add(id);
            }
        }

        private void AdvanceAllTasksLocked(DateTimeOffset now)
        {
            foreach (var task in tasks)
            {
                if (IsTerminalTask(task.Status)) continue;
                if (task.Status == TaskCancelRequested)
                {
                    StopTaskCommands(task, now, false);
                    CompleteTaskLocked(task, TaskCancelled, now);
                    continue;
                }
                foreach (var command in task.Commands)
                {
                    if (command.Status != CommandRunning) continue;
                    AdvanceCommandLocked(task, command, now);
                    if (task.HadCommandFailure && task.StopOnFailure) break;
                }
                if (task.HadCommandFailure && task.StopOnFailure) StopTaskCommands(task, now, true);
            }

            var playerBusy = tasks.Any(t => t.Commands.Exists(c => c.OwnsPlayerOrders));
            var blockedQueues = new HashSet<string>();
            foreach (var task in tasks)
            {
                if (IsTerminalTask(task.Status)) continue;
                foreach (var command in task.Commands)
                {
                    if (command.Status != CommandPending) continue;
                    if (task.HadCommandFailure && task.StopOnFailure) break;
                    var queue = UsesPlayer(command) ? "instruction" : CommandQueue(command);
                    if (blockedQueues.Contains(queue)) continue;
                    if (UsesPlayer(command) && playerBusy)
                    {
                        blockedQueues.Add(queue);
                        continue;
                    }
                    var dependencies = command.Dependencies.Select(id => task.Commands.Find(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase))).ToList();
                    if (dependencies.Any(c => IsTerminalCommand(c.Status) && c.Status != CommandSucceeded))
                    {
                        StartTask(task, now);
                        FinishCommandLocked(command, CommandFailed, "dependency_failed", "A required command did not succeed.", now, null);
                        task.HadCommandFailure = true;
                        continue;
                    }
                    if (dependencies.Any(c => c.Status != CommandSucceeded))
                    {
                        command.Phase = "waitingDependencies";
                        blockedQueues.Add(queue);
                        continue;
                    }
                    StartTask(task, now);
                    StartCommandLocked(command, now);
                    command.OwnsPlayerOrders = UsesPlayer(command);
                    AdvanceCommandLocked(task, command, now);
                    if (command.OwnsPlayerOrders)
                    {
                        playerBusy = true;
                        blockedQueues.Add("instruction");
                    }
                }
                if (task.HadCommandFailure && task.StopOnFailure) StopTaskCommands(task, now, true);
                task.CurrentCommandIndex = task.Commands.FindIndex(c => !IsTerminalCommand(c.Status));
                if (task.CurrentCommandIndex < 0)
                {
                    task.CurrentCommandIndex = task.Commands.Count;
                    CompleteTaskLocked(task, task.HadCommandFailure ? TaskFailed : TaskSucceeded, now);
                }
            }
        }

        private static void StartTask(TaskState task, DateTimeOffset now)
        {
            if (task.Status != TaskQueued) return;
            task.Status = TaskRunning;
            task.StartedAt = now;
        }

        private void AdvanceCommandLocked(TaskState task, CommandState command, DateTimeOffset now)
        {
            if (IsCommandTimedOut(command, now))
            {
                commandExecutor.StopCommandEffects(command);
                FinishCommandLocked(command, CommandFailed, "timeout", "Command timed out.", now, commandExecutor.TimeoutResult(command));
            }
            else commandExecutor.Execute(task, command, now);
            if (command.Status == CommandFailed) task.HadCommandFailure = true;
            if (IsTerminalCommand(command.Status))
            {
                command.OwnsPlayerOrders = false;
                return;
            }
            var nativeWaiting = command.BuildObjectId != 0 || command.BuildTargets != null ||
                ((CommandQueue(command) == "craft" || CommandQueue(command) == "research") && command.ActionIssued);
            if (nativeWaiting && !command.Background)
            {
                // 预建已下达，释放建造模式和本命令靠近订单；后续等待不碰其他命令的移动。
                commandExecutor.StopCommandEffects(command);
                commandExecutor.ExitCommandBuildMode(command);
                command.OwnsPlayerOrders = false;
                command.Background = true;
            }
        }

        private void StopTaskCommands(TaskState task, DateTimeOffset now, bool failure)
        {
            foreach (var command in task.Commands)
            {
                if (IsTerminalCommand(command.Status)) continue;
                commandExecutor.StopCommandEffects(command);
                var status = failure && command.Status == CommandPending ? CommandSkipped : CommandCancelled;
                FinishCommandLocked(command, status, failure ? "stopped_after_failure" : "task_cancelled",
                    "Task stopped; already submitted native work is not reverted.", now, null);
                command.OwnsPlayerOrders = false;
            }
        }
    }
}
