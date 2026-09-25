using System;
using AutomaticDSP.Serialization;
using UnityEngine;
using static AutomaticDSP.Tasks.TaskStatusNames;

namespace AutomaticDSP.Tasks
{
    internal sealed partial class TaskCommandExecutor
    {
        private void ExecuteUpgradeEntityLocked(TaskState task, CommandState command, DateTimeOffset now)
        {
            if (!TryGetPlayer(out var player, out var errorCode, out var errorMessage) ||
                !TryValidateCurrentPlanet(command, player, out errorCode, out errorMessage))
            {
                finishCommand(command, CommandFailed, errorCode, errorMessage, now, null);
                return;
            }

            var factory = GameMain.localPlanet?.factory;
            var actionBuild = player.controller?.actionBuild;
            if (factory == null || actionBuild == null)
            {
                finishCommand(command, CommandFailed, "game_not_ready", "Player build action is not available.", now, null);
                return;
            }

            if (!TryGetTargetEntityId(task, command, out var entityId, out errorMessage) ||
                !TryGetInt(command, "itemId", out var itemId))
            {
                finishCommand(command, CommandFailed, "invalid_command", "upgradeEntity requires an entity target and target itemId.", now, null);
                return;
            }

            if (!TryGetEntity(factory, entityId, out var entity))
            {
                finishCommand(command, CommandFailed, "target_not_found", $"Entity not found: {entityId}", now, null);
                return;
            }

            var source = LDB.items.Select(entity.protoId);
            var target = LDB.items.Select(itemId);
            if (source == null || target == null || !source.canUpgrade || target.Grade <= source.Grade ||
                source.GetGradeItem(target.Grade)?.ID != itemId)
            {
                finishCommand(command, CommandFailed, "invalid_command", "Target item is not a higher grade of this entity.", now, null);
                return;
            }

            // 指定等级的原生升级入口本身不校验科技与距离，入口层必须保留这些限制。
            if (!GameMain.history.ItemUnlocked(itemId))
            {
                finishCommand(command, CommandFailed, "tech_locked", $"Item is locked: {itemId}", now, null);
                return;
            }

            var distance = Vector3.Distance(player.position, entity.pos);
            var range = player.mecha.buildArea;
            if (distance > range)
            {
                finishCommand(command, CommandFailed, "out_of_range", "Move within build range before upgrading.", now,
                    RangeFailureResult(distance, range, entity.pos));
                return;
            }

            var requiredCount = actionBuild.ObjectAssetValue(entityId);
            if (player.package.GetItemCount(itemId) < requiredCount &&
                (player.inhandItemId != itemId || player.inhandItemCount < requiredCount))
            {
                finishCommand(command, CommandFailed, "missing_item", "Not enough target items for the upgrade.", now, null);
                return;
            }

            command.Phase = "upgrading";
            var upgraded = actionBuild.DoUpgradeObject(entityId, target.Grade, 0, out var nativeError);
            var actualItemId = factory.entityPool[entityId].protoId;
            finishCommand(command, upgraded && actualItemId == itemId ? CommandSucceeded : CommandFailed,
                upgraded && actualItemId == itemId ? null : "upgrade_failed",
                upgraded && actualItemId == itemId ? null : "Native upgrade did not produce the requested item.", now,
                new JsonObject { ["entityId"] = entityId, ["previousItemId"] = entity.protoId,
                    ["itemId"] = actualItemId, ["nativeError"] = nativeError });
        }
    }
}
