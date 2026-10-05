using System;
using Newtonsoft.Json.Linq;
using UnityEngine;
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
            if (command.NormalizedType == "setdispensersetting")
            {
                var setting = GetString(command, "setting", "");
                if (!TryGetToken(command, "value", out var value))
                {
                    finishCommand(command, CommandFailed, "invalid_command", "需要 setting 与 value。", now, null);
                    return;
                }
                var result = new JsonObject { ["planetId"] = factory.planetId, ["entityId"] = entity.id, ["setting"] = setting };
                if (setting == "courierAutoReplenish" && value.Type == JTokenType.Boolean)
                {
                    var before = player.package.GetItemCount(5003);
                    dispenser.courierAutoReplenish = value.Value<bool>();
                    // 使用总控原生补充路径，真实扣除普通背包，而非将货箱库存变成运力。
                    factory.EntityAutoReplenishIfNeeded(entity.id, Vector2.zero);
                    result["value"] = dispenser.courierAutoReplenish;
                    result["consumedCount"] = before - player.package.GetItemCount(5003);
                    result["inventoryCount"] = player.package.GetItemCount(5003);
                    result["courierCount"] = dispenser.idleCourierCount + dispenser.workCourierCount;
                }
                else if (setting == "chargePowerKW")
                {
                    var work = LDB.items.Select(entity.protoId).prefabDesc.workEnergyPerTick;
                    var min = work / 2 / 5000 * 300;
                    var max = work * 5 / 5000 * 300;
                    result["minPowerKW"] = min;
                    result["maxPowerKW"] = max;
                    if (value.Type != JTokenType.Integer || !int.TryParse(value.ToString(), out var kw) ||
                        kw < min || kw > max || kw % 300 != 0)
                    {
                        finishCommand(command, CommandFailed, "invalid_command", "充电功率必须是原生范围内的整数千瓦，且为 300 的倍数。", now, result);
                        return;
                    }
                    factory.powerSystem.consumerPool[dispenser.pcId].workEnergyPerTick = (long)kw / 300 * 5000;
                    result["value"] = kw;
                    result["workEnergyPerTick"] = factory.powerSystem.consumerPool[dispenser.pcId].workEnergyPerTick;
                }
                else
                {
                    finishCommand(command, CommandFailed, "invalid_command", "设置名称或值类型无效。", now, null);
                    return;
                }
                finishCommand(command, CommandSucceeded, null, null, now, result);
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
