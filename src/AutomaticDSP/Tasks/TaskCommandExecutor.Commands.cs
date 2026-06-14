using System;
using System.Collections.Generic;
using AutomaticDSP.State;
using AutomaticDSP.Serialization;
using UnityEngine;
using static AutomaticDSP.Tasks.TaskStatusNames;

namespace AutomaticDSP.Tasks
{
    internal sealed partial class TaskCommandExecutor
    {
        private void ExecuteNoopLocked(CommandState command, DateTimeOffset now)
        {
            command.Phase = "waitingCondition";

            var durationSeconds = Math.Max(0, command.Request.DurationSeconds ?? 0);
            if (durationSeconds > 0 && command.StartedAt.HasValue && now < command.StartedAt.Value.AddSeconds(durationSeconds))
            {
                return;
            }

            var durationTicks = Math.Max(0, command.Request.DurationTicks ?? 0);
            var currentTick = CurrentGameTick();
            if (durationTicks > 0 &&
                command.StartGameTick.HasValue &&
                currentTick.HasValue &&
                currentTick.Value < command.StartGameTick.Value + durationTicks)
            {
                return;
            }

            finishCommand(
                command,
                CommandSucceeded,
                null,
                null,
                now,
                new JsonObject { ["completed"] = true });
        }

        private void ExecuteWaitUntilLocked(CommandState command, DateTimeOffset now)
        {
            command.Phase = "waitingCondition";
            if (command.Request.Condition == null)
            {
                finishCommand(command, CommandFailed, "invalid_command", "waitUntil requires a condition.", now, null);
                return;
            }

            if (!TryEvaluateWaitCondition(command, now, out var isSatisfied, out var errorCode, out var errorMessage))
            {
                finishCommand(command, CommandFailed, errorCode, errorMessage, now, null);
                return;
            }

            if (!isSatisfied)
            {
                return;
            }

            finishCommand(
                command,
                CommandSucceeded,
                null,
                null,
                now,
                new JsonObject { ["conditionMet"] = true });
        }

        private void ExecuteMoveToLocked(CommandState command, DateTimeOffset now)
        {
            if (!TryGetPlayer(out var player, out var errorCode, out var errorMessage))
            {
                finishCommand(command, CommandFailed, errorCode, errorMessage, now, null);
                return;
            }

            if (!TryValidateCurrentPlanet(command, player, out errorCode, out errorMessage))
            {
                finishCommand(command, CommandFailed, errorCode, errorMessage, now, null);
                return;
            }

            if (!TryGetVector(command, "position", out var target, out errorMessage))
            {
                finishCommand(command, CommandFailed, "invalid_command", errorMessage, now, null);
                return;
            }

            var tolerance = Math.Max(0.1, GetDouble(command, "tolerance", 2.0));
            command.Phase = "approaching";
            if (!command.ActionIssued)
            {
                player.Order(OrderNode.MoveTo(target), false);
                command.ActionIssued = true;
            }

            var distance = Vector3.Distance(player.position, target);
            if (distance > tolerance)
            {
                command.Result = new JsonObject
                {
                    ["distance"] = distance,
                    ["target"] = Vector(target)
                };
                return;
            }

            player.ClearOrders();
            finishCommand(
                command,
                CommandSucceeded,
                null,
                null,
                now,
                new JsonObject
                {
                    ["distance"] = distance,
                    ["target"] = Vector(target)
                });
        }

        private void ExecuteMineTargetLocked(CommandState command, DateTimeOffset now)
        {
            if (!TryGetPlayer(out var player, out var errorCode, out var errorMessage))
            {
                finishCommand(command, CommandFailed, errorCode, errorMessage, now, null);
                return;
            }

            if (!TryValidateCurrentPlanet(command, player, out errorCode, out errorMessage))
            {
                finishCommand(command, CommandFailed, errorCode, errorMessage, now, null);
                return;
            }

            var factory = GameMain.localPlanet?.factory;
            if (factory == null)
            {
                finishCommand(command, CommandFailed, "game_not_ready", "Current planet factory is not loaded.", now, null);
                return;
            }

            if (!TryGetString(command, "targetType", out var targetType))
            {
                finishCommand(command, CommandFailed, "invalid_command", "mineTarget requires targetType.", now, null);
                return;
            }

            if (!TryGetInt(command, "targetId", out var targetId) || targetId <= 0)
            {
                finishCommand(command, CommandFailed, "invalid_command", "mineTarget requires a positive targetId.", now, null);
                return;
            }

            if (!command.ActionIssued)
            {
                if (!TryPrepareMineTarget(command, factory, targetType, targetId, out errorCode, out errorMessage))
                {
                    finishCommand(command, CommandFailed, errorCode, errorMessage, now, null);
                    return;
                }

                SnapshotMiningInventory(command);
                command.InitialInventoryCount = command.TargetItemId > 0 ? InventoryCount(command.TargetItemId) : 0;
                var requestedCount = Math.Max(0, GetInt(command, "itemCount", GetInt(command, "count", 1)));
                command.TargetInventoryCount = command.TargetItemId > 0 ? command.InitialInventoryCount + requestedCount : 0;
                command.UntilDepleted = GetBool(command, "untilDepleted", command.UntilDepleted);
                if (!IsWithinCommandIssueRange(player, command.TargetPosition, out var distance, out var range))
                {
                    finishCommand(
                        command,
                        CommandFailed,
                        "out_of_range",
                        "Mining target is outside command issue range. Move closer before submitting mineTarget.",
                        now,
                        RangeFailureResult(distance, range, command.TargetPosition));
                    return;
                }

                command.Phase = "approaching";
                player.Order(OrderNode.MineTarget(command.TargetPosition, command.MineObjectType, targetId, command.ObjectPosition), false);
                command.ActionIssued = true;
            }

            command.Phase = "harvesting";
            var currentCount = command.TargetItemId > 0 ? InventoryCount(command.TargetItemId) : 0;
            if (command.TargetItemId > 0 && currentCount >= command.TargetInventoryCount && !command.UntilDepleted)
            {
                player.ClearOrders();
                finishCommand(
                    command,
                    CommandSucceeded,
                    null,
                    null,
                    now,
                    MiningResult(command, depleted: false));
                return;
            }

            if (IsMineTargetDepleted(command, factory))
            {
                if (command.UntilDepleted || command.TargetItemId <= 0 || currentCount > command.InitialInventoryCount)
                {
                    finishCommand(
                        command,
                        CommandSucceeded,
                        null,
                        null,
                        now,
                        MiningResult(command, depleted: true));
                }
                else
                {
                    finishCommand(command, CommandFailed, "target_depleted", "Mining target disappeared before requested items were collected.", now, null);
                }

                return;
            }

            if (IsMineOrderInterrupted(player))
            {
                player.Order(OrderNode.MineTarget(command.TargetPosition, command.MineObjectType, command.TargetId, command.ObjectPosition), false);
            }
        }

        private void ExecuteAutoReplenishMechaFuelLocked(CommandState command, DateTimeOffset now)
        {
            if (!TryGetPlayer(out var player, out var errorCode, out var errorMessage))
            {
                finishCommand(command, CommandFailed, errorCode, errorMessage, now, null);
                return;
            }

            var mecha = player.mecha;
            if (mecha?.reactorStorage == null)
            {
                finishCommand(command, CommandFailed, "game_not_ready", "Mecha reactor storage is not available.", now, null);
                return;
            }

            command.Phase = "replenishingFuel";
            var before = StorageItemCounts(mecha.reactorStorage);
            var reactorEnergyBefore = mecha.reactorEnergy;
            var reactorItemIdBefore = mecha.reactorItemId;
            mecha.AutoReplenishFuelAll();

            var after = StorageItemCounts(mecha.reactorStorage);
            var moved = PositiveItemDeltas(before, after);
            var result = new JsonObject
            {
                ["method"] = "Mecha.AutoReplenishFuelAll",
                ["reactorEnergyBefore"] = reactorEnergyBefore,
                ["reactorEnergyAfter"] = mecha.reactorEnergy,
                ["reactorItemIdBefore"] = reactorItemIdBefore == 0 ? (object)null : reactorItemIdBefore,
                ["reactorItemIdAfter"] = mecha.reactorItemId == 0 ? (object)null : mecha.reactorItemId,
                ["movedCount"] = TotalItemCount(moved),
                ["movedItems"] = ItemCountList(moved),
                ["reactorItems"] = ItemCountList(after)
            };

            if (TotalItemCount(moved) <= 0 && TotalItemCount(after) <= 0)
            {
                finishCommand(
                    command,
                    CommandFailed,
                    "no_fuel_moved",
                    "No mecha fuel was moved into reactor storage.",
                    now,
                    result);
                return;
            }

            finishCommand(command, CommandSucceeded, null, null, now, result);
        }

        private void ExecuteEntityFastFillInLocked(TaskState task, CommandState command, DateTimeOffset now)
        {
            if (!TryGetPlayer(out var player, out var errorCode, out var errorMessage))
            {
                finishCommand(command, CommandFailed, errorCode, errorMessage, now, null);
                return;
            }

            if (!TryValidateCurrentPlanet(command, player, out errorCode, out errorMessage))
            {
                finishCommand(command, CommandFailed, errorCode, errorMessage, now, null);
                return;
            }

            var factory = GameMain.localPlanet?.factory;
            if (factory == null)
            {
                finishCommand(command, CommandFailed, "game_not_ready", "Current planet factory is not loaded.", now, null);
                return;
            }

            if (!TryGetTargetEntityId(task, command, out var entityId, out errorMessage))
            {
                finishCommand(command, CommandFailed, "invalid_command", errorMessage, now, null);
                return;
            }

            if (!TryGetEntity(factory, entityId, out var entity))
            {
                finishCommand(command, CommandFailed, "target_not_found", $"Entity not found: {entityId}", now, null);
                return;
            }

            command.TargetId = entityId;
            command.ObjectPosition = entity.pos;
            command.TargetPosition = MiningStandPosition(entity.pos);
            if (!IsWithinCommandIssueRange(player, entity.pos, out var commandDistance, out var commandRange))
            {
                finishCommand(
                    command,
                    CommandFailed,
                    "out_of_range",
                    "Entity is outside command issue range. Move closer before submitting entityFastFillIn.",
                    now,
                    RangeFailureResult(commandDistance, commandRange, entity.pos));
                return;
            }

            if (!IsPlayerInInteractionRange(player, entity.pos, out var distance, out var range))
            {
                command.Phase = "approaching";
                if (!command.ActionIssued)
                {
                    player.Order(OrderNode.MoveTo(command.TargetPosition), false);
                    command.ActionIssued = true;
                }

                return;
            }

            player.ClearOrders();
            command.Phase = "fastFilling";
            var fromPackage = GetBool(command, "fromPackage", true);
            factory.EntityFastFillIn(entityId, fromPackage, out var itemBundle);
            var moved = ItemBundleCounts(itemBundle);
            var result = new JsonObject
            {
                ["method"] = "PlanetFactory.EntityFastFillIn",
                ["entityId"] = entityId,
                ["fromPackage"] = fromPackage,
                ["movedCount"] = TotalItemCount(moved),
                ["movedItems"] = ItemCountList(moved),
                ["entity"] = EntityFastFillSummary(factory, entityId)
            };

            if (TotalItemCount(moved) <= 0)
            {
                finishCommand(
                    command,
                    CommandFailed,
                    "no_item_transferred",
                    "No item was transferred by EntityFastFillIn.",
                    now,
                    result);
                return;
            }

            finishCommand(command, CommandSucceeded, null, null, now, result);
        }

        private void ExecuteEntityFastTakeOutLocked(TaskState task, CommandState command, DateTimeOffset now)
        {
            if (!TryGetPlayer(out var player, out var errorCode, out var errorMessage))
            {
                finishCommand(command, CommandFailed, errorCode, errorMessage, now, null);
                return;
            }

            if (!TryValidateCurrentPlanet(command, player, out errorCode, out errorMessage))
            {
                finishCommand(command, CommandFailed, errorCode, errorMessage, now, null);
                return;
            }

            var factory = GameMain.localPlanet?.factory;
            if (factory == null)
            {
                finishCommand(command, CommandFailed, "game_not_ready", "Current planet factory is not loaded.", now, null);
                return;
            }

            if (!TryGetTargetEntityId(task, command, out var entityId, out errorMessage))
            {
                finishCommand(command, CommandFailed, "invalid_command", errorMessage, now, null);
                return;
            }

            if (!TryGetEntity(factory, entityId, out var entity))
            {
                finishCommand(command, CommandFailed, "target_not_found", $"Entity not found: {entityId}", now, null);
                return;
            }

            command.TargetId = entityId;
            command.ObjectPosition = entity.pos;
            command.TargetPosition = MiningStandPosition(entity.pos);
            if (!IsWithinCommandIssueRange(player, entity.pos, out var commandDistance, out var commandRange))
            {
                finishCommand(
                    command,
                    CommandFailed,
                    "out_of_range",
                    "Entity is outside command issue range. Move closer before submitting entityFastTakeOut.",
                    now,
                    RangeFailureResult(commandDistance, commandRange, entity.pos));
                return;
            }

            if (!IsPlayerInInteractionRange(player, entity.pos, out var distance, out var range))
            {
                command.Phase = "approaching";
                if (!command.ActionIssued)
                {
                    player.Order(OrderNode.MoveTo(command.TargetPosition), false);
                    command.ActionIssued = true;
                }

                return;
            }

            player.ClearOrders();
            command.Phase = "fastTakingOut";
            var toPackage = GetBool(command, "toPackage", true);
            factory.EntityFastTakeOut(entityId, toPackage, out var itemBundle, out var full);
            var moved = ItemBundleCounts(itemBundle);
            var result = new JsonObject
            {
                ["method"] = "PlanetFactory.EntityFastTakeOut",
                ["entityId"] = entityId,
                ["toPackage"] = toPackage,
                ["full"] = full,
                ["movedCount"] = TotalItemCount(moved),
                ["movedItems"] = ItemCountList(moved),
                ["entity"] = EntityFastFillSummary(factory, entityId)
            };

            if (TotalItemCount(moved) <= 0)
            {
                finishCommand(
                    command,
                    CommandFailed,
                    "no_item_transferred",
                    "No item was transferred by EntityFastTakeOut.",
                    now,
                    result);
                return;
            }

            finishCommand(command, CommandSucceeded, null, null, now, result);
        }

        private void ExecuteTransferStorageItemLocked(TaskState task, CommandState command, DateTimeOffset now)
        {
            if (!TryGetPlayer(out var player, out var errorCode, out var errorMessage))
            {
                finishCommand(command, CommandFailed, errorCode, errorMessage, now, null);
                return;
            }

            if (!TryValidateCurrentPlanet(command, player, out errorCode, out errorMessage))
            {
                finishCommand(command, CommandFailed, errorCode, errorMessage, now, null);
                return;
            }

            var factory = GameMain.localPlanet?.factory;
            if (factory == null)
            {
                finishCommand(command, CommandFailed, "game_not_ready", "Current planet factory is not loaded.", now, null);
                return;
            }

            if (!TryGetTargetEntityId(task, command, out var entityId, out errorMessage))
            {
                finishCommand(command, CommandFailed, "invalid_command", errorMessage, now, null);
                return;
            }

            if (!TryGetInt(command, "itemId", out var itemId) || itemId <= 0)
            {
                finishCommand(command, CommandFailed, "invalid_command", "transferStorageItem requires itemId.", now, null);
                return;
            }

            var count = GetInt(command, "count", 0);
            if (count <= 0)
            {
                finishCommand(command, CommandFailed, "invalid_command", "transferStorageItem requires positive count.", now, null);
                return;
            }

            var direction = GetString(command, "direction", "fromStorage");
            if (!string.Equals(direction, "fromStorage", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(direction, "toStorage", StringComparison.OrdinalIgnoreCase))
            {
                finishCommand(command, CommandFailed, "invalid_command", "direction must be fromStorage or toStorage.", now, null);
                return;
            }

            if (!TryGetStorage(factory, entityId, out var storage, out errorMessage))
            {
                finishCommand(command, CommandFailed, "target_not_found", errorMessage, now, null);
                return;
            }

            if (!TryGetEntity(factory, entityId, out var entity))
            {
                finishCommand(command, CommandFailed, "target_not_found", $"Entity not found: {entityId}", now, null);
                return;
            }

            command.TargetId = entityId;
            command.ObjectPosition = entity.pos;
            command.TargetPosition = MiningStandPosition(entity.pos);
            if (!IsWithinCommandIssueRange(player, entity.pos, out var commandDistance, out var commandRange))
            {
                finishCommand(
                    command,
                    CommandFailed,
                    "out_of_range",
                    "Entity is outside command issue range. Move closer before submitting transferStorageItem.",
                    now,
                    RangeFailureResult(commandDistance, commandRange, entity.pos));
                return;
            }

            if (!IsPlayerInInteractionRange(player, entity.pos, out var distance, out var range))
            {
                command.Phase = "approaching";
                if (!command.ActionIssued)
                {
                    player.Order(OrderNode.MoveTo(command.TargetPosition), false);
                    command.ActionIssued = true;
                }

                return;
            }

            player.ClearOrders();
            command.Phase = "transferring";
            var moved = string.Equals(direction, "fromStorage", StringComparison.OrdinalIgnoreCase)
                ? TransferFromStorage(storage, player.package, itemId, count)
                : TransferToStorage(player.package, storage, itemId, count);

            var result = new JsonObject
            {
                ["method"] = direction,
                ["entityId"] = entityId,
                ["itemId"] = itemId,
                ["itemName"] = ItemName(itemId),
                ["requestedCount"] = count,
                ["movedCount"] = moved,
                ["inventoryCount"] = InventoryCount(itemId),
                ["storageCount"] = storage.GetItemCount(itemId)
            };

            if (moved <= 0)
            {
                finishCommand(command, CommandFailed, "no_item_transferred", "No item was transferred.", now, result);
                return;
            }

            finishCommand(command, CommandSucceeded, null, null, now, result);
        }

        private void ExecuteDismantleEntityLocked(TaskState task, CommandState command, DateTimeOffset now)
        {
            if (!TryGetPlayer(out var player, out var errorCode, out var errorMessage))
            {
                finishCommand(command, CommandFailed, errorCode, errorMessage, now, null);
                return;
            }

            if (!TryValidateCurrentPlanet(command, player, out errorCode, out errorMessage))
            {
                finishCommand(command, CommandFailed, errorCode, errorMessage, now, null);
                return;
            }

            var factory = GameMain.localPlanet?.factory;
            if (factory == null)
            {
                finishCommand(command, CommandFailed, "game_not_ready", "Current planet factory is not loaded.", now, null);
                return;
            }

            if (!TryGetTargetEntityId(task, command, out var entityId, out errorMessage))
            {
                finishCommand(command, CommandFailed, "invalid_command", errorMessage, now, null);
                return;
            }

            if (!TryGetEntity(factory, entityId, out var entity))
            {
                finishCommand(command, CommandFailed, "target_not_found", $"Entity not found: {entityId}", now, null);
                return;
            }

            var buildArea = Math.Max(5f, player.mecha?.buildArea ?? 5f);
            var distance = Vector3.Distance(player.position, entity.pos);
            if (!IsWithinCommandIssueRange(player, entity.pos, out var commandDistance, out var commandRange))
            {
                finishCommand(
                    command,
                    CommandFailed,
                    "out_of_range",
                    "Entity is outside command issue range. Move closer before submitting dismantleEntity.",
                    now,
                    RangeFailureResult(commandDistance, commandRange, entity.pos));
                return;
            }

            if (distance > buildArea)
            {
                command.Phase = "approaching";
                command.ObjectPosition = entity.pos;
                command.TargetPosition = BuildAreaStandPosition(player, entity.pos, buildArea);
                if (!command.ActionIssued)
                {
                    player.Order(OrderNode.MoveTo(command.TargetPosition), false);
                    command.ActionIssued = true;
                }

                command.Result = new JsonObject
                {
                    ["entityId"] = entityId,
                    ["distance"] = distance,
                    ["range"] = buildArea,
                    ["target"] = Vector(entity.pos)
                };
                return;
            }

            var actionBuild = player.controller?.actionBuild;
            if (actionBuild == null)
            {
                finishCommand(command, CommandFailed, "game_not_ready", "Player build action is not available.", now, null);
                return;
            }

            player.ClearOrders();
            command.Phase = "dismantling";
            var before = StorageItemCounts(player.package);
            var summary = EntitySummary(entityId, entity);
            var dismantled = actionBuild.DoDismantleObject(entityId);
            if (!dismantled)
            {
                finishCommand(command, CommandFailed, "dismantle_failed", $"Entity could not be dismantled: {entityId}", now, summary);
                return;
            }

            var after = StorageItemCounts(player.package);
            var returnedItems = PositiveItemDeltas(before, after);
            finishCommand(
                command,
                CommandSucceeded,
                null,
                null,
                now,
                new JsonObject
                {
                    ["entityId"] = entityId,
                    ["protoId"] = entity.protoId,
                    ["protoName"] = ItemName(entity.protoId),
                    ["position"] = Vector(entity.pos),
                    ["returnedItems"] = ItemCountList(returnedItems),
                    ["returnedCount"] = TotalItemCount(returnedItems)
                });
        }

        private static void SnapshotMiningInventory(CommandState command)
        {
            command.MiningInitialInventoryCounts = new Dictionary<int, int>();
            if (command.MiningProductItemIds == null)
            {
                command.MiningProductItemIds = new List<int>();
            }

            if (command.TargetItemId > 0 && !command.MiningProductItemIds.Contains(command.TargetItemId))
            {
                command.MiningProductItemIds.Insert(0, command.TargetItemId);
            }

            foreach (var itemId in command.MiningProductItemIds)
            {
                if (itemId > 0 && !command.MiningInitialInventoryCounts.ContainsKey(itemId))
                {
                    command.MiningInitialInventoryCounts[itemId] = InventoryCount(itemId);
                }
            }
        }

        private static JsonObject MiningResult(CommandState command, bool depleted)
        {
            var result = new JsonObject
            {
                ["itemId"] = command.TargetItemId == 0 ? (object)null : command.TargetItemId,
                ["gained"] = command.TargetItemId == 0 ? (object)null : InventoryCount(command.TargetItemId) - command.InitialInventoryCount,
                ["targetCount"] = command.TargetInventoryCount,
                ["currentCount"] = command.TargetItemId == 0 ? (object)null : InventoryCount(command.TargetItemId),
                ["remaining"] = command.TargetItemId == 0 ? (object)null : Math.Max(0, command.TargetInventoryCount - InventoryCount(command.TargetItemId)),
                ["items"] = MiningItemDeltas(command),
                ["depleted"] = depleted
            };

            return result;
        }

        public object TimeoutResult(CommandState command)
        {
            switch (command.NormalizedType)
            {
                case "minetarget":
                    return command.MiningInitialInventoryCounts == null
                        ? null
                        : MiningResult(command, IsMineTargetDepleted(command, GameMain.localPlanet?.factory));
                default:
                    return null;
            }
        }

        private static List<object> MiningItemDeltas(CommandState command)
        {
            var result = new List<object>();
            if (command.MiningInitialInventoryCounts == null)
            {
                return result;
            }

            foreach (var pair in command.MiningInitialInventoryCounts)
            {
                var current = InventoryCount(pair.Key);
                result.Add(new JsonObject
                {
                    ["itemId"] = pair.Key,
                    ["itemName"] = ItemName(pair.Key),
                    ["initial"] = pair.Value,
                    ["current"] = current,
                    ["gained"] = current - pair.Value
                });
            }

            return result;
        }

        private static Dictionary<int, int> StorageItemCounts(StorageComponent storage)
        {
            var result = new Dictionary<int, int>();
            if (storage?.grids == null)
            {
                return result;
            }

            var limit = Math.Min(storage.size, storage.grids.Length);
            for (var i = 0; i < limit; i++)
            {
                var grid = storage.grids[i];
                if (grid.itemId <= 0 || grid.count <= 0)
                {
                    continue;
                }

                result.TryGetValue(grid.itemId, out var count);
                result[grid.itemId] = count + grid.count;
            }

            return result;
        }

        private static bool TryGetStorage(PlanetFactory factory, int entityId, out StorageComponent storage, out string errorMessage)
        {
            storage = null;
            errorMessage = null;
            if (factory == null || !TryGetEntity(factory, entityId, out var entity))
            {
                errorMessage = $"Entity not found: {entityId}";
                return false;
            }

            if (entity.storageId <= 0 ||
                factory.factoryStorage == null ||
                factory.factoryStorage.storagePool == null ||
                entity.storageId >= factory.factoryStorage.storagePool.Length)
            {
                errorMessage = $"Entity is not a storage entity: {entityId}";
                return false;
            }

            storage = factory.factoryStorage.storagePool[entity.storageId];
            if (storage == null || storage.id != entity.storageId)
            {
                errorMessage = $"Storage component not found for entity: {entityId}";
                return false;
            }

            return true;
        }

        private static int TransferFromStorage(StorageComponent storage, StorageComponent package, int itemId, int count)
        {
            var taken = storage.TakeItem(itemId, count, out var inc);
            if (taken <= 0)
            {
                return 0;
            }

            var moved = package.AddItemStacked(itemId, taken, inc, out var remainInc);
            if (moved < taken)
            {
                storage.AddItem(itemId, taken - moved, remainInc, out var _);
            }

            return moved;
        }

        private static int TransferToStorage(StorageComponent package, StorageComponent storage, int itemId, int count)
        {
            var taken = package.TakeItem(itemId, count, out var inc);
            if (taken <= 0)
            {
                return 0;
            }

            var moved = storage.AddItem(itemId, taken, inc, out var remainInc);
            if (moved < taken)
            {
                package.AddItemStacked(itemId, taken - moved, remainInc, out var _);
            }

            return moved;
        }

        private static Dictionary<int, int> PositiveItemDeltas(Dictionary<int, int> before, Dictionary<int, int> after)
        {
            var result = new Dictionary<int, int>();
            foreach (var pair in after)
            {
                before.TryGetValue(pair.Key, out var oldCount);
                var delta = pair.Value - oldCount;
                if (delta > 0)
                {
                    result[pair.Key] = delta;
                }
            }

            return result;
        }

        private static int TotalItemCount(Dictionary<int, int> items)
        {
            var total = 0;
            foreach (var pair in items)
            {
                total += pair.Value;
            }

            return total;
        }

        private static List<object> ItemCountList(Dictionary<int, int> items)
        {
            var result = new List<object>();
            foreach (var pair in items)
            {
                result.Add(new JsonObject
                {
                    ["itemId"] = pair.Key,
                    ["itemName"] = ItemName(pair.Key),
                    ["count"] = pair.Value
                });
            }

            return result;
        }

        private static bool TryGetEntity(PlanetFactory factory, int entityId, out EntityData entity)
        {
            entity = default;
            if (entityId <= 0 ||
                factory?.entityPool == null ||
                entityId >= factory.entityPool.Length ||
                factory.entityPool[entityId].id != entityId)
            {
                return false;
            }

            entity = factory.entityPool[entityId];
            return true;
        }

        private static Vector3 BuildAreaStandPosition(Player player, Vector3 objectPosition, float buildArea)
        {
            var from = player == null ? objectPosition.normalized * objectPosition.magnitude : player.position;
            var direction = objectPosition - from;
            if (direction.sqrMagnitude < 0.01f)
            {
                direction = objectPosition.normalized;
            }

            var standDistance = Math.Max(2f, buildArea * 0.75f);
            var target = objectPosition - direction.normalized * standDistance;
            return target.normalized * objectPosition.magnitude;
        }

        private static Dictionary<int, int> ItemBundleCounts(ItemBundle itemBundle)
        {
            var result = new Dictionary<int, int>();
            if (itemBundle.items == null)
            {
                return result;
            }

            foreach (var pair in itemBundle.items)
            {
                if (pair.Key <= 0 || pair.Value == 0)
                {
                    continue;
                }

                var countDelta = Math.Abs(pair.Value);
                result.TryGetValue(pair.Key, out var count);
                result[pair.Key] = count + countDelta;
            }

            return result;
        }

        private static JsonObject EntitySummary(int entityId, EntityData entity)
        {
            return new JsonObject
            {
                ["entityId"] = entityId,
                ["protoId"] = entity.protoId,
                ["protoName"] = ItemName(entity.protoId),
                ["position"] = Vector(entity.pos)
            };
        }

        private static JsonObject EntityFastFillSummary(PlanetFactory factory, int entityId)
        {
            if (!TryGetEntity(factory, entityId, out var entity))
            {
                return null;
            }

            var result = new JsonObject
            {
                ["entityId"] = entityId,
                ["protoId"] = entity.protoId,
                ["protoName"] = ItemName(entity.protoId),
                ["position"] = Vector(entity.pos)
            };

            if (entity.powerGenId > 0 &&
                factory.powerSystem?.genPool != null &&
                entity.powerGenId < factory.powerSystem.genPool.Length)
            {
                var generator = factory.powerSystem.genPool[entity.powerGenId];
                result["powerGenerator"] = new JsonObject
                {
                    ["powerGenId"] = entity.powerGenId,
                    ["fuelMask"] = generator.fuelMask,
                    ["fuelId"] = generator.fuelId == 0 ? (object)null : generator.fuelId,
                    ["fuelName"] = generator.fuelId == 0 ? null : ItemName(generator.fuelId),
                    ["fuelCount"] = generator.fuelCount,
                    ["fuelHeat"] = generator.fuelHeat
                };
            }

            return result;
        }

        private void ExecuteCraftInventoryLocked(CommandState command, DateTimeOffset now)
        {
            if (!TryGetPlayer(out var player, out var errorCode, out var errorMessage))
            {
                finishCommand(command, CommandFailed, errorCode, errorMessage, now, null);
                return;
            }

            var forge = player.mecha?.forge;
            if (forge == null)
            {
                finishCommand(command, CommandFailed, "game_not_ready", "Player forge is not available.", now, null);
                return;
            }

            if (command.CraftTargets != null)
            {
                WaitForCraftCompletionLocked(command, now);
                return;
            }

            if (!TryResolveCraftRequest(
                    command,
                    out var recipe,
                    out var recipeId,
                    out var craftCount,
                    out var requestedItemId,
                    out var requestedItemCount,
                    out errorCode,
                    out errorMessage,
                    out var errorResult))
            {
                finishCommand(command, CommandFailed, errorCode, errorMessage, now, errorResult);
                return;
            }

            if (!forge.TryAddTask(recipeId, craftCount, true))
            {
                finishCommand(
                    command,
                    CommandFailed,
                    "missing_item",
                    $"Not enough items to craft recipe: {recipeId}",
                    now,
                    CraftResult(recipe, craftCount, requestedItemId, requestedItemCount, canCraft: false, CraftRecursiveMissingItems(recipe, craftCount)));
                return;
            }

            command.CraftTargets = CraftTargetInventory(recipe, craftCount);
            command.CraftRecipeId = recipeId;
            command.CraftCount = craftCount;
            command.RequestedItemId = requestedItemId;
            command.RequestedItemCount = requestedItemCount;

            var task = forge.AddTask(recipeId, craftCount);
            if (task == null)
            {
                finishCommand(
                    command,
                    CommandFailed,
                    "craft_failed",
                    $"Failed to enqueue forge task for recipe: {recipeId}",
                    now,
                    CraftResult(recipe, craftCount, requestedItemId, requestedItemCount, canCraft: false, CraftRecursiveMissingItems(recipe, craftCount)));
                return;
            }

            command.ActionIssued = true;
            WaitForCraftCompletionLocked(command, now);
        }

        private void WaitForCraftCompletionLocked(CommandState command, DateTimeOffset now)
        {
            command.Phase = "crafting";
            foreach (var pair in command.CraftTargets)
            {
                if (InventoryCount(pair.Key) < pair.Value)
                {
                    return;
                }
            }

            var recipe = LDB.recipes.Select(command.CraftRecipeId);
            if (recipe == null)
            {
                finishCommand(command, CommandFailed, "invalid_command", $"Recipe not found: {command.CraftRecipeId}", now, null);
                return;
            }

            finishCommand(
                command,
                CommandSucceeded,
                null,
                null,
                now,
                CraftResult(recipe, command.CraftCount, command.RequestedItemId, command.RequestedItemCount, canCraft: true, new List<object>()));
        }

        private void ExecuteResearchTechLocked(CommandState command, DateTimeOffset now)
        {
            var history = GameMain.history;
            if (history == null)
            {
                finishCommand(command, CommandFailed, "game_not_ready", "Game history is not available.", now, null);
                return;
            }

            if (!TryGetInt(command, "techId", out var techId) || techId <= 0)
            {
                finishCommand(command, CommandFailed, "invalid_command", "researchTech requires a positive techId.", now, null);
                return;
            }

            var tech = LDB.techs.Select(techId);
            if (tech == null)
            {
                finishCommand(command, CommandFailed, "invalid_command", $"Tech not found: {techId}", now, null);
                return;
            }

            var waitForUnlock = GetBool(command, "waitForUnlock", true);
            if (history.TechUnlocked(techId))
            {
                finishCommand(command, CommandSucceeded, null, null, now, ResearchTechResult(history, tech, command.ActionIssued ? "unlocked" : "alreadyUnlocked"));
                return;
            }

            if (!command.ActionIssued)
            {
                command.Phase = "researching";
                if (!history.TechInQueue(techId))
                {
                    if (!history.CanEnqueueTech(techId))
                    {
                        finishCommand(
                            command,
                            CommandFailed,
                            "tech_not_available",
                            $"Tech cannot be enqueued: {techId}",
                            now,
                            ResearchTechResult(history, tech, "enqueueFailed"));
                        return;
                    }

                    history.EnqueueTech(techId);
                }

                command.ActionIssued = true;
                if (!waitForUnlock)
                {
                    finishCommand(command, CommandSucceeded, null, null, now, ResearchTechResult(history, tech, "enqueued"));
                    return;
                }
            }

            command.Phase = "waitingResearch";
            if (history.TechUnlocked(techId))
            {
                finishCommand(command, CommandSucceeded, null, null, now, ResearchTechResult(history, tech, "unlocked"));
            }
        }

        private void ExecuteBuyoutTechLocked(CommandState command, DateTimeOffset now)
        {
            var history = GameMain.history;
            if (history == null)
            {
                finishCommand(command, CommandFailed, "game_not_ready", "Game history is not available.", now, null);
                return;
            }

            if (!TryGetInt(command, "techId", out var techId) || techId <= 0)
            {
                finishCommand(command, CommandFailed, "invalid_command", "buyoutTech requires a positive techId.", now, null);
                return;
            }

            var tech = LDB.techs.Select(techId);
            if (tech == null)
            {
                finishCommand(command, CommandFailed, "invalid_command", $"Tech not found: {techId}", now, null);
                return;
            }

            if (history.TechUnlocked(techId))
            {
                finishCommand(command, CommandSucceeded, null, null, now, ResearchTechResult(history, tech, "alreadyUnlocked", usesMetadata: true));
                return;
            }

            command.Phase = "buyout";
            if (history.BuyoutTech(techId) || history.TechUnlocked(techId))
            {
                finishCommand(command, CommandSucceeded, null, null, now, ResearchTechResult(history, tech, "buyout", usesMetadata: true));
                return;
            }

            finishCommand(
                command,
                CommandFailed,
                "tech_buyout_failed",
                $"Tech could not be completed with metadata: {techId}",
                now,
                ResearchTechResult(history, tech, "buyoutFailed", usesMetadata: true));
        }

        private void ExecuteRemoveTechInQueueLocked(CommandState command, DateTimeOffset now)
        {
            var history = GameMain.history;
            if (history == null)
            {
                finishCommand(command, CommandFailed, "game_not_ready", "Game history is not available.", now, null);
                return;
            }

            if (!TryGetInt(command, "index", out var index))
            {
                finishCommand(command, CommandFailed, "invalid_command", "removeTechInQueue requires index.", now, null);
                return;
            }

            var queue = history.techQueue;
            if (queue == null)
            {
                finishCommand(command, CommandFailed, "game_not_ready", "Tech queue is not available.", now, null);
                return;
            }

            if (index < 0 || index >= queue.Length)
            {
                finishCommand(
                    command,
                    CommandFailed,
                    "invalid_command",
                    $"index must be between 0 and {queue.Length - 1}.",
                    now,
                    TechQueueResult(history, "removeFailed"));
                return;
            }

            history.RemoveTechInQueue(index);
            finishCommand(command, CommandSucceeded, null, null, now, TechQueueResult(history, "removed"));
        }

        private static bool TryResolveCraftRequest(
            CommandState command,
            out RecipeProto recipe,
            out int recipeId,
            out int craftCount,
            out int requestedItemId,
            out int requestedItemCount,
            out string errorCode,
            out string errorMessage,
            out object errorResult)
        {
            recipe = null;
            recipeId = 0;
            craftCount = 0;
            requestedItemId = 0;
            requestedItemCount = 0;
            errorCode = null;
            errorMessage = null;
            errorResult = null;

            TryGetInt(command, "recipeId", out recipeId);
            TryGetInt(command, "itemId", out requestedItemId);
            requestedItemCount = Math.Max(1, GetInt(command, "count", 1));

            if (requestedItemId > 0)
            {
                if (!TryResolveHandcraftRecipeForItem(
                        requestedItemId,
                        recipeId,
                        out recipe,
                        out var resultCount,
                        out errorCode,
                        out errorMessage,
                        out errorResult))
                {
                    return false;
                }

                recipeId = recipe.ID;
                craftCount = (requestedItemCount + resultCount - 1) / resultCount;
                return true;
            }

            if (recipeId <= 0)
            {
                errorCode = "invalid_command";
                errorMessage = "craftInventory requires itemId or recipeId.";
                return false;
            }

            craftCount = requestedItemCount;
            recipe = LDB.recipes.Select(recipeId);
            if (recipe == null)
            {
                errorCode = "invalid_command";
                errorMessage = $"Recipe not found: {recipeId}";
                return false;
            }

            if (!recipe.Handcraft)
            {
                errorCode = "invalid_command";
                errorMessage = $"Recipe is not handcraftable: {recipeId}";
                errorResult = CraftResult(recipe, craftCount, 0, 0, canCraft: false, new List<object>());
                return false;
            }

            if (GameMain.history == null || !GameMain.history.RecipeUnlocked(recipeId))
            {
                errorCode = "recipe_locked";
                errorMessage = $"Recipe is locked: {recipeId}";
                errorResult = CraftResult(recipe, craftCount, 0, 0, canCraft: false, new List<object>());
                return false;
            }

            return true;
        }

        private static bool TryResolveHandcraftRecipeForItem(
            int itemId,
            int preferredRecipeId,
            out RecipeProto recipe,
            out int resultCount,
            out string errorCode,
            out string errorMessage,
            out object errorResult)
        {
            recipe = null;
            resultCount = 0;
            errorCode = null;
            errorMessage = null;
            errorResult = null;

            if (preferredRecipeId > 0)
            {
                var preferredRecipe = LDB.recipes.Select(preferredRecipeId);
                if (preferredRecipe == null)
                {
                    errorCode = "invalid_command";
                    errorMessage = $"Recipe not found: {preferredRecipeId}";
                    return false;
                }

                if (!preferredRecipe.Handcraft || !RecipeProducesItem(preferredRecipe, itemId, out resultCount))
                {
                    errorCode = "invalid_command";
                    errorMessage = $"Recipe {preferredRecipeId} does not handcraft item: {itemId}";
                    errorResult = CraftResult(preferredRecipe, 1, itemId, 1, canCraft: false, new List<object>());
                    return false;
                }

                if (GameMain.history == null || !GameMain.history.RecipeUnlocked(preferredRecipeId))
                {
                    errorCode = "recipe_locked";
                    errorMessage = $"Recipe is locked: {preferredRecipeId}";
                    errorResult = CraftResult(preferredRecipe, 1, itemId, 1, canCraft: false, new List<object>());
                    return false;
                }

                recipe = preferredRecipe;
                return true;
            }

            var item = LDB.items.Select(itemId);
            var handcraft = item?.handcraft;
            if (handcraft == null)
            {
                errorCode = "invalid_command";
                errorMessage = $"No handcraft recipe produces item: {itemId}";
                return false;
            }

            if (!RecipeProducesItem(handcraft, itemId, out resultCount))
            {
                errorCode = "invalid_command";
                errorMessage = $"Handcraft recipe {handcraft.ID} does not produce item: {itemId}";
                errorResult = CraftResult(handcraft, 1, itemId, 1, canCraft: false, new List<object>());
                return false;
            }

            if (item.handcraftProductCount > 0)
            {
                resultCount = item.handcraftProductCount;
            }

            if (GameMain.history == null || !GameMain.history.RecipeUnlocked(handcraft.ID))
            {
                errorCode = "recipe_locked";
                errorMessage = $"No unlocked handcraft recipe produces item: {itemId}";
                errorResult = CraftResult(handcraft, 1, itemId, 1, canCraft: false, new List<object>());
                return false;
            }

            recipe = handcraft;
            return true;
        }

        private static bool RecipeProducesItem(RecipeProto recipe, int itemId, out int resultCount)
        {
            resultCount = 0;
            if (recipe?.Results == null || recipe.ResultCounts == null)
            {
                return false;
            }

            for (var i = 0; i < recipe.Results.Length && i < recipe.ResultCounts.Length; i++)
            {
                if (recipe.Results[i] == itemId && recipe.ResultCounts[i] > 0)
                {
                    resultCount = recipe.ResultCounts[i];
                    return true;
                }
            }

            return false;
        }

        private static Dictionary<int, int> CraftTargetInventory(RecipeProto recipe, int craftCount)
        {
            var targets = new Dictionary<int, int>();
            var products = CraftItemTotals(recipe.Results, recipe.ResultCounts, craftCount);
            foreach (var pair in products)
            {
                targets[pair.Key] = InventoryCount(pair.Key) + pair.Value;
            }

            return targets;
        }

        private static List<object> CraftRecursiveMissingItems(RecipeProto recipe, int craftCount)
        {
            var virtualInventory = new Dictionary<int, int>();
            var missing = new Dictionary<int, int>();
            AddRecipeNeeds(recipe, craftCount, virtualInventory, missing, new HashSet<int>());
            return CraftMissingList(missing);
        }

        private static void AddRecipeNeeds(
            RecipeProto recipe,
            int craftCount,
            Dictionary<int, int> virtualInventory,
            Dictionary<int, int> missing,
            HashSet<int> recipeStack)
        {
            if (recipe == null || recipe.Items == null || recipe.ItemCounts == null || craftCount <= 0)
            {
                return;
            }

            recipeStack.Add(recipe.ID);
            var ingredients = CraftItemTotals(recipe.Items, recipe.ItemCounts, craftCount);
            foreach (var ingredient in ingredients)
            {
                AddItemNeed(ingredient.Key, ingredient.Value, virtualInventory, missing, recipeStack);
            }

            recipeStack.Remove(recipe.ID);
        }

        private static void AddItemNeed(
            int itemId,
            int count,
            Dictionary<int, int> virtualInventory,
            Dictionary<int, int> missing,
            HashSet<int> recipeStack)
        {
            if (itemId <= 0 || count <= 0)
            {
                return;
            }

            var remaining = ConsumeVirtualItem(virtualInventory, itemId, count);
            if (remaining <= 0)
            {
                return;
            }

            if (!TryFindUnlockedHandcraftRecipeForItem(itemId, recipeStack, out var recipe, out var resultCount))
            {
                missing.TryGetValue(itemId, out var missingCount);
                missing[itemId] = missingCount + remaining;
                return;
            }

            var craftCount = (remaining + resultCount - 1) / resultCount;
            AddRecipeNeeds(recipe, craftCount, virtualInventory, missing, recipeStack);
            AddVirtualItem(virtualInventory, itemId, resultCount * craftCount);
            ConsumeVirtualItem(virtualInventory, itemId, remaining);
        }

        private static int ConsumeVirtualItem(Dictionary<int, int> virtualInventory, int itemId, int count)
        {
            if (!virtualInventory.TryGetValue(itemId, out var available))
            {
                available = InventoryCount(itemId);
            }

            var consumed = Math.Min(available, count);
            virtualInventory[itemId] = available - consumed;
            return count - consumed;
        }

        private static void AddVirtualItem(Dictionary<int, int> virtualInventory, int itemId, int count)
        {
            if (itemId <= 0 || count <= 0)
            {
                return;
            }

            virtualInventory.TryGetValue(itemId, out var current);
            virtualInventory[itemId] = current + count;
        }

        private static bool TryFindUnlockedHandcraftRecipeForItem(
            int itemId,
            HashSet<int> recipeStack,
            out RecipeProto recipe,
            out int resultCount)
        {
            recipe = null;
            resultCount = 0;
            var item = LDB.items.Select(itemId);
            recipe = item?.handcraft;
            if (recipe == null ||
                recipeStack.Contains(recipe.ID) ||
                GameMain.history == null ||
                !GameMain.history.RecipeUnlocked(recipe.ID) ||
                !RecipeProducesItem(recipe, itemId, out resultCount))
            {
                recipe = null;
                resultCount = 0;
                return false;
            }

            if (item.handcraftProductCount > 0)
            {
                resultCount = item.handcraftProductCount;
            }

            return true;
        }

        private static List<object> CraftMissingList(Dictionary<int, int> missing)
        {
            var items = new List<object>();
            foreach (var pair in missing)
            {
                var available = InventoryCount(pair.Key);
                items.Add(new JsonObject
                {
                    ["itemId"] = pair.Key,
                    ["itemName"] = ItemName(pair.Key),
                    ["required"] = pair.Value + available,
                    ["available"] = available,
                    ["missing"] = pair.Value
                });
            }

            return items;
        }

        private static JsonObject CraftResult(
            RecipeProto recipe,
            int craftCount,
            int requestedItemId,
            int requestedItemCount,
            bool canCraft,
            List<object> missing)
        {
            return new JsonObject
            {
                ["canCraft"] = canCraft,
                ["recipeId"] = recipe.ID,
                ["recipeName"] = recipe.name,
                ["craftCount"] = craftCount,
                ["requestedItemId"] = requestedItemId == 0 ? (object)null : requestedItemId,
                ["requestedItemCount"] = requestedItemId == 0 ? (object)null : requestedItemCount,
                ["ingredients"] = CraftItemList(recipe.Items, recipe.ItemCounts, craftCount),
                ["products"] = CraftItemList(recipe.Results, recipe.ResultCounts, craftCount),
                ["missing"] = missing
            };
        }

        private static Dictionary<int, int> CraftItemTotals(int[] itemIds, int[] itemCounts, int craftCount)
        {
            var totals = new Dictionary<int, int>();
            if (itemIds == null || itemCounts == null)
            {
                return totals;
            }

            for (var i = 0; i < itemIds.Length && i < itemCounts.Length; i++)
            {
                var itemId = itemIds[i];
                var count = itemCounts[i] * craftCount;
                if (itemId <= 0 || count <= 0)
                {
                    continue;
                }

                totals.TryGetValue(itemId, out var total);
                totals[itemId] = total + count;
            }

            return totals;
        }

        private static List<object> CraftItemList(int[] itemIds, int[] itemCounts, int craftCount)
        {
            var items = new List<object>();
            foreach (var pair in CraftItemTotals(itemIds, itemCounts, craftCount))
            {
                items.Add(new JsonObject
                {
                    ["itemId"] = pair.Key,
                    ["itemName"] = ItemName(pair.Key),
                    ["count"] = pair.Value
                });
            }

            return items;
        }

        private static string ItemName(int itemId)
        {
            return itemId <= 0 ? null : LDB.items.Select(itemId)?.name;
        }

        private static string TechName(int techId)
        {
            return techId <= 0 ? null : LDB.techs.Select(techId)?.name;
        }

        private static JsonObject ResearchTechResult(GameHistoryData history, TechProto tech, string action, bool usesMetadata = false)
        {
            var techId = tech?.ID ?? 0;
            object techState = techId > 0 ? (object)history.TechState(techId) : null;
            return new JsonObject
            {
                ["action"] = action,
                ["usesMetadata"] = usesMetadata,
                ["techId"] = techId,
                ["techName"] = tech?.name,
                ["unlocked"] = techId > 0 && history.TechUnlocked(techId),
                ["inQueue"] = techId > 0 && history.TechInQueue(techId),
                ["canEnqueue"] = techId > 0 && history.CanEnqueueTech(techId),
                ["currentTechId"] = ReflectionReader.GetInt(history, 0, "currentTech"),
                ["queueLength"] = ReflectionReader.GetInt(history, 0, "techQueueLength"),
                ["hashUploaded"] = ReflectionReader.GetLong(techState, 0, "hashUploaded"),
                ["hashNeeded"] = ReflectionReader.GetLong(techState, tech == null ? 0 : tech.GetHashNeeded(0), "hashNeeded"),
                ["items"] = TechItemCosts(tech),
                ["metadataBuyoutCost"] = MetadataBuyoutCosts(tech, techState)
            };
        }

        private static JsonObject TechQueueResult(GameHistoryData history, string action)
        {
            var queue = new List<object>();
            if (history?.techQueue != null)
            {
                for (var i = 0; i < history.techQueue.Length; i++)
                {
                    var techId = history.techQueue[i];
                    queue.Add(new JsonObject
                    {
                        ["index"] = i,
                        ["techId"] = techId,
                        ["techName"] = TechName(techId)
                    });
                }
            }

            return new JsonObject
            {
                ["action"] = action,
                ["currentTechId"] = history == null ? 0 : ReflectionReader.GetInt(history, 0, "currentTech"),
                ["queueLength"] = history == null ? 0 : ReflectionReader.GetInt(history, 0, "techQueueLength"),
                ["techQueue"] = queue
            };
        }

        private static List<object> TechItemCosts(TechProto tech)
        {
            var result = new List<object>();
            if (tech?.Items == null || tech.ItemPoints == null)
            {
                return result;
            }

            var count = Math.Min(tech.Items.Length, tech.ItemPoints.Length);
            for (var i = 0; i < count; i++)
            {
                var itemId = tech.Items[i];
                if (itemId <= 0)
                {
                    continue;
                }

                result.Add(new JsonObject
                {
                    ["itemId"] = itemId,
                    ["itemName"] = ItemName(itemId),
                    ["points"] = tech.ItemPoints[i],
                    ["available"] = InventoryCount(itemId)
                });
            }

            return result;
        }

        private static List<object> MetadataBuyoutCosts(TechProto tech, object techState)
        {
            var result = new List<object>();
            if (tech == null)
            {
                return result;
            }

            var hashUploaded = ReflectionReader.GetLong(techState, 0, "hashUploaded");
            var hashNeeded = ReflectionReader.GetLong(techState, tech.GetHashNeeded(0), "hashNeeded");
            var progress = hashNeeded <= 0 ? 1.0 : Math.Max(0.0, Math.Min(1.0, (double)hashUploaded / hashNeeded));
            if (tech.PropertyOverrideItemArray != null)
            {
                foreach (var entry in tech.PropertyOverrideItemArray)
                {
                    var itemId = ReflectionReader.GetInt(entry, 0, "id");
                    var count = ReflectionReader.GetInt(entry, 0, "count");
                    var required = (int)Math.Ceiling(count * (1.0 - progress));
                    AddMetadataBuyoutCost(result, itemId, required);
                }

                return result;
            }

            if (tech.Items == null || tech.ItemPoints == null)
            {
                return result;
            }

            var itemCount = Math.Min(tech.Items.Length, tech.ItemPoints.Length);
            for (var i = 0; i < itemCount; i++)
            {
                var itemId = tech.Items[i];
                if (itemId <= 0)
                {
                    continue;
                }

                var remainingHash = Math.Max(0, hashNeeded - hashUploaded);
                var required = tech.ItemPoints[i] * remainingHash / 3600;
                AddMetadataBuyoutCost(result, itemId, required);
            }

            return result;
        }

        private static void AddMetadataBuyoutCost(List<object> result, int itemId, long required)
        {
            if (itemId <= 0)
            {
                return;
            }

            result.Add(new JsonObject
            {
                ["itemId"] = itemId,
                ["itemName"] = ItemName(itemId),
                ["required"] = required,
                ["available"] = MetadataAvailableProperty(itemId)
            });
        }

        private static long MetadataAvailableProperty(int itemId)
        {
            try
            {
                var propertySystem = DSPGame.propertySystem;
                var data = GameMain.data;
                if (propertySystem == null || data == null)
                {
                    return 0;
                }

                return propertySystem.GetItemAvaliableProperty(data.GetClusterSeedKey(), itemId);
            }
            catch
            {
                return 0;
            }
        }

        private void ExecuteSetRecipeLocked(TaskState task, CommandState command, DateTimeOffset now)
        {
            if (!TryGetPlayer(out var player, out var errorCode, out var errorMessage))
            {
                finishCommand(command, CommandFailed, errorCode, errorMessage, now, null);
                return;
            }

            var factory = GameMain.localPlanet?.factory;
            if (factory?.factorySystem == null)
            {
                finishCommand(command, CommandFailed, "game_not_ready", "Current planet factory system is not loaded.", now, null);
                return;
            }

            if (!TryGetTargetEntityId(task, command, out var entityId, out errorMessage))
            {
                finishCommand(command, CommandFailed, "invalid_command", errorMessage, now, null);
                return;
            }

            if (entityId <= 0 ||
                factory.entityPool == null ||
                entityId >= factory.entityPool.Length ||
                factory.entityPool[entityId].id != entityId)
            {
                finishCommand(command, CommandFailed, "target_not_found", $"Entity not found: {entityId}", now, null);
                return;
            }

            if (!TryGetInt(command, "recipeId", out var recipeId) || recipeId <= 0)
            {
                finishCommand(command, CommandFailed, "invalid_command", "setRecipe requires a positive recipeId.", now, null);
                return;
            }

            var recipe = LDB.recipes.Select(recipeId);
            if (recipe == null)
            {
                finishCommand(command, CommandFailed, "invalid_command", $"Recipe not found: {recipeId}", now, null);
                return;
            }

            if (GameMain.history == null || !GameMain.history.RecipeUnlocked(recipeId))
            {
                finishCommand(command, CommandFailed, "recipe_locked", $"Recipe is locked: {recipeId}", now, null);
                return;
            }

            var entity = factory.entityPool[entityId];
            if (entity.assemblerId > 0)
            {
                if (!CanAssemblerUseRecipe(entity.protoId, recipe))
                {
                    finishCommand(command, CommandFailed, "invalid_recipe", $"Recipe {recipeId} is not valid for entity {entityId}.", now, null);
                    return;
                }

                if (factory.factorySystem.assemblerPool[entity.assemblerId].recipeId != recipeId)
                {
                    factory.factorySystem.TakeBackItems_Assembler(player, entity.assemblerId);
                    factory.factorySystem.assemblerPool[entity.assemblerId].SetRecipe(recipeId, factory.entitySignPool);
                }

                finishCommand(
                    command,
                    CommandSucceeded,
                    null,
                    null,
                    now,
                    new JsonObject
                    {
                        ["entityId"] = entityId,
                        ["recipeId"] = recipeId
                    });
                return;
            }

            if (entity.labId > 0)
            {
                if (recipe.Type != ERecipeType.Research)
                {
                    finishCommand(command, CommandFailed, "invalid_recipe", $"Recipe {recipeId} is not a lab matrix recipe.", now, null);
                    return;
                }

                if (factory.factorySystem.labPool[entity.labId].recipeId != recipeId ||
                    factory.factorySystem.labPool[entity.labId].researchMode)
                {
                    factory.factorySystem.TakeBackItems_Lab(player, entity.labId);
                    factory.factorySystem.labPool[entity.labId].SetFunction(_researchMode: false, recipeId, 0, factory.entitySignPool);
                    factory.factorySystem.SyncLabFunctions(player, entity.labId);
                    factory.factorySystem.SyncLabForceAccMode(player, entity.labId);
                }

                finishCommand(
                    command,
                    CommandSucceeded,
                    null,
                    null,
                    now,
                    new JsonObject
                    {
                        ["entityId"] = entityId,
                        ["recipeId"] = recipeId
                    });
                return;
            }

            finishCommand(command, CommandFailed, "invalid_target", $"Entity does not support recipes: {entityId}", now, null);
        }

        private void ExecuteSetLabResearchModeLocked(TaskState task, CommandState command, DateTimeOffset now)
        {
            if (!TryGetPlayer(out var player, out var errorCode, out var errorMessage))
            {
                finishCommand(command, CommandFailed, errorCode, errorMessage, now, null);
                return;
            }

            if (!TryValidateCurrentPlanet(command, player, out errorCode, out errorMessage))
            {
                finishCommand(command, CommandFailed, errorCode, errorMessage, now, null);
                return;
            }

            var factory = GameMain.localPlanet?.factory;
            if (factory == null)
            {
                finishCommand(command, CommandFailed, "game_not_ready", "Current planet factory is not loaded.", now, null);
                return;
            }

            if (!TryGetTargetEntityId(task, command, out var entityId, out errorMessage))
            {
                finishCommand(command, CommandFailed, "invalid_command", errorMessage, now, null);
                return;
            }

            if (!TryGetEntity(factory, entityId, out var entity))
            {
                finishCommand(command, CommandFailed, "target_not_found", $"Entity not found: {entityId}", now, null);
                return;
            }

            if (entity.labId <= 0 ||
                factory.factorySystem?.labPool == null ||
                entity.labId >= factory.factorySystem.labPool.Length ||
                factory.factorySystem.labPool[entity.labId].id != entity.labId)
            {
                finishCommand(command, CommandFailed, "invalid_target", $"Entity is not a matrix lab: {entityId}", now, null);
                return;
            }

            var history = GameMain.history;
            if (history == null)
            {
                finishCommand(command, CommandFailed, "game_not_ready", "Game history is not available.", now, null);
                return;
            }

            var techId = GetInt(command, "techId", history.currentTech);
            var tech = techId > 0 ? LDB.techs.Select(techId) : null;
            if (techId > 0 && (tech == null || !tech.IsLabTech))
            {
                finishCommand(command, CommandFailed, "invalid_tech", $"Tech is not a lab research tech: {techId}", now, null);
                return;
            }

            var lab = factory.factorySystem.labPool[entity.labId];
            if (!lab.researchMode || lab.techId != techId)
            {
                factory.factorySystem.TakeBackItems_Lab(player, entity.labId);
                factory.factorySystem.labPool[entity.labId].SetFunction(_researchMode: true, 0, techId, factory.entitySignPool);
                factory.factorySystem.SyncLabFunctions(player, entity.labId);
                factory.factorySystem.SyncLabForceAccMode(player, entity.labId);
            }

            lab = factory.factorySystem.labPool[entity.labId];
            finishCommand(
                command,
                CommandSucceeded,
                null,
                null,
                now,
                new JsonObject
                {
                    ["entityId"] = entityId,
                    ["labId"] = entity.labId,
                    ["researchMode"] = lab.researchMode,
                    ["techId"] = lab.techId == 0 ? (object)null : lab.techId,
                    ["techName"] = lab.techId == 0 ? null : TechName(lab.techId)
                });
        }
    }
}
