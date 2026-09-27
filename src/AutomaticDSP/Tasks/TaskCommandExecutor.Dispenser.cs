using System;
using AutomaticDSP.Serialization;
using static AutomaticDSP.Tasks.TaskStatusNames;

namespace AutomaticDSP.Tasks
{
    internal sealed partial class TaskCommandExecutor
    {
        private void ExecuteDispenserSetting(CommandState command, Player player, PlanetFactory factory, EntityData entity, DateTimeOffset now)
        {
            var dispenser = entity.dispenserId > 0 ? factory.transport.dispenserPool[entity.dispenserId] : null;
            if (dispenser == null)
            {
                finishCommand(command, CommandFailed, "invalid_dispenser", "目标不是物流配送器。", now, null);
                return;
            }
            if (command.NormalizedType == "setdispenser")
            {
                var filter = GetInt(command, "itemId", dispenser.filter);
                if (!Enum.TryParse(GetString(command, "playerMode", dispenser.playerMode.ToString()), true, out EPlayerDeliveryMode playerMode) ||
                    !Enum.IsDefined(typeof(EPlayerDeliveryMode), playerMode) ||
                    !Enum.TryParse(GetString(command, "storageMode", dispenser.storageMode.ToString()), true, out EStorageDeliveryMode storageMode) ||
                    !Enum.IsDefined(typeof(EStorageDeliveryMode), storageMode) || filter == 1099 ||
                    (filter < -1 && (filter == int.MinValue || LDB.items.Select(-filter) == null)) ||
                    (filter > 0 && (LDB.items.Select(filter) == null || !GameMain.history.ItemUnlocked(filter))) ||
                    (filter < 0 && (playerMode != EPlayerDeliveryMode.Recycle || storageMode != EStorageDeliveryMode.None)))
                {
                    finishCommand(command, CommandFailed, "invalid_command", "物品或配送模式无效；-1 仅用于只面向机甲的全部回收。", now, null);
                    return;
                }
                factory.transport.SetDispenserFilter(dispenser.id, filter);
                factory.transport.SetDispenserPlayerDeliveryMode(dispenser.id, playerMode);
                factory.transport.SetDispenserStorageDeliveryMode(dispenser.id, storageMode);
                finishCommand(command, CommandSucceeded, null, null, now, new JsonObject
                {
                    ["entityId"] = entity.id, ["dispenserId"] = dispenser.id, ["storageId"] = dispenser.storageId,
                    ["itemId"] = dispenser.filter, ["playerMode"] = dispenser.playerMode.ToString(),
                    ["storageMode"] = dispenser.storageMode.ToString(), ["pairCount"] = dispenser.pairCount
                });
                return;
            }
            var requested = GetInt(command, "count", -1);
            var limit = LDB.items.Select(entity.protoId).prefabDesc.dispenserMaxCourierCount;
            if (requested < 0 || requested > limit || requested < dispenser.workCourierCount)
            {
                finishCommand(command, CommandFailed, requested >= 0 && requested < dispenser.workCourierCount ? "vehicles_busy" : "invalid_command",
                    "目标总数必须在原生容量内，且不能取回工作中的机器人。", now, null);
                return;
            }
            var delta = requested - dispenser.workCourierCount - dispenser.idleCourierCount;
            if (delta > player.package.GetItemCount(5003))
            {
                finishCommand(command, CommandFailed, "insufficient_items", "普通背包中的物流机器人不足。", now, null);
                return;
            }
            if (delta > 0) delta = player.package.TakeItem(5003, delta, out _);
            else if (delta < 0) player.TryAddItemToPackage(5003, -delta, 0, false);
            dispenser.idleCourierCount += delta;
            finishCommand(command, CommandSucceeded, null, null, now, new JsonObject
            {
                ["entityId"] = entity.id, ["count"] = dispenser.idleCourierCount + dispenser.workCourierCount,
                ["workingCount"] = dispenser.workCourierCount, ["movedCount"] = delta,
                ["inventoryCount"] = player.package.GetItemCount(5003),
                ["deliveryCount"] = player.deliveryPackage.GetItemCount(5003),
                ["handCount"] = player.inhandItemId == 5003 ? player.inhandItemCount : 0
            });
        }
    }
}
