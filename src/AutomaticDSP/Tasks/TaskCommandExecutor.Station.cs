using System;
using AutomaticDSP.Serialization;
using Newtonsoft.Json.Linq;
using static AutomaticDSP.Tasks.TaskStatusNames;

namespace AutomaticDSP.Tasks
{
    internal sealed partial class TaskCommandExecutor
    {
        private void ExecuteRemoteLogisticsSetting(TaskState task, CommandState command, DateTimeOffset now)
        {
            var player = GameMain.mainPlayer;
            if (player == null)
            {
                finishCommand(command, CommandFailed, "game_not_ready", "玩家不可用。", now, null);
                return;
            }
            var planetId = player.planetId;
            if (TryGetToken(command, "planetId", out var token) &&
                !(token.Type == JTokenType.String && token.Value<string>() == "current") &&
                (token.Type != JTokenType.Integer || !TryGetInt(command, "planetId", out planetId) || planetId <= 0))
            {
                finishCommand(command, CommandFailed, "invalid_command", "planetId 必须为正整数或 current。", now, null);
                return;
            }
            var factory = GameMain.galaxy?.PlanetById(planetId)?.factory;
            if (factory == null)
            {
                finishCommand(command, CommandFailed, "factory_unavailable", "目标行星尚无已建立的工厂；太空中须指定 planetId。", now, null);
                return;
            }
            if (!TryGetTargetEntityId(task, command, out var entityId, out var errorMessage))
            {
                finishCommand(command, CommandFailed, "invalid_command", errorMessage, now, null);
                return;
            }
            if (!TryGetEntity(factory, entityId, out var entity))
            {
                finishCommand(command, CommandFailed, "target_not_found", "目标实体不存在。", now, null);
                return;
            }
            // 原生总控允许远程配置，直接物资搬运仍由各自命令校验本地范围。
            if (command.NormalizedType == "setdispenser" || command.NormalizedType == "setdispensersetting")
            {
                ExecuteDispenserSetting(command, player, factory, entity, now);
                return;
            }
            if (command.NormalizedType == "setveincollectorspeed")
            {
                ExecuteVeinCollectorSpeed(command, factory, entity, now);
                return;
            }
            ExecuteStationSetting(command, player, factory, entity, now);
        }

        private void ExecuteStationSetting(CommandState command, Player player, PlanetFactory factory, EntityData entity, DateTimeOffset now)
        {
            var station = entity.stationId > 0 ? factory.transport.stationPool[entity.stationId] : null;
            if (station == null || ((station.isCollector || station.isVeinCollector) &&
                command.NormalizedType != "setstationstorage" && command.NormalizedType != "transferstationitem"))
            {
                finishCommand(command, CommandFailed, "invalid_station", "需要普通行星或星际运输站。", now, null);
                return;
            }
            if (command.NormalizedType == "setstationsetting")
            {
                ExecuteStationOption(command, player, factory, entity, station, now);
                return;
            }
            var desc = LDB.items.Select(entity.protoId).prefabDesc;
            if (command.NormalizedType == "setstationchargepower")
            {
                // 对齐原生充电滑块的范围与 3 MW 刻度，不修改站内储能或实际供电。
                var minPowerMW = desc.workEnergyPerTick / 2 / 50000 * 3;
                var maxPowerMW = desc.workEnergyPerTick * 5 / 50000 * 3;
                if (!TryGetToken(command, "powerMW", out var token) || token.Type != JTokenType.Integer ||
                    !int.TryParse(token.ToString(), out var powerMW) || powerMW % 3 != 0 ||
                    powerMW < minPowerMW || powerMW > maxPowerMW)
                {
                    finishCommand(command, CommandFailed, "invalid_command", "powerMW 必须为原生范围内的整数，且为 3 的倍数。", now,
                        new JsonObject { ["minPowerMW"] = minPowerMW, ["maxPowerMW"] = maxPowerMW });
                    return;
                }
                ref var consumer = ref factory.powerSystem.consumerPool[station.pcId];
                var previousPowerMW = consumer.workEnergyPerTick * 60.0 / 1000000.0;
                consumer.workEnergyPerTick = (long)powerMW / 3 * 50000;
                finishCommand(command, CommandSucceeded, null, null, now, new JsonObject
                {
                    ["entityId"] = entity.id, ["previousPowerMW"] = previousPowerMW,
                    ["powerMW"] = powerMW, ["workEnergyPerTick"] = consumer.workEnergyPerTick
                });
                return;
            }
            if (command.NormalizedType == "transferstationitem")
            {
                var itemId = GetInt(command, "itemId", 0);
                var requestedItems = GetInt(command, "count", 0);
                var direction = GetString(command, "direction", "fromStation");
                var inventory = GetString(command, "inventory", "package");
                var index = Array.FindIndex(station.storage, store => store.itemId == itemId && itemId > 0);
                if (index < 0 || requestedItems <= 0 || !IsPlayerInventory(inventory) ||
                    (direction != "fromStation" && direction != "toStation"))
                {
                    finishCommand(command, CommandFailed, "invalid_command", "需要已配置物品、正数量、背包类型和 fromStation/toStation 方向。", now, null);
                    return;
                }
                if (inventory == "delivery" && !player.deliveryPackage.unlocked)
                {
                    finishCommand(command, CommandFailed, "tech_locked", "物流背包尚未解锁。", now, null);
                    return;
                }
                int taken, inc, movedItems;
                if (direction == "toStation")
                {
                    // 原生手动投料使用科技允许的总容量，不受物流需求上限滑块限制。
                    var capacity = desc.stationMaxItemCount + (station.isStellar && !station.isCollector && !station.isVeinCollector
                        ? GameMain.history.remoteStationExtraStorage : GameMain.history.localStationExtraStorage);
                    var count = Math.Min(requestedItems, Math.Max(0, capacity - station.storage[index].count));
                    inc = 0;
                    taken = count == 0 ? 0 : TakePlayerInventory(player, inventory, itemId, count, out inc);
                    movedItems = station.AddItem(itemId, taken, inc);
                }
                else
                {
                    var id = itemId;
                    taken = requestedItems;
                    station.TakeItem(ref id, ref taken, out inc);
                    movedItems = AddPlayerInventory(player, inventory, itemId, taken, inc, out var remainingInc);
                    if (taken > movedItems) station.AddItem(itemId, taken - movedItems, remainingInc);
                }
                finishCommand(command, movedItems > 0 ? CommandSucceeded : CommandFailed,
                    movedItems > 0 ? null : "no_item_transferred", movedItems > 0 ? null : "来源无物品或目标不能容纳。", now,
                    new JsonObject { ["entityId"] = entity.id, ["itemId"] = itemId, ["requestedCount"] = requestedItems,
                        ["movedCount"] = movedItems, ["stationCount"] = station.storage[index].count });
                return;
            }
            if (command.NormalizedType == "setstationstorage")
            {
                var index = GetInt(command, "storageIndex", -1);
                var itemId = GetInt(command, "itemId", -1);
                var collecting = station.isCollector || station.isVeinCollector;
                var capacity = desc.stationMaxItemCount + (station.isStellar && !collecting ? GameMain.history.remoteStationExtraStorage : GameMain.history.localStationExtraStorage);
                var max = GetInt(command, "max", capacity);
                if (index < 0 || index >= station.storage.Length || itemId < 0 ||
                    (itemId > 0 && (LDB.items.Select(itemId) == null || (!collecting && !GameMain.history.ItemUnlocked(itemId)))) ||
                    max < 0 || max > capacity ||
                    !Enum.TryParse(GetString(command, "localLogic", "None"), true, out ELogisticStorage local) || !Enum.IsDefined(typeof(ELogisticStorage), local) ||
                    !Enum.TryParse(GetString(command, "remoteLogic", "None"), true, out ELogisticStorage remote) || !Enum.IsDefined(typeof(ELogisticStorage), remote) ||
                    (!station.isStellar && remote != ELogisticStorage.None))
                {
                    finishCommand(command, CommandFailed, "invalid_command", "货槽、物品、容量或物流模式无效。", now, null);
                    return;
                }
                // 采集槽由原生产物决定，总控只允许供应或仓储，不能改成需求或替换产物。
                if (collecting && (itemId <= 0 || itemId != station.storage[index].itemId ||
                    (station.isVeinCollector && (local == ELogisticStorage.Demand || remote != ELogisticStorage.None)) ||
                    (station.isCollector && (remote == ELogisticStorage.Demand || local != station.storage[index].localLogic))))
                {
                    finishCommand(command, CommandFailed, "invalid_command", "采集槽必须保留原物品；大矿机仅支持本地供应/仓储，轨道采集器仅支持星际供应/仓储。", now, null);
                    return;
                }
                for (var i = 0; i < station.storage.Length; i++)
                    if (i != index && itemId > 0 && station.storage[i].itemId == itemId)
                    {
                        finishCommand(command, CommandFailed, "duplicate_item", "运输站不能选择重复物品。", now, null);
                        return;
                    }
                // 原生方法负责退回旧货物、清理订单及刷新物流配对。
                var previous = station.storage[index];
                if (factory.planet != GameMain.localPlanet && previous.itemId != itemId && previous.count > 0)
                {
                    // 对齐原生远程重置：旧货物留在来源行星成为垃圾，不远程返还到机甲。
                    GameMain.data.trashSystem.AddTrashOnPlanet(previous.itemId, previous.count, previous.inc, station.entityId, factory.planet);
                    factory.transport.SetStationStorage(station.id, index, 0, 0, ELogisticStorage.None, ELogisticStorage.None, null);
                }
                factory.transport.SetStationStorage(station.id, index, itemId, max, local, remote, player);
                var store = station.storage[index];
                finishCommand(command, CommandSucceeded, null, null, now, new JsonObject
                {
                    ["planetId"] = factory.planetId, ["entityId"] = entity.id, ["storageIndex"] = index, ["itemId"] = store.itemId,
                    ["max"] = store.max, ["count"] = store.count,
                    ["localLogic"] = store.localLogic.ToString(), ["remoteLogic"] = store.remoteLogic.ToString()
                });
                return;
            }

            var vehicleId = GetInt(command, "itemId", 0);
            var requested = GetInt(command, "count", -1);
            var ship = vehicleId == 5002;
            var limit = ship ? desc.stationMaxShipCount : desc.stationMaxDroneCount;
            if ((vehicleId != 5001 && vehicleId != 5002) || (ship && !station.isStellar) || requested < 0 || requested > limit)
            {
                finishCommand(command, CommandFailed, "invalid_command", "运输工具类型或目标总数无效。", now, null);
                return;
            }
            var working = ship ? station.workShipCount : station.workDroneCount;
            var idle = ship ? station.idleShipCount : station.idleDroneCount;
            if (requested < working)
            {
                finishCommand(command, CommandFailed, "vehicles_busy", "不能取回执行运输中的工具。", now, null);
                return;
            }
            var delta = requested - working - idle;
            if (delta > player.package.GetItemCount(vehicleId))
            {
                finishCommand(command, CommandFailed, "insufficient_items", "背包中的运输工具不足。", now, null);
                return;
            }
            // 对齐原生窗口的容量与闲置数量约束；每次增加必须对应背包实际扣除。
            var moved = delta > 0 ? player.package.TakeItem(vehicleId, delta, out _) : 0;
            if (delta < 0)
            {
                // 返回值仅统计普通背包；剩余物品已由原生转入物流背包或手中，必须全部扣除。
                player.TryAddItemToPackage(vehicleId, -delta, 0, false);
                moved = delta;
            }
            if (ship) station.idleShipCount += moved;
            else station.idleDroneCount += moved;
            finishCommand(command, CommandSucceeded, null, null, now, new JsonObject
            {
                ["entityId"] = entity.id, ["itemId"] = vehicleId, ["requestedCount"] = requested,
                ["count"] = working + idle + moved, ["workingCount"] = working, ["movedCount"] = moved,
                ["inventoryCount"] = player.package.GetItemCount(vehicleId),
                ["deliveryCount"] = player.deliveryPackage.GetItemCount(vehicleId),
                ["handCount"] = player.inhandItemId == vehicleId ? player.inhandItemCount : 0
            });
        }
    }
}
