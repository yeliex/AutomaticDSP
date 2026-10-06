using System;
using AutomaticDSP.Serialization;
using UnityEngine;
using static AutomaticDSP.Tasks.TaskStatusNames;

namespace AutomaticDSP.Tasks
{
    internal sealed partial class TaskCommandExecutor
    {
        private void ExecuteMoveToLocked(CommandState command, DateTimeOffset now)
        {
            if (!TryGetPlayer(out var player, out var errorCode, out var errorMessage) ||
                !TryValidateCurrentPlanet(command, player, out errorCode, out errorMessage))
            {
                finishCommand(command, CommandFailed, errorCode, errorMessage, now, null);
                return;
            }
            if (!TryGetVector(command, "position", out var target, out errorMessage))
            {
                finishCommand(command, CommandFailed, "invalid_command", errorMessage, now, null);
                return;
            }
            if (command.ActionIssued && player.planetId != command.MovePlanetId)
            {
                StopCommandEffects(command);
                finishCommand(command, CommandFailed, "planet_changed", "移动期间当前行星发生变化。", now, command.Result);
                return;
            }

            var distance = Vector3.Distance(player.position, target);
            var tolerance = Math.Max(0.1, GetDouble(command, "tolerance", 2.0));
            var tick = GameMain.gameTick;
            var overrideReason = MovementControl.OverrideReason(player, true, command.NativeMoveOrder);
            command.Result = new JsonObject
            {
                ["distance"] = distance,
                ["target"] = Vector(target),
                ["overrideReason"] = overrideReason
            };
            if (overrideReason != null)
            {
                // 仅撤销本任务的订单，保留人工订单；接管期间不累计停滞时间。
                StopCommandEffects(command);
                command.Phase = "manualOverride";
                command.MoveProgressTick = tick;
                command.MoveProgressDistance = distance;
                return;
            }
            if (distance <= tolerance)
            {
                StopCommandEffects(command);
                finishCommand(command, CommandSucceeded, null, null, now, command.Result);
                return;
            }

            if (!command.ActionIssued || command.Phase == "manualOverride" ||
                tick < command.MoveProgressTick || distance <= command.MoveProgressDistance - 0.5)
            {
                command.MoveProgressTick = tick;
                command.MoveProgressDistance = distance;
            }
            command.Phase = "approaching";
            // 使用游戏时间排除暂停；短暂碰撞抖动或重复下达不能重置有效推进判定。
            if (tick - command.MoveProgressTick >= 600)
            {
                StopCommandEffects(command);
                finishCommand(command, CommandFailed, "movement_stuck", "连续 10 秒游戏时间未有效接近移动目标。", now, command.Result);
                return;
            }
            if (!command.ActionIssued || !ReferenceEquals(player.currentOrder, command.NativeMoveOrder) ||
                command.NativeMoveOrder.targetReached)
            {
                command.NativeMoveOrder = OrderNode.MoveTo(target);
                player.Order(command.NativeMoveOrder, false);
                if (!command.ActionIssued) command.MovePlanetId = player.planetId;
                command.ActionIssued = true;
            }
        }
    }
}
