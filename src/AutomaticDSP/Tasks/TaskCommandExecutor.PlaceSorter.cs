using System;
using System.Collections.Generic;
using AutomaticDSP.Serialization;
using Newtonsoft.Json.Linq;
using UnityEngine;
using static AutomaticDSP.Tasks.TaskStatusNames;

namespace AutomaticDSP.Tasks
{
    internal sealed partial class TaskCommandExecutor
    {
        private void ExecutePlaceSorterLocked(TaskState task, CommandState command, DateTimeOffset now)
        {
            if (command.BuildTargets != null)
            {
                WaitForBuiltObjectsLocked(command, now);
                return;
            }

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

            if (!TryGetInt(command, "itemId", out var itemId) || itemId <= 0)
            {
                finishCommand(command, CommandFailed, "invalid_command", "placeSorter requires a positive itemId.", now, null);
                return;
            }

            var item = LDB.items.Select(itemId);
            if (item == null || !item.IsEntity || !item.CanBuild || !item.prefabDesc.isInserter)
            {
                finishCommand(command, CommandFailed, "invalid_command", $"Item is not a buildable sorter: {itemId}", now, null);
                return;
            }

            if (GameMain.history == null || !GameMain.history.ItemUnlocked(itemId))
            {
                finishCommand(command, CommandFailed, "tech_locked", $"Item is locked: {itemId}", now, null);
                return;
            }

            var filterItemId = 0;
            if (TryGetToken(command, "filterItemId", out _) &&
                (!TryGetInt(command, "filterItemId", out filterItemId) || filterItemId < 0 ||
                 (filterItemId > 0 && LDB.items.Select(filterItemId) == null)))
            {
                finishCommand(command, CommandFailed, "invalid_command", "filterItemId must be zero or an existing item ID.", now, null);
                return;
            }

            if (!TryGetSorterEndpoint(task, command, "input", out var inputEndpoint, out errorMessage) ||
                !TryGetSorterEndpoint(task, command, "output", out var outputEndpoint, out errorMessage))
            {
                finishCommand(command, CommandFailed, "invalid_command", errorMessage, now, null);
                return;
            }

            var actionBuild = player.controller?.actionBuild;
            if (actionBuild == null || GameMain.data == null)
            {
                finishCommand(command, CommandFailed, "game_not_ready", "Build action is not ready.", now, null);
                return;
            }

            var tool = new AutomationInserterBuildTool();
            try
            {
                tool._Init(GameMain.data);
                actionBuild.SetFactoryReferences();
                tool.SetFactoryReferences();
                tool.PrepareInventorySnapshot();

                if (!TryResolveSorterEndpoint(tool, factory, inputEndpoint, out var inputPosition, out var inputRotation, out var inputSlot, out errorMessage) ||
                    !TryResolveSorterEndpoint(tool, factory, outputEndpoint, out var outputPosition, out var outputRotation, out var outputSlot, out errorMessage))
                {
                    finishCommand(command, CommandFailed, "invalid_command", errorMessage, now, null);
                    return;
                }

                var issueTargets = new List<Vector3> { inputPosition, outputPosition, Vector3.Lerp(inputPosition, outputPosition, 0.5f) };
                var inputIsBelt = tool.ObjectIsBelt(inputEndpoint.EntityId);
                var outputIsBelt = tool.ObjectIsBelt(outputEndpoint.EntityId);
                var direction = outputPosition - inputPosition;
                if (inputIsBelt) inputRotation = OrientBeltSorterSlot(inputRotation, direction);
                if (outputIsBelt) outputRotation = OrientBeltSorterSlot(outputRotation, -direction);
                // 原生 DeterminePreviews 的端点配对校验不在 CheckBuildConditions 内。
                // 保留其角度容差，阻止跨架高层或背向插槽；球面上不能比较全局 y。
                var bias = Mathf.Max(Vector3.Angle(direction, inputRotation * Vector3.forward),
                    Vector3.Angle(-direction, outputRotation * Vector3.forward));
                bias = Mathf.Max(bias, 180f - Vector3.Angle(inputRotation * Vector3.forward, outputRotation * Vector3.forward));
                var allowedBias = inputIsBelt || outputIsBelt ? 11f : 14f;
                if (bias >= allowedBias)
                {
                    finishCommand(command, CommandFailed, "invalid_connection", "Sorter endpoints fail native pose alignment; check height and slot facing.", now,
                        new JsonObject { ["condition"] = "NeedConn", ["endpointAngle"] = bias,
                            ["maxEndpointAngle"] = allowedBias,
                            ["heightDifference"] = Mathf.Abs(inputPosition.magnitude - outputPosition.magnitude) });
                    return;
                }
                // 原生写入会替换已有连接；任务不能因复用插槽而断开其他分拣器。
                if (!TryEnsureObjectSlotAvailable(factory, inputEndpoint.EntityId, inputSlot, out errorMessage) ||
                    !TryEnsureObjectSlotAvailable(factory, outputEndpoint.EntityId, outputSlot, out errorMessage))
                {
                    finishCommand(command, CommandFailed, "slot_occupied", errorMessage, now, null);
                    return;
                }
                if (!AreAllWithinCommandIssueRange(player, issueTargets, out var failedTarget, out var commandDistance, out var commandRange))
                {
                    finishCommand(
                        command,
                        CommandFailed,
                        "out_of_range",
                        "Sorter endpoints are outside command issue range. Move closer before submitting placeSorter.",
                        now,
                        RangeFailureResult(commandDistance, commandRange, failedTarget));
                    return;
                }

                player.controller.cmd.type = ECommand.Build;
                command.EnteredBuildMode = true;
                player.controller.cmd.mode = item.BuildMode;
                player.controller.cmd.stage = 1;
                player.controller.cmd.refId = item.ID;
                player.controller.cmd.target = outputPosition;
                player.controller.cmd.test = inputPosition;

                tool.handItem = item;
                tool.handPrefabDesc = item.prefabDesc;
                tool.startObjectId = inputEndpoint.EntityId;
                tool.castObjectId = outputEndpoint.EntityId;
                tool.castObjectPos = outputPosition;
                tool.buildPreviews.Clear();

                var preview = new BuildPreview();
                preview.ResetAll();
                preview.item = item;
                preview.desc = item.prefabDesc;
                preview.lpos = inputPosition;
                preview.lrot = inputRotation;
                preview.lpos2 = outputPosition;
                preview.lrot2 = outputRotation * Quaternion.Euler(0f, 180f, 0f);
                preview.inputObjId = inputEndpoint.EntityId;
                preview.inputFromSlot = inputSlot;
                preview.inputToSlot = 1;
                preview.outputObjId = outputEndpoint.EntityId;
                preview.outputFromSlot = 0;
                preview.outputToSlot = outputSlot;
                preview.condition = EBuildCondition.Ok;
                preview.needModel = false;
                // 在预建中保存筛选，避免落成到后续设置命令之间误送其他产物。
                preview.filterId = filterItemId;
                preview.genNearColliderArea2 = 25;
                tool.buildPreviews.Add(preview);

                ActivateBuildColliders(factory, preview);
                factory.planet.physics?.nearColliderLogic?.ActiveCollidersInArea(outputPosition, 5f);
                command.Phase = "validating";
                var canBuild = tool.CheckBuildConditions();
                if (!canBuild || preview.condition != EBuildCondition.Ok)
                {
                    if (preview.condition == EBuildCondition.OutOfReach &&
                        TryApproachBuildTarget(player, Vector3.Lerp(inputPosition, outputPosition, 0.5f)))
                    {
                        command.Phase = "approaching";
                        return;
                    }

                    finishCommand(
                        command,
                        CommandFailed,
                        BuildConditionCode(preview.condition),
                        preview.conditionText,
                        now,
                        new JsonObject { ["condition"] = preview.condition.ToString() });
                    return;
                }

                command.Phase = "creatingPrebuild";
                tool.CreatePrebuilds();
                if (preview.objId == 0)
                {
                    finishCommand(command, CommandFailed, "build_failed", "Build tool did not create a sorter prebuild.", now, null);
                    return;
                }

                command.BuildItemId = itemId;
                command.BuildTargets = new List<BuildWaitTarget>
                {
                    new BuildWaitTarget(preview.objId, itemId, inputPosition, preview)
                };
            }
            finally
            {
                tool._Free();
            }

            WaitForBuiltObjectsLocked(command, now);
        }
        private static Quaternion OrientBeltSorterSlot(Quaternion rotation, Vector3 direction)
        {
            var best = rotation;
            var angle = Vector3.Angle(direction, rotation * Vector3.forward);
            for (var yaw = 90; yaw < 360; yaw += 90)
            {
                var candidate = rotation * Quaternion.Euler(0f, yaw, 0f);
                var candidateAngle = Vector3.Angle(direction, candidate * Vector3.forward);
                if (candidateAngle < angle)
                {
                    best = candidate;
                    angle = candidateAngle;
                }
            }
            return best;
        }

        private static bool TryGetSorterEndpoint(
            TaskState task,
            CommandState command,
            string name,
            out SorterEndpoint endpoint,
            out string errorMessage)
        {
            endpoint = null;
            errorMessage = null;

            var entityField = name + "EntityId";
            var slotField = name + "Slot";
            var positionField = name + "Position";
            var entityId = 0;
            var slot = 0;
            var entityIndex = 0;
            var hasPosition = false;
            var position = Vector3.zero;
            string commandId = null;

            if (TryGetInt(command, entityField, out var topLevelEntityId))
            {
                entityId = topLevelEntityId;
            }

            if (TryGetInt(command, slotField, out var topLevelSlot))
            {
                slot = topLevelSlot;
            }

            if (TryGetInt(command, name + "EntityIndex", out var topLevelEntityIndex))
            {
                entityIndex = topLevelEntityIndex;
            }

            if (TryGetToken(command, positionField, out var positionToken))
            {
                if (!TryReadVectorToken(positionToken, positionField, out position, out errorMessage))
                {
                    return false;
                }

                hasPosition = true;
            }

            if (TryGetToken(command, name, out var endpointToken))
            {
                if (endpointToken.Type == JTokenType.Integer)
                {
                    entityId = endpointToken.Value<int>();
                }
                else if (endpointToken is JObject endpointObject)
                {
                    if (endpointObject.TryGetValue("entityId", StringComparison.OrdinalIgnoreCase, out var entityToken))
                    {
                        entityId = entityToken.Value<int>();
                    }

                    if (endpointObject.TryGetValue("commandId", StringComparison.OrdinalIgnoreCase, out var commandToken))
                    {
                        commandId = commandToken.Value<string>();
                    }

                    if (endpointObject.TryGetValue("entityIndex", StringComparison.OrdinalIgnoreCase, out var entityIndexToken))
                    {
                        entityIndex = entityIndexToken.Value<int>();
                    }

                    if (endpointObject.TryGetValue("slot", StringComparison.OrdinalIgnoreCase, out var slotToken))
                    {
                        slot = slotToken.Value<int>();
                    }

                    if (endpointObject.TryGetValue("position", StringComparison.OrdinalIgnoreCase, out var nestedPositionToken))
                    {
                        if (!TryReadVectorToken(nestedPositionToken, $"{name}.position", out position, out errorMessage))
                        {
                            return false;
                        }

                        hasPosition = true;
                    }
                }
                else
                {
                    errorMessage = $"{name} must be an entity id or object.";
                    return false;
                }
            }

            if (entityId <= 0 && !string.IsNullOrWhiteSpace(commandId))
            {
                if (!TryResolveCommandEntityId(task, commandId, entityIndex, out entityId, out errorMessage))
                {
                    return false;
                }
            }

            if (entityId <= 0)
            {
                errorMessage = $"placeSorter requires {entityField}, {name}.entityId, or {name}.commandId.";
                return false;
            }

            endpoint = new SorterEndpoint(entityId, slot, hasPosition, position);
            return true;
        }
        private static bool TryResolveSorterEndpoint(
            AutomationInserterBuildTool tool,
            PlanetFactory factory,
            SorterEndpoint endpoint,
            out Vector3 position,
            out Quaternion rotation,
            out int slot,
            out string errorMessage)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;
            slot = endpoint.Slot;
            errorMessage = null;

            if (factory.entityPool == null ||
                endpoint.EntityId >= factory.entityPool.Length ||
                factory.entityPool[endpoint.EntityId].id != endpoint.EntityId)
            {
                errorMessage = $"Sorter endpoint entity not found: {endpoint.EntityId}";
                return false;
            }

            var objectPose = tool.GetObjectPose(endpoint.EntityId);
            if (tool.ObjectIsBelt(endpoint.EntityId))
            {
                position = objectPose.position;
                rotation = Quaternion.AngleAxis(tool.GetObjectTilt(endpoint.EntityId), objectPose.forward) * objectPose.rotation;
                slot = -1;
            }
            else
            {
                var slots = tool.GetLocalSlots(endpoint.EntityId);
                if (slots == null || slots.Length == 0)
                {
                    errorMessage = $"Sorter endpoint has no slots: {endpoint.EntityId}";
                    return false;
                }

                if (slot < 0 || slot >= slots.Length)
                {
                    errorMessage = $"Sorter endpoint slot is out of range: {endpoint.EntityId}/{slot}";
                    return false;
                }

                var pose = slots[slot].GetTransformedBy(objectPose);
                position = pose.position;
                rotation = pose.rotation;
            }

            if (endpoint.HasPosition && (endpoint.Position - position).sqrMagnitude > 0.0001f)
            {
                errorMessage = $"Sorter endpoint position must match its belt or building slot: {endpoint.EntityId}/{slot}";
                return false;
            }
            return true;
        }
    }
}
