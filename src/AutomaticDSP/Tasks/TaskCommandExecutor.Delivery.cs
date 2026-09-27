using System;
using System.Collections.Generic;
using AutomaticDSP.Serialization;
using static AutomaticDSP.Tasks.TaskStatusNames;

namespace AutomaticDSP.Tasks
{
    internal sealed partial class TaskCommandExecutor
    {
        private void ExecuteDeliveryCommand(CommandState command, DateTimeOffset now)
        {
            if (!TryGetPlayer(out var player, out var code, out var message))
            {
                finishCommand(command, CommandFailed, code, message, now, null);
                return;
            }
            var delivery = player.deliveryPackage;
            if (command.NormalizedType != "transferinventoryitem")
            {
                if (!delivery.unlocked)
                {
                    finishCommand(command, CommandFailed, "tech_locked", "物流背包尚未解锁。", now, null);
                    return;
                }
                if (command.NormalizedType == "setdeliveryenabled")
                {
                    if (!TryGetToken(command, "enabled", out var enabled) || enabled.Type != Newtonsoft.Json.Linq.JTokenType.Boolean)
                    {
                        finishCommand(command, CommandFailed, "invalid_command", "需要布尔值 enabled。", now, null);
                        return;
                    }
                    delivery.enable = GetBool(command, "enabled", false);
                    finishCommand(command, CommandSucceeded, null, null, now, new JsonObject { ["enabled"] = delivery.enable });
                    return;
                }
                var index = GetInt(command, "index", -1);
                var itemId = GetInt(command, "itemId", -1);
                if (index < 0 || index >= delivery.gridLength || !delivery.IsGridActive(index) || itemId < 0 ||
                    (itemId > 0 && (LDB.items.Select(itemId) == null || !GameMain.history.ItemUnlocked(itemId))))
                {
                    finishCommand(command, CommandFailed, "invalid_command", "物流格或物品不可用。", now, null);
                    return;
                }
                var grid = delivery.grids[index];
                if (itemId != grid.itemId && grid.count > 0)
                {
                    finishCommand(command, CommandFailed, "storage_not_empty", "先取出格内物品，再替换或清空配置。", now, null);
                    return;
                }
                for (var i = 0; i < delivery.gridLength; i++)
                    if (i != index && itemId > 0 && delivery.grids[i].itemId == itemId)
                    {
                        finishCommand(command, CommandFailed, "duplicate_item", "物流背包不能配置重复物品。", now, null);
                        return;
                    }
                var require = GetInt(command, "requireCount", itemId == grid.itemId ? grid.requireCount : 0);
                var recycle = GetInt(command, "recycleCount", itemId == grid.itemId ? grid.recycleCount : int.MaxValue);
                var stack = itemId == 0 ? 1 : StorageComponent.itemStackCount[itemId];
                var step = stack % 2 == 0 ? stack / 2 : stack;
                // 与原生滑块一致：半堆（奇数堆为整堆）步长、最多 30 堆及无限阈值。
                if (require < 0 || recycle < require ||
                    (require != int.MaxValue && (require > stack * 30 || require % step != 0)) ||
                    (recycle != int.MaxValue && (recycle > stack * 30 || recycle % step != 0)) ||
                    (itemId == 0 && (require != 0 || recycle != int.MaxValue)))
                {
                    finishCommand(command, CommandFailed, "invalid_command", "补货/回收阈值不符合原生范围或步长；无限使用 2147483647。", now, null);
                    return;
                }
                if (itemId != grid.itemId) player.SetDeliveryItem(index, itemId);
                delivery.grids[index].requireCount = require;
                delivery.grids[index].recycleCount = recycle;
                finishCommand(command, CommandSucceeded, null, null, now, new JsonObject
                {
                    ["index"] = index, ["itemId"] = itemId, ["requireCount"] = require,
                    ["recycleCount"] = recycle, ["count"] = delivery.grids[index].count,
                    ["ordered"] = delivery.grids[index].ordered
                });
                return;
            }

            var from = GetString(command, "from", "");
            var to = GetString(command, "to", "");
            var transferItem = GetInt(command, "itemId", 0);
            var requested = GetInt(command, "count", 0);
            if (from == to || !IsPlayerInventory(from) || !IsPlayerInventory(to) ||
                transferItem <= 0 || LDB.items.Select(transferItem) == null || requested <= 0)
            {
                finishCommand(command, CommandFailed, "invalid_command", "需要不同的 from/to（package、delivery、hand）、物品和正数量。", now, null);
                return;
            }
            if ((from == "delivery" || to == "delivery") && !delivery.unlocked)
            {
                finishCommand(command, CommandFailed, "tech_locked", "物流背包尚未解锁。", now, null);
                return;
            }
            if (to == "hand" && player.inhandItemCount > 0 && player.inhandItemId != transferItem)
            {
                finishCommand(command, CommandFailed, "hand_occupied", "手中已有其他物品。", now, null);
                return;
            }
            var count = TakePlayerInventory(player, from, transferItem, requested, out var inc);
            var moved = AddPlayerInventory(player, to, transferItem, count, inc, out var remainingInc);
            // 只转移目标实际容纳的数量；同一主线程操作内将剩余物品和增产点原样退回来源。
            if (count > moved) AddPlayerInventory(player, from, transferItem, count - moved, remainingInc, out _);
            finishCommand(command, moved > 0 ? CommandSucceeded : CommandFailed,
                moved > 0 ? null : "no_item_transferred", moved > 0 ? null : "来源无物品或目标不能容纳。", now,
                new JsonObject
                {
                    ["itemId"] = transferItem, ["requestedCount"] = requested, ["movedCount"] = moved,
                    ["packageCount"] = player.package.GetItemCount(transferItem),
                    ["deliveryCount"] = delivery.GetItemCount(transferItem),
                    ["handCount"] = player.inhandItemId == transferItem ? player.inhandItemCount : 0
                });
        }

        private static bool IsPlayerInventory(string inventory)
        {
            return inventory == "package" || inventory == "delivery" || inventory == "hand";
        }

        private static Dictionary<int, int> PlayerItemCounts(Player player)
        {
            var counts = StorageItemCounts(player.package);
            foreach (var grid in player.deliveryPackage.grids)
            {
                if (grid.itemId <= 0 || grid.count <= 0) continue;
                counts.TryGetValue(grid.itemId, out var count);
                counts[grid.itemId] = count + grid.count;
            }
            if (player.inhandItemId > 0 && player.inhandItemCount > 0)
            {
                counts.TryGetValue(player.inhandItemId, out var count);
                counts[player.inhandItemId] = count + player.inhandItemCount;
            }
            return counts;
        }

        private static int TakePlayerInventory(Player player, string inventory, int itemId, int count, out int inc)
        {
            inc = 0;
            if (inventory == "package") return player.package.TakeItem(itemId, count, out inc);
            if (inventory == "delivery")
            {
                player.deliveryPackage.TakeItems(ref itemId, ref count, out inc);
                return count;
            }
            if (player.inhandItemId != itemId) return 0;
            count = Math.Min(count, player.inhandItemCount);
            if (count > 0) player.UseHandItems(count, out inc);
            return count;
        }

        private static int AddPlayerInventory(Player player, string inventory, int itemId, int count, int inc, out int remainingInc)
        {
            remainingInc = inc;
            if (count <= 0) return 0;
            int added;
            if (inventory == "package")
            {
                added = player.package.AddItemStacked(itemId, count, inc, out remainingInc);
                player.NotifyPackageAddItem(itemId, added, inc - remainingInc);
            }
            else if (inventory == "delivery")
            {
                added = player.deliveryPackage.AddItem(itemId, count, inc, out remainingInc);
                player.NotifyDeliveryPackageAddItem(itemId, added, inc - remainingInc);
            }
            else
            {
                if (player.inhandItemCount > 0 && player.inhandItemId != itemId) return 0;
                player.SetHandItemId_Unsafe(itemId);
                player.AddHandItemCount_Unsafe(count);
                player.SetHandItemInc_Unsafe(player.inhandItemInc + inc);
                added = count;
                remainingInc = 0;
            }
            return added;
        }
    }
}
