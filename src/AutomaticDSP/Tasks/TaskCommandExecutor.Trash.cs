using System;
using AutomaticDSP.Serialization;
using UnityEngine;
using static AutomaticDSP.Tasks.TaskStatusNames;

namespace AutomaticDSP.Tasks
{
    internal sealed partial class TaskCommandExecutor
    {
        private void ExecuteTrashCommand(CommandState command, DateTimeOffset now)
        {
            if (!TryGetPlayer(out var player, out var code, out var message))
            {
                finishCommand(command, CommandFailed, code, message, now, null);
                return;
            }
            var system = GameMain.data?.trashSystem;
            if (system?.container == null)
            {
                finishCommand(command, CommandFailed, "game_not_ready", "Trash system is unavailable.", now, null);
                return;
            }
            if (command.NormalizedType == "discardinventoryitem")
            {
                if (!TryGetInt(command, "itemId", out var itemId) || LDB.items.Select(itemId) == null ||
                    !TryGetInt(command, "count", out var count) || count <= 0)
                {
                    finishCommand(command, CommandFailed, "invalid_command", "Provide itemId and positive count.", now, null);
                    return;
                }
                // 原生一次最多生成 500 堆，先限制数量，避免扣除后被原生上限截断。
                if (count > (long)LDB.items.Select(itemId).StackSize * 500 || player.package.GetItemCount(itemId) < count)
                {
                    finishCommand(command, CommandFailed, "invalid_count", "Count exceeds inventory or native discard limit.", now, null);
                    return;
                }
                var taken = player.package.TakeItem(itemId, count, out var inc);
                player.ThrowTrash(itemId, taken, inc, 0);
                finishCommand(command, CommandSucceeded, null, null, now,
                    new JsonObject { ["itemId"] = itemId, ["discardedCount"] = taken });
                return;
            }
            if (command.NormalizedType == "cleartrash")
            {
                if (!TryGetString(command, "scope", out var scope) || scope != "all")
                {
                    finishCommand(command, CommandFailed, "invalid_command", "Native clear is global; explicit scope: all is required.", now, null);
                    return;
                }
                system.ClearAllTrash();
                finishCommand(command, CommandSucceeded, null, null, now,
                    new JsonObject { ["scope"] = "all", ["permanent"] = true });
                return;
            }
            var container = system.container;
            if (!TryGetInt(command, "trashId", out var id) || id < 0 || id >= container.trashCursor ||
                container.trashObjPool[id].item <= 0)
            {
                finishCommand(command, CommandFailed, "target_not_found", "Trash no longer exists; refresh state.", now, null);
                return;
            }
            var trash = container.trashObjPool[id];
            // 垃圾槽位会回收复用，要求物品匹配，避免旧 ID 误拾不同物品。
            if (!TryGetInt(command, "itemId", out var expectedItem) || expectedItem != trash.item)
            {
                finishCommand(command, CommandFailed, "target_changed", "Provide the current trash itemId.", now, null);
                return;
            }
            var pick = player.controller?.actionPick;
            if (!player.isAlive || pick == null || !pick.canPick || player.inhandItemId > 0 ||
                VFInput.inFullscreenGUI || VFInput.onGUI || VFInput.onGUIOperate ||
                (pick.pickFilters.TryGetValue(trash.item, out var filter) && filter == 0))
            {
                finishCommand(command, CommandFailed, "pickup_unavailable", "Native player state or pickup filter prevents picking.", now, null);
                return;
            }
            var planet = GameMain.localPlanet;
            var landPlanetId = container.trashDataPool[id].landPlanetId;
            var height = planet == null ? 1000f : player.position.magnitude - planet.realRadius;
            var rangeSquared = Mathf.Clamp(height * height * 0.25f, 4900f, 250000f);
            if ((landPlanetId > 0 && landPlanetId != (planet?.id ?? 0)) ||
                (trash.rPos - player.position).sqrMagnitude >= rangeSquared)
            {
                finishCommand(command, CommandFailed, "out_of_range", "Move within the native pickup range of the trash.", now, null);
                return;
            }
            if (trash.expire >= 0)
            {
                finishCommand(command, CommandFailed, "pickup_in_progress", "Trash is already being picked up.", now, null);
                return;
            }
            // 与原生定点拾取相同：启动吸取，由 TrashSystem.GameTick 入包并处理溢出。
            container.trashObjPool[id].expire = 35;
            GameMain.gameScenario?.NotifyOnTrashPicking();
            finishCommand(command, CommandSucceeded, null, null, now,
                new JsonObject { ["trashId"] = id, ["itemId"] = trash.item,
                    ["count"] = trash.count, ["phase"] = "pickupStarted" });
        }
    }
}
