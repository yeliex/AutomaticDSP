using System;
using System.Collections.Generic;
using AutomaticDSP.Serialization;
using AutomaticDSP.Storage;
using BepInEx.Logging;
using Newtonsoft.Json.Linq;
using UnityEngine;
using static AutomaticDSP.Tasks.TaskStatusNames;

namespace AutomaticDSP.Tasks
{
    internal sealed partial class TaskQueueService
    {
        private readonly HistoryStore historyStore;
        private readonly TaskCommandExecutor commandExecutor;
        private readonly ManualLogSource log;
        private readonly object queueLock = new object();
        private readonly List<TaskState> tasks = new List<TaskState>();
        private long nextTaskId = 1;

        public TaskQueueService(HistoryStore historyStore, ManualLogSource log)
        {
            this.historyStore = historyStore;
            this.log = log;
            this.commandExecutor = new TaskCommandExecutor(FinishCommandLocked);
        }

        public JsonObject Enqueue(TaskSubmitRequest request)
        {
            lock (queueLock)
            {
                var task = CreateTask(request);
                task.QueueIndex = HasInstructionWork(task) ? ActiveTaskCountLocked() : -1;
                tasks.Add(task);
                return new JsonObject
                {
                    ["task"] = TaskSnapshot(task, task.QueueIndex)
                };
            }
        }

        public JsonObject Cancel(string taskId)
        {
            if (string.IsNullOrWhiteSpace(taskId))
            {
                throw new TaskQueueException("bad_request", "Task id is required.");
            }

            lock (queueLock)
            {
                var task = FindTaskLocked(taskId);
                if (task == null)
                {
                    throw new TaskQueueException("task_not_found", $"Task not found: {taskId}");
                }

                if (task.Status == TaskQueued)
                {
                    var now = DateTimeOffset.UtcNow;
                    CancelPendingCommandsLocked(task, now, 0);
                    task.Status = TaskCancelled;
                    task.CompletedAt = now;
                }
                else if (task.Status == TaskRunning)
                {
                    task.Status = TaskCancelRequested;
                }

                return new JsonObject
                {
                    ["task"] = TaskSnapshot(task, QueueIndexLocked(task))
                };
            }
        }

        public JsonObject GetActiveTasksResponse()
        {
            lock (queueLock)
            {
                var activeTasks = new List<object>();
                foreach (var task in tasks)
                {
                    if (IsTerminalTask(task.Status))
                    {
                        continue;
                    }

                    activeTasks.Add(TaskSnapshot(task, QueueIndexLocked(task)));
                }

                return new JsonObject
                {
                    ["tasks"] = activeTasks
                };
            }
        }

        public JsonObject GetTaskResponse(string taskId)
        {
            if (string.IsNullOrWhiteSpace(taskId))
            {
                throw new TaskQueueException("bad_request", "Task id is required.");
            }

            lock (queueLock)
            {
                var task = FindTaskLocked(taskId);
                if (task == null)
                {
                    throw new TaskQueueException("task_not_found", $"Task not found: {taskId}");
                }

                return new JsonObject
                {
                    ["task"] = TaskSnapshot(task, QueueIndexLocked(task))
                };
            }
        }

        public List<TaskOverlaySnapshot> GetOverlaySnapshots(int maxTasks, int maxCommandsPerTask)
        {
            lock (queueLock)
            {
                var limit = Math.Max(0, maxTasks);
                var result = new List<TaskOverlaySnapshot>();
                if (limit == 0)
                {
                    return result;
                }

                var queueIndex = 0;
                foreach (var task in tasks)
                {
                    var taskQueueIndex = -1;
                    if (HasInstructionWork(task))
                    {
                        taskQueueIndex = queueIndex++;
                    }

                    result.Add(TaskOverlaySnapshot(task, taskQueueIndex, maxCommandsPerTask));
                    if (result.Count > limit)
                    {
                        result.RemoveAt(0);
                    }
                }

                return result;
            }
        }

        public void Update()
        {
            lock (queueLock) AdvanceAllTasksLocked(DateTimeOffset.UtcNow);
        }

        private TaskState CreateTask(TaskSubmitRequest request)
        {
            if (request == null)
            {
                throw new TaskQueueException("bad_request", "Request body is required.");
            }

            if (request.Commands == null || request.Commands.Count == 0)
            {
                throw new TaskQueueException("invalid_command", "Task must include at least one command.");
            }

            var seenCommandIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var commands = new List<CommandState>();
            for (var i = 0; i < request.Commands.Count; i++)
            {
                var command = request.Commands[i];
                if (command == null)
                {
                    throw new TaskQueueException("invalid_command", $"Command {i} is null.");
                }

                if (string.IsNullOrWhiteSpace(command.Type))
                {
                    throw new TaskQueueException("invalid_command", $"Command {i} is missing required field: type.");
                }

                var commandId = string.IsNullOrWhiteSpace(command.Id) ? $"command:{i}" : command.Id.Trim();
                if (!seenCommandIds.Add(commandId))
                {
                    throw new TaskQueueException("invalid_command", $"Duplicate command id: {commandId}");
                }

                var state = new CommandState(commandId, command.Type.Trim(), command);
                if (request.Immediate && !TaskCommandExecutor.CanExecuteImmediately(state))
                {
                    throw new TaskQueueException("invalid_command", "Immediate tasks only support non-waiting craft/research enqueue, queue removal, notice dismissal and trash operations.");
                }
                ReadDependencies(state, commands);
                commands.Add(state);
            }

            var id = $"task:{nextTaskId++}";
            return new TaskState(id, request.ClientRequestId, request.StopOnFailure ?? true, commands) { Immediate = request.Immediate };
        }

        private void StartCommandLocked(CommandState command, DateTimeOffset now)
        {
            command.Status = CommandRunning;
            command.Phase = "validating";
            command.StartedAt = now;
            command.StartGameTick = CurrentGameTick();
        }

        private bool IsCommandTimedOut(CommandState command, DateTimeOffset now)
        {
            if (!command.Request.TimeoutSeconds.HasValue || !command.StartedAt.HasValue)
            {
                return false;
            }

            var timeoutSeconds = Math.Max(0, command.Request.TimeoutSeconds.Value);
            return now >= command.StartedAt.Value.AddSeconds(timeoutSeconds);
        }

        private void FinishCommandLocked(
            CommandState command,
            string status,
            string errorCode,
            string errorMessage,
            DateTimeOffset now,
            object result)
        {
            if (IsTerminalCommand(command.Status))
            {
                return;
            }

            command.Status = status;
            commandExecutor.ExitCommandBuildMode(command);
            command.Phase = status == CommandSucceeded ? "completed" :
                status == CommandCancelled ? "cancelled" :
                status == CommandSkipped ? "skipped" :
                "failed";
            command.CompletedAt = now;
            command.ErrorCode = errorCode;
            command.ErrorMessage = errorMessage;
            command.Result = result;
            WriteCommandHistory(command);
        }

        private void CompleteTaskLocked(TaskState task, string status, DateTimeOffset now)
        {
            task.Status = status;
            task.CompletedAt = now;
        }

        private void CancelPendingCommandsLocked(TaskState task, DateTimeOffset now, int startIndex)
        {
            for (var i = Math.Max(0, startIndex); i < task.Commands.Count; i++)
            {
                var command = task.Commands[i];
                if (command.Status == CommandPending)
                {
                    FinishCommandLocked(
                        command,
                        CommandCancelled,
                        "task_cancelled",
                        "Command cancelled before it started.",
                        now,
                        null);
                }
            }
        }

        private void WriteCommandHistory(CommandState command)
        {
            try
            {
                historyStore.InsertCommandHistory(
                    command.TaskId,
                    command.Id,
                    command.Type,
                    command.Status,
                    command.StartedAt,
                    command.CompletedAt,
                    command.ErrorCode,
                    command.ErrorMessage,
                    command.Result,
                    CurrentGameTick());
            }
            catch (Exception ex)
            {
                log.LogWarning($"Failed to write command history: {ex.Message}");
            }
        }

        private TaskState FindTaskLocked(string taskId)
        {
            foreach (var task in tasks)
            {
                if (string.Equals(task.Id, taskId, StringComparison.OrdinalIgnoreCase))
                {
                    return task;
                }
            }

            return null;
        }

        private int ActiveTaskCountLocked()
        {
            var count = 0;
            foreach (var task in tasks)
            {
                if (HasInstructionWork(task))
                {
                    count++;
                }
            }

            return count;
        }

        private int QueueIndexLocked(TaskState target)
        {
            if (!HasInstructionWork(target)) return -1;
            var queueIndex = 0;
            foreach (var task in tasks)
            {
                if (!HasInstructionWork(task))
                {
                    continue;
                }

                if (ReferenceEquals(task, target))
                {
                    return queueIndex;
                }

                queueIndex++;
            }

            return -1;
        }

        private static JsonObject TaskSnapshot(TaskState task, int queueIndex)
        {
            var commands = new List<object>();
            foreach (var command in task.Commands)
            {
                commands.Add(CommandSnapshot(command));
            }

            return new JsonObject
            {
                ["id"] = task.Id,
                ["clientRequestId"] = task.ClientRequestId,
                ["immediate"] = task.Immediate,
                ["status"] = task.Status,
                ["queueIndex"] = queueIndex,
                ["currentCommandIndex"] = task.CurrentCommandIndex,
                ["stopOnFailure"] = task.StopOnFailure,
                ["createdAt"] = task.CreatedAt,
                ["startedAt"] = task.StartedAt,
                ["completedAt"] = task.CompletedAt,
                ["commands"] = commands
            };
        }

        private static JsonObject CommandSnapshot(CommandState command)
        {
            return new JsonObject
            {
                ["id"] = command.Id,
                ["type"] = command.Type,
                ["status"] = command.Status,
                ["phase"] = command.Phase,
                ["queue"] = CommandQueue(command),
                ["background"] = command.Background,
                ["dependsOn"] = command.Dependencies,
                ["startedAt"] = command.StartedAt,
                ["completedAt"] = command.CompletedAt,
                ["errorCode"] = command.ErrorCode,
                ["errorMessage"] = command.ErrorMessage,
                ["result"] = command.Result
            };
        }

        private static TaskOverlaySnapshot TaskOverlaySnapshot(TaskState task, int queueIndex, int maxCommandsPerTask)
        {
            var commands = new List<CommandOverlaySnapshot>();
            var count = Math.Min(Math.Max(0, maxCommandsPerTask), task.Commands.Count);
            for (var i = 0; i < count; i++)
            {
                var command = task.Commands[i];
                commands.Add(new CommandOverlaySnapshot(
                    command.Id,
                    command.Type,
                    command.Status,
                    command.Phase,
                    command.ErrorCode,
                    command.CompletedAt ?? command.StartedAt));
            }

            return new TaskOverlaySnapshot(
                task.Id,
                task.ClientRequestId,
                task.Status,
                task.CreatedAt,
                queueIndex,
                task.CurrentCommandIndex,
                task.Commands.Count,
                commands);
        }

        private static bool IsTerminalTask(string status)
        {
            return status == TaskSucceeded || status == TaskFailed || status == TaskCancelled;
        }

        private static bool IsTerminalCommand(string status)
        {
            return status == CommandSucceeded ||
                status == CommandFailed ||
                status == CommandSkipped ||
                status == CommandCancelled;
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

    internal sealed class TaskOverlaySnapshot
    {
        public TaskOverlaySnapshot(
            string id,
            string clientRequestId,
            string status,
            DateTimeOffset createdAt,
            int queueIndex,
            int currentCommandIndex,
            int commandCount,
            List<CommandOverlaySnapshot> commands)
        {
            Id = id;
            ClientRequestId = clientRequestId;
            Status = status;
            CreatedAt = createdAt;
            QueueIndex = queueIndex;
            CurrentCommandIndex = currentCommandIndex;
            CommandCount = commandCount;
            Commands = commands;
        }

        public string Id { get; }

        public string ClientRequestId { get; }

        public string Status { get; }

        public DateTimeOffset CreatedAt { get; }

        public int QueueIndex { get; }

        public int CurrentCommandIndex { get; }

        public int CommandCount { get; }

        public List<CommandOverlaySnapshot> Commands { get; }
    }

    internal sealed class CommandOverlaySnapshot
    {
        public CommandOverlaySnapshot(string id, string type, string status, string phase, string errorCode, DateTimeOffset? timestamp)
        {
            Id = id;
            Type = type;
            Status = status;
            Phase = phase;
            ErrorCode = errorCode;
            Timestamp = timestamp;
        }

        public string Id { get; }

        public string Type { get; }

        public string Status { get; }

        public string Phase { get; }

        public string ErrorCode { get; }

        public DateTimeOffset? Timestamp { get; }
    }
}
