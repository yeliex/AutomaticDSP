using System;
using System.Collections.Generic;
using static AutomaticDSP.Tasks.TaskStatusNames;

namespace AutomaticDSP.Tasks
{
    internal sealed class TaskState
    {
        public TaskState(string id, string clientRequestId, bool stopOnFailure, List<CommandState> commands)
        {
            Id = id;
            ClientRequestId = clientRequestId;
            StopOnFailure = stopOnFailure;
            Commands = commands;
            CreatedAt = DateTimeOffset.UtcNow;
            Status = TaskQueued;
            for (var i = 0; i < commands.Count; i++)
            {
                commands[i].TaskId = id;
            }
        }

        public string Id { get; }

        public string ClientRequestId { get; }

        public bool StopOnFailure { get; }

        public List<CommandState> Commands { get; }

        public string Status { get; set; }

        public int QueueIndex { get; set; }

        public int CurrentCommandIndex { get; set; }

        public bool HadCommandFailure { get; set; }

        public DateTimeOffset CreatedAt { get; }

        public DateTimeOffset? StartedAt { get; set; }

        public DateTimeOffset? CompletedAt { get; set; }
    }

}
