using System;
using AutomaticDSP.Serialization;
using UnityEngine;
using static AutomaticDSP.Tasks.TaskStatusNames;

namespace AutomaticDSP.Tasks
{
    internal sealed partial class TaskCommandExecutor
    {
        private void ExecutePlaceBeltAddonLocked(CommandState command, Player player, PlanetFactory factory,
            ItemProto item, Vector3 position, float yaw, int stackOnEntityId, DateTimeOffset now)
        {
            if (stackOnEntityId != 0)
            {
                if (!item.prefabDesc.multiLevel || !TryGetEntity(factory, stackOnEntityId, out var baseEntity) ||
                    baseEntity.protoId != item.ID)
                {
                    finishCommand(command, CommandFailed, "invalid_command", "Addon stacking requires an existing matching stackable entity.", now, null);
                    return;
                }
                if (!TryEnsureObjectSlotAvailable(factory, stackOnEntityId, 15, out var error))
                {
                    finishCommand(command, CommandFailed, "slot_occupied", error, now, null);
                    return;
                }
                position = baseEntity.pos;
            }
            if (!IsWithinCommandIssueRange(player, position, out var distance, out var range))
            {
                finishCommand(command, CommandFailed, "out_of_range", "Move closer before placing the addon.", now,
                    RangeFailureResult(distance, range, position));
                return;
            }

            var tool = new AutomationAddonBuildTool();
            try
            {
                tool._Init(GameMain.data);
                player.controller.actionBuild.SetFactoryReferences();
                tool.SetFactoryReferences();
                tool.PrepareInventorySnapshot();
                player.controller.cmd.type = ECommand.Build;
                player.controller.cmd.mode = item.BuildMode;
                player.controller.cmd.stage = 0;
                player.controller.cmd.refId = item.ID;
                command.EnteredBuildMode = true;
                tool.handItem = item;
                tool.handPrefabDesc = item.prefabDesc;
                tool.yaw = yaw;
                tool.cursorValid = true;
                tool.cursorTarget = position;
                tool.castGroundPos = position;
                tool.castGroundPosSnapped = factory.planet.aux.Snap(position, onTerrain: true);
                tool.castObjectId = stackOnEntityId;
                tool.multiLevelCovering = stackOnEntityId != 0;
                tool.DeterminePreviews();
                var preview = tool.buildPreviews[0];
                tool.ActiveColliders();
                tool.FindPotentialBelt(0);
                tool.SnapToBelt(0);
                tool.SnapToBeltAutoAdjust(0);
                tool.ResetBeltSearch();
                tool.ActiveColliders();
                tool.FindPotentialBeltStrict(0);
                if (!IsWithinCommandIssueRange(player, preview.lpos, out distance, out range))
                {
                    finishCommand(command, CommandFailed, "out_of_range", "Snapped addon is outside command issue range.", now,
                        RangeFailureResult(distance, range, preview.lpos));
                    return;
                }
                command.Phase = "validating";
                if (!tool.CheckBuildConditions() || preview.condition != EBuildCondition.Ok)
                {
                    if (preview.condition == EBuildCondition.OutOfReach && TryApproachBuildTarget(player, preview.lpos))
                    {
                        command.Phase = "approaching";
                        return;
                    }
                    finishCommand(command, CommandFailed, BuildConditionCode(preview.condition), preview.conditionText, now,
                        new JsonObject { ["condition"] = preview.condition.ToString() });
                    return;
                }
                tool.CreatePrebuilds();
                if (preview.objId == 0)
                {
                    finishCommand(command, CommandFailed, "build_failed", "Addon prebuild was not created.", now, null);
                    return;
                }
                command.BuildObjectId = preview.objId;
                command.BuildPreview = preview;
                command.BuildItemId = item.ID;
                command.BuildPosition = preview.lpos;
            }
            finally
            {
                tool._Free();
            }
            WaitForBuiltObjectLocked(command, now);
        }
    }
}
