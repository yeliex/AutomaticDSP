using System;
using System.Collections.Generic;
using UnityEngine;
using static AutomaticDSP.Tasks.TaskStatusNames;

namespace AutomaticDSP.Tasks
{
    internal sealed class CommandState
    {
        public CommandState(string id, string type, TaskCommandRequest request)
        {
            Id = id;
            Type = type;
            NormalizedType = TaskCommandType.Normalize(type);
            Request = request;
            Status = CommandPending;
        }

        public string TaskId { get; set; }

        public string Id { get; }

        public string Type { get; }

        public string NormalizedType { get; }

        public TaskCommandRequest Request { get; }

        public string Status { get; set; }

        public string Phase { get; set; }

        public bool EnteredBuildMode { get; set; }

        public bool Background { get; set; }

        public bool OwnsPlayerOrders { get; set; }

        public List<string> Dependencies { get; } = new List<string>();

        public ForgeTask NativeForgeTask { get; set; }

        public DateTimeOffset? StartedAt { get; set; }

        public DateTimeOffset? CompletedAt { get; set; }

        public string ErrorCode { get; set; }

        public string ErrorMessage { get; set; }

        public object Result { get; set; }

        public long? StartGameTick { get; set; }

        public bool ActionIssued { get; set; }

        public int TargetId { get; set; }

        public EObjectType MineObjectType { get; set; }

        public Vector3 TargetPosition { get; set; }

        public Vector3 ObjectPosition { get; set; }

        public int TargetItemId { get; set; }

        public int InitialInventoryCount { get; set; }

        public int TargetInventoryCount { get; set; }

        public List<int> MiningProductItemIds { get; set; }

        public Dictionary<int, int> MiningInitialInventoryCounts { get; set; }

        public bool UntilDepleted { get; set; }

        public Dictionary<int, int> CraftTargets { get; set; }

        public int CraftRecipeId { get; set; }

        public int CraftCount { get; set; }

        public int RequestedItemId { get; set; }

        public int RequestedItemCount { get; set; }

        public Dictionary<string, long> WaitBaselines { get; set; }

        public BuildPreview BuildPreview { get; set; }

        public int BuildObjectId { get; set; }

        public int BuildItemId { get; set; }

        public Vector3 BuildPosition { get; set; }

        public List<BuildWaitTarget> BuildTargets { get; set; }
    }

}
