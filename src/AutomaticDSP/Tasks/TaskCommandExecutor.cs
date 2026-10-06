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
                case "createdysonorbit":
                case "editdysonorbit":
                case "setdysonorbitenabled":
                case "removedysonorbit":
                    ExecuteDysonOrbit(command, now);
                    return;
                case "createdysonlayer":
                case "editdysonlayer":
                case "removedysonlayer":
                    ExecuteDysonLayer(command, now);
                    return;
                case "createdysonnode":
                case "createdysonframe":
                case "createdysonshell":
                case "removedysonnode":
                case "removedysonframe":
                case "removedysonshell":
                    ExecuteDysonStructure(command, now);
                    return;
                case "validateblueprint":
                case "applydysonblueprint":
                case "exportdysonblueprint":
                    ExecuteBlueprint(command, now);
                    return;
                case "applyfactoryblueprint":
                    ExecuteFactoryBlueprint(command, now);
                    return;
                case "reformterrain":
                    ExecuteReformTerrain(command, now);
                    return;
                case "collectvegetation":
                case "plantvegetation":
                    ExecuteVegetation(command, now);
                    return;
                case "navigateto":
                    ExecuteNavigateTo(command, now);
                    return;
                case "takeoff":
                case "land":
                case "flightinput":
                case "warp":
                case "exitwarp":
                    ExecuteFlight(command, now);
                    return;
                case "setlogisticsroute":
                    ExecuteLogisticsRoute(command, now);
                    return;
                case "noop":
                    ExecuteNoopLocked(command, now);
                    return;
                case "discardinventoryitem":
                case "pickuptrash":
                case "cleartrash":
                    ExecuteTrashCommand(command, now);
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
                case "autoreplenishmechawarper":
                    ExecuteAutoReplenishMechaWarper(command, now);
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
                case "setdeliveryslot":
                case "setdeliveryenabled":
                case "transferinventoryitem":
                    ExecuteDeliveryCommand(command, now);
                    return;
                case "dismantleentity":
                    ExecuteDismantleEntityLocked(task, command, now);
                    return;
                case "upgradeentity":
                    ExecuteUpgradeEntityLocked(task, command, now);
                    return;
                case "reversebelt":
                    ExecuteReverseBeltLocked(task, command, now);
                    return;
                case "setstoragelimit":
                case "setsorterfilter":
                case "setsplitterpriority":
                case "setstationstorage":
                case "setstationvehicles":
                case "setstationsetting":
                case "setstationchargepower":
                case "transferstationitem":
                case "setdispensersetting":
                case "setveincollectorspeed":
                case "setdispenser":
                case "setdispensercouriers":
                    ExecuteLogisticsSettingLocked(task, command, now);
                    return;
                case "craftinventory":
                    ExecuteCraftInventoryLocked(command, now);
                    return;
                case "removeforgetask":
                    ExecuteRemoveForgeTask(command, now);
                    return;
                case "cancelprebuild":
                    ExecuteCancelPrebuild(command, now);
                    return;
                case "dismissnotice":
                    ExecuteDismissNotice(command, now);
                    return;
                case "researchtech":
                    ExecuteResearchTechLocked(command, now);
                    return;
                case "buyouttech":
                    ExecuteBuyoutTechLocked(command, now);
                    return;
                case "sandboxunlocktechs":
                    ExecuteSandboxUnlockTechs(command, now);
                    return;
                case "removetechinqueue":
                    ExecuteRemoveTechInQueueLocked(command, now);
                    return;
                case "setrecipe":
                    ExecuteSetRecipeLocked(task, command, now);
                    return;
                case "setrayreceivermode":
                case "setejectororbit":
                case "setproliferatormode":
                    ExecuteProductionSetting(task, command, now);
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
