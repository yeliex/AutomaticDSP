using System;
using System.Collections.Generic;
using AutomaticDSP.Serialization;
using Newtonsoft.Json.Linq;
using UnityEngine;
using static AutomaticDSP.Tasks.TaskStatusNames;

namespace AutomaticDSP.Tasks
{
    internal delegate void TaskCommandFinisher(
        CommandState command,
        string status,
        string errorCode,
        string errorMessage,
        DateTimeOffset now,
        object result);

    internal sealed partial class TaskCommandExecutor
    {
        private readonly TaskCommandFinisher finishCommand;

        public TaskCommandExecutor(TaskCommandFinisher finishCommand)
        {
            this.finishCommand = finishCommand;
        }

        public void Execute(TaskState task, CommandState command, DateTimeOffset now)
        {
            switch (command.NormalizedType)
            {
                case "noop":
                    ExecuteNoopLocked(command, now);
                    return;
                case "waituntil":
                    ExecuteWaitUntilLocked(command, now);
                    return;
                case "moveto":
                    ExecuteMoveToLocked(command, now);
                    return;
                case "minetarget":
                    ExecuteMineTargetLocked(command, now);
                    return;
                case "autoreplenishmechafuel":
                    ExecuteAutoReplenishMechaFuelLocked(command, now);
                    return;
                case "entityfastfillin":
                    ExecuteEntityFastFillInLocked(task, command, now);
                    return;
                case "entityfasttakeout":
                    ExecuteEntityFastTakeOutLocked(task, command, now);
                    return;
                case "transferstorageitem":
                    ExecuteTransferStorageItemLocked(task, command, now);
                    return;
                case "dismantleentity":
                    ExecuteDismantleEntityLocked(task, command, now);
                    return;
                case "craftinventory":
                    ExecuteCraftInventoryLocked(command, now);
                    return;
                case "researchtech":
                    ExecuteResearchTechLocked(command, now);
                    return;
                case "buyouttech":
                    ExecuteBuyoutTechLocked(command, now);
                    return;
                case "removetechinqueue":
                    ExecuteRemoveTechInQueueLocked(command, now);
                    return;
                case "setrecipe":
                    ExecuteSetRecipeLocked(task, command, now);
                    return;
                case "setlabresearchmode":
                    ExecuteSetLabResearchModeLocked(task, command, now);
                    return;
                case "placebuilding":
                    ExecutePlaceBuildingLocked(command, now);
                    return;
                case "placebelt":
                    ExecutePlaceBeltLocked(task, command, now);
                    return;
                case "placesorter":
                    ExecutePlaceSorterLocked(task, command, now);
                    return;
                default:
                    finishCommand(
                        command,
                        CommandFailed,
                        "unsupported_command",
                        $"Unsupported command type: {command.Type}",
                        now,
                        null);
                    return;
            }
        }


    }
}
