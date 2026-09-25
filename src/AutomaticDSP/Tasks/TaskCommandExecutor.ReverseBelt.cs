using System;
using System.Collections.Generic;
using AutomaticDSP.Serialization;
using UnityEngine;
using static AutomaticDSP.Tasks.TaskStatusNames;

namespace AutomaticDSP.Tasks
{
    internal sealed partial class TaskCommandExecutor
    {
        private void ExecuteReverseBeltLocked(TaskState task, CommandState command, DateTimeOffset now)
        {
            if (!TryGetPlayer(out var player, out var errorCode, out var errorMessage) ||
                !TryValidateCurrentPlanet(command, player, out errorCode, out errorMessage))
            {
                finishCommand(command, CommandFailed, errorCode, errorMessage, now, null);
                return;
            }

            var factory = GameMain.localPlanet?.factory;
            var window = UIRoot.instance?.uiGame?.beltWindow;
            if (factory == null || window == null)
            {
                finishCommand(command, CommandFailed, "game_not_ready", "Native belt control is unavailable.", now, null);
                return;
            }
            if (!TryGetTargetEntityId(task, command, out var entityId, out errorMessage))
            {
                finishCommand(command, CommandFailed, "invalid_command", errorMessage, now, null);
                return;
            }
            if (!TryGetEntity(factory, entityId, out var entity))
            {
                finishCommand(command, CommandFailed, "target_not_found", "Belt entity was not found.", now, null);
                return;
            }
            if (entity.beltId <= 0)
            {
                finishCommand(command, CommandFailed, "invalid_command", "reverseBelt requires a built belt entity.", now, null);
                return;
            }
            var distance = Vector3.Distance(player.position, entity.pos);
            if (distance > player.mecha.buildArea)
            {
                finishCommand(command, CommandFailed, "out_of_range", "Move within build range of the selected belt.", now,
                    RangeFailureResult(distance, player.mecha.buildArea, entity.pos));
                return;
            }

            var traffic = factory.cargoTraffic;
            var belt = traffic.beltPool[entity.beltId];
            var path = traffic.GetCargoPath(belt.segPathId);
            // 原生按钮仅对 2–1023 个带段的运输路径开放；回调本身没有此保护。
            if (path == null || path.belts.Count <= 1 || path.belts.Count >= 1024)
            {
                finishCommand(command, CommandFailed, "invalid_belt_path", "Native reversal requires a path containing 2 to 1023 belts.", now, null);
                return;
            }
            var entityIds = new List<int>();
            foreach (var id in path.belts) entityIds.Add(traffic.beltPool[id].entityId);

            var previousFactory = window.factory;
            var previousTraffic = window.traffic;
            var previousPlayer = window.player;
            var previousBeltId = window.beltId;
            try
            {
                // 反转实现位于原生窗口回调中；仅临时绑定上下文，不打开窗口。
                // 保留原生货物回收、分拣器偏移及建筑端口重连语义。
                window.factory = factory;
                window.traffic = traffic;
                window.player = player;
                window.beltId = entity.beltId;
                window.OnReverseButtonClick(0);
            }
            finally
            {
                window.factory = previousFactory;
                window.traffic = previousTraffic;
                window.player = previousPlayer;
                window.beltId = previousBeltId;
            }
            var reversed = traffic.beltPool[entity.beltId].outputId == belt.mainInputId;
            finishCommand(command, reversed ? CommandSucceeded : CommandFailed,
                reversed ? null : "reverse_failed", reversed ? null : "Native reversal did not update the belt direction.", now,
                new JsonObject { ["entityId"] = entityId, ["entityIds"] = entityIds,
                    ["previousPathId"] = belt.segPathId, ["pathId"] = traffic.beltPool[entity.beltId].segPathId });
        }
    }
}
