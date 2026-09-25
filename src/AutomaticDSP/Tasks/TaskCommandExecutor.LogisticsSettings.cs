using System;
using AutomaticDSP.Serialization;
using Newtonsoft.Json.Linq;
using UnityEngine;
using static AutomaticDSP.Tasks.TaskStatusNames;

namespace AutomaticDSP.Tasks
{
    internal sealed partial class TaskCommandExecutor
    {
        private void ExecuteLogisticsSettingLocked(TaskState task, CommandState command, DateTimeOffset now)
        {
            if (!TryGetPlayer(out var player, out var errorCode, out var errorMessage) ||
                !TryValidateCurrentPlanet(command, player, out errorCode, out errorMessage))
            {
                finishCommand(command, CommandFailed, errorCode, errorMessage, now, null);
                return;
            }
            var factory = GameMain.localPlanet?.factory;
            if (factory == null)
            {
                finishCommand(command, CommandFailed, "game_not_ready", "Local factory is unavailable.", now, null);
                return;
            }
            if (!TryGetTargetEntityId(task, command, out var entityId, out errorMessage))
            {
                finishCommand(command, CommandFailed, "invalid_command", errorMessage, now, null);
                return;
            }
            if (!TryGetEntity(factory, entityId, out var entity))
            {
                finishCommand(command, CommandFailed, "target_not_found", "Entity was not found.", now, null);
                return;
            }
            var distance = Vector3.Distance(player.position, entity.pos);
            if (distance > player.mecha.buildArea)
            {
                finishCommand(command, CommandFailed, "out_of_range", "Move within build range before changing logistics settings.", now,
                    RangeFailureResult(distance, player.mecha.buildArea, entity.pos));
                return;
            }

            JsonObject result;
            switch (command.NormalizedType)
            {
                case "setstoragelimit":
                    if (entity.storageId <= 0 || !TryGetInt(command, "maxSlots", out var maxSlots))
                    {
                        finishCommand(command, CommandFailed, "invalid_command", "setStorageLimit requires a storage entity and maxSlots.", now, null);
                        return;
                    }
                    var storage = factory.factoryStorage.storagePool[entity.storageId];
                    if (maxSlots < 0 || maxSlots > storage.size)
                    {
                        finishCommand(command, CommandFailed, "invalid_command", "maxSlots must be between zero and the storage size.", now, null);
                        return;
                    }
                    // 与原生容量滑块一致：限制自动化可用格数，不删除已有库存。
                    storage.SetBans(storage.size - maxSlots);
                    result = new JsonObject { ["entityId"] = entityId, ["size"] = storage.size,
                        ["bans"] = storage.bans, ["maxSlots"] = storage.size - storage.bans };
                    break;
                case "setsorterfilter":
                    if (entity.inserterId <= 0 || !TryGetInt(command, "itemId", out var itemId) ||
                        itemId < 0 || (itemId > 0 && LDB.items.Select(itemId) == null))
                    {
                        finishCommand(command, CommandFailed, "invalid_command", "setSorterFilter requires a sorter and valid itemId, or zero to clear.", now, null);
                        return;
                    }
                    // 原生窗口同时更新过滤器和实体图标；已拿起的货物仍由游戏处理。
                    factory.factorySystem.inserterPool[entity.inserterId].filter = itemId;
                    factory.entitySignPool[entityId].iconId0 = (uint)itemId;
                    factory.entitySignPool[entityId].iconType = itemId > 0 ? 1u : 0u;
                    result = new JsonObject { ["entityId"] = entityId,
                        ["itemId"] = factory.factorySystem.inserterPool[entity.inserterId].filter };
                    break;
                default:
                    if (entity.splitterId <= 0 || !TryGetInt(command, "slot", out var slot) || slot < 0 || slot > 3 ||
                        !TryGetToken(command, "priority", out var priorityToken) || priorityToken.Type != JTokenType.Boolean)
                    {
                        finishCommand(command, CommandFailed, "invalid_command", "setSplitterPriority requires a splitter, slot 0..3 and boolean priority.", now, null);
                        return;
                    }
                    factory.ReadObjectConn(entityId, slot, out var isOutput, out var otherEntityId, out var otherSlot);
                    if (!TryGetEntity(factory, otherEntityId, out var connected) || connected.beltId <= 0)
                    {
                        // 原生 UI 不允许设置未接带端口，不使用 SetPriority 的内部预设能力。
                        finishCommand(command, CommandFailed, "invalid_connection", "Splitter slot must connect to a built belt.", now, null);
                        return;
                    }
                    var priority = priorityToken.Value<bool>();
                    var traffic = factory.cargoTraffic;
                    traffic.splitterPool[entity.splitterId].SetPriority(slot, priority,
                        priority ? traffic.splitterPool[entity.splitterId].outFilter : 0);
                    var splitter = traffic.splitterPool[entity.splitterId];
                    result = new JsonObject { ["entityId"] = entityId, ["slot"] = slot, ["isOutput"] = isOutput,
                        ["inPriority"] = splitter.inPriority, ["outPriority"] = splitter.outPriority,
                        ["inputBeltId"] = splitter.input0, ["outputBeltId"] = splitter.output0, ["outFilter"] = splitter.outFilter };
                    break;
            }
            finishCommand(command, CommandSucceeded, null, null, now, result);
        }
    }
}
