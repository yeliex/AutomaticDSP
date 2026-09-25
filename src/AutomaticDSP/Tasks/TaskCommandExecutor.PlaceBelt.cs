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
        private void ExecutePlaceBeltLocked(TaskState task, CommandState command, DateTimeOffset now)
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
                finishCommand(command, CommandFailed, "invalid_command", "placeBelt requires a positive itemId.", now, null);
                return;
            }

            var item = LDB.items.Select(itemId);
            if (item == null || !item.IsEntity || !item.CanBuild || !item.prefabDesc.isBelt)
            {
                finishCommand(command, CommandFailed, "invalid_command", $"Item is not a buildable belt: {itemId}", now, null);
                return;
            }

            if (GameMain.history == null || !GameMain.history.ItemUnlocked(itemId))
            {
                finishCommand(command, CommandFailed, "tech_locked", $"Item is locked: {itemId}", now, null);
                return;
            }

            var actionBuild = player.controller?.actionBuild;
            if (actionBuild == null || GameMain.data == null)
            {
                finishCommand(command, CommandFailed, "game_not_ready", "Build action is not ready.", now, null);
                return;
            }

            var tool = new AutomationPathBuildTool();
            try
            {
                tool._Init(GameMain.data);
                actionBuild.SetFactoryReferences();
                tool.SetFactoryReferences();
                tool.PrepareInventorySnapshot();

                if (!TryGetBeltPathPoints(task, command, tool, factory, out var pathPoints, out var startEndpoint, out var endEndpoint, out errorMessage))
                {
                    finishCommand(command, CommandFailed, "invalid_command", errorMessage, now, null);
                    return;
                }

                // 已有带段作为连接对象，不再生成同位置的预览；重合点会让原生坡度计算误报 TooSteep。
                if (startEndpoint?.EntityId > 0 && tool.ObjectIsBelt(startEndpoint.EntityId) &&
                    (pathPoints[0] - tool.GetObjectPose(startEndpoint.EntityId).position).sqrMagnitude < 0.01f)
                    pathPoints.RemoveAt(0);
                if (pathPoints.Count > 0 && endEndpoint?.EntityId > 0 && tool.ObjectIsBelt(endEndpoint.EntityId) &&
                    (pathPoints[pathPoints.Count - 1] - tool.GetObjectPose(endEndpoint.EntityId).position).sqrMagnitude < 0.01f)
                    pathPoints.RemoveAt(pathPoints.Count - 1);
                if (pathPoints.Count == 0)
                {
                    finishCommand(command, CommandFailed, "invalid_command", "Belt path contains no new points between its existing endpoints.", now, null);
                    return;
                }

                if (!AreAllWithinCommandIssueRange(player, pathPoints, out var failedTarget, out var commandDistance, out var commandRange))
                {
                    finishCommand(
                        command,
                        CommandFailed,
                        "out_of_range",
                        "Belt path is outside command issue range. Move closer or split the belt path before submitting placeBelt.",
                        now,
                        RangeFailureResult(commandDistance, commandRange, failedTarget));
                    return;
                }

                player.controller.cmd.type = ECommand.Build;
                command.EnteredBuildMode = true;
                player.controller.cmd.mode = item.BuildMode;
                player.controller.cmd.stage = 1;
                player.controller.cmd.refId = item.ID;
                var nativeStartTarget = (startEndpoint?.EntityId ?? 0) > 0
                    ? tool.GetObjectPose(startEndpoint.EntityId).position
                    : pathPoints[0];
                var nativeEndTarget = (endEndpoint?.EntityId ?? 0) > 0
                    ? tool.GetObjectPose(endEndpoint.EntityId).position
                    : pathPoints[pathPoints.Count - 1];
                player.controller.cmd.target = nativeEndTarget;
                player.controller.cmd.test = nativeStartTarget;

                tool.handItem = item;
                tool.handPrefabDesc = item.prefabDesc;
                tool.startObjectId = startEndpoint?.EntityId ?? 0;
                tool.castObjectId = endEndpoint?.EntityId ?? 0;
                tool.castObject = tool.castObjectId != 0;
                tool.castObjectPos = tool.castObjectId == 0 ? Vector3.zero : nativeEndTarget;
                tool.startTarget = nativeStartTarget;
                tool.cursorValid = true;
                tool.cursorTarget = nativeEndTarget;
                tool.castGroundPos = pathPoints[pathPoints.Count - 1];
                tool.castGroundPosSnapped = pathPoints[pathPoints.Count - 1];
                tool.altitude = 0;
                tool.tilt = 0f;
                tool.startTilt = 0f;
                tool.maxSlope = 0f;
                tool.pathPointCount = pathPoints.Count;
                tool.buildPreviews.Clear();

                for (var i = 0; i < pathPoints.Count; i++)
                {
                    tool.pathPoints[i] = pathPoints[i];
                    var preview = new BuildPreview();
                    preview.ResetAll();
                    preview.item = item;
                    preview.desc = item.prefabDesc;
                    preview.lpos = pathPoints[i];
                    preview.lpos2 = pathPoints[i];
                    preview.needModel = false;
                    preview.isConnNode = true;
                    preview.condition = EBuildCondition.Ok;
                    preview.genNearColliderArea2 = i % 6 == 0 || i == pathPoints.Count - 1 ? 25 : 0;
                    tool.buildPreviews.Add(preview);
                }

                for (var i = 0; i < tool.buildPreviews.Count - 1; i++)
                {
                    tool.buildPreviews[i].output = tool.buildPreviews[i + 1];
                    tool.buildPreviews[i].outputFromSlot = 0;
                    tool.buildPreviews[i].outputToSlot = 1;
                    tool.buildPreviews[i].outputOffset = 0;
                }

                if (!ApplyBeltEndpointConnections(factory, tool.buildPreviews, startEndpoint, endEndpoint, out errorMessage))
                {
                    finishCommand(command, CommandFailed, "invalid_connection", errorMessage, now, null);
                    return;
                }

                // 原生物理查询依赖附近碰撞体已激活，否则可能漏掉交叉处的已有带段。
                foreach (var preview in tool.buildPreviews)
                {
                    ActivateBuildColliders(factory, preview);
                }

                command.Phase = "validating";
                var canBuild = tool.CheckBuildConditions();
                var failedPreview = FirstFailedPreview(tool.buildPreviews);
                if (!canBuild || failedPreview != null)
                {
                    var condition = failedPreview?.condition ?? EBuildCondition.Failure;
                    if (condition == EBuildCondition.OutOfReach &&
                        TryApproachBuildTarget(player, failedPreview.lpos))
                    {
                        command.Phase = "approaching";
                        return;
                    }

                    finishCommand(
                        command,
                        CommandFailed,
                        BuildConditionCode(condition),
                        failedPreview?.conditionText ?? "Build condition failed.",
                        now,
                        new JsonObject { ["condition"] = condition.ToString() });
                    return;
                }

                command.Phase = "creatingPrebuild";
                tool.CreatePrebuilds();

                var targets = new List<BuildWaitTarget>();
                foreach (var preview in tool.buildPreviews)
                {
                    if (preview.objId == 0)
                    {
                        finishCommand(command, CommandFailed, "build_failed", "Build tool did not create all belt prebuilds.", now, null);
                        return;
                    }

                    targets.Add(new BuildWaitTarget(preview.objId, itemId, preview.lpos, preview));
                }

                command.BuildItemId = itemId;
                command.BuildTargets = targets;
            }
            finally
            {
                tool._Free();
            }

            WaitForBuiltObjectsLocked(command, now);
        }
        private static bool TryGetBeltPathPoints(
            TaskState task,
            CommandState command,
            AutomationPathBuildTool tool,
            PlanetFactory factory,
            out List<Vector3> pathPoints,
            out BeltEndpoint startEndpoint,
            out BeltEndpoint endEndpoint,
            out string errorMessage)
        {
            pathPoints = new List<Vector3>();
            startEndpoint = null;
            endEndpoint = null;
            errorMessage = null;

            if (TryGetToken(command, "points", out var pointsToken))
            {
                var points = pointsToken as JArray;
                if (points == null || points.Count < 2)
                {
                    errorMessage = "placeBelt points must include at least two positions.";
                    return false;
                }

                var rawPoints = new List<Vector3>();
                for (var i = 0; i < points.Count; i++)
                {
                    if (!TryReadVectorToken(points[i], $"points[{i}]", out var point, out errorMessage))
                    {
                        return false;
                    }

                    rawPoints.Add(point);
                }

                if (HasBeltEndpointInput(command, "start") &&
                    !TryGetBeltEndpoint(task, command, "start", out startEndpoint, out errorMessage))
                {
                    return false;
                }

                if (HasBeltEndpointInput(command, "end") &&
                    !TryGetBeltEndpoint(task, command, "end", out endEndpoint, out errorMessage))
                {
                    return false;
                }

                foreach (var point in rawPoints)
                {
                    if (pathPoints.Count > 0 &&
                        (pathPoints[pathPoints.Count - 1] - point).sqrMagnitude < 0.01f)
                    {
                        continue;
                    }

                    pathPoints.Add(point);
                }

                return pathPoints.Count >= 2;
            }

            if (!TryGetBeltEndpoint(task, command, "start", out startEndpoint, out errorMessage) ||
                !TryGetBeltEndpoint(task, command, "end", out endEndpoint, out errorMessage))
            {
                errorMessage = "placeBelt requires points or startPosition/endPosition.";
                return false;
            }

            if (!TryResolveBeltEndpoint(tool, factory, startEndpoint, out var start, out errorMessage) ||
                !TryResolveBeltEndpoint(tool, factory, endEndpoint, out var end, out errorMessage))
            {
                return false;
            }

            var beginFlat = startEndpoint?.EntityId <= 0;
            return AppendBeltLine(factory, start, end, beginFlat, pathPoints, out errorMessage) && pathPoints.Count >= 2;
        }

        private static bool HasBeltEndpointInput(CommandState command, string name)
        {
            return TryGetToken(command, name, out _) ||
                TryGetToken(command, name + "Position", out _) ||
                TryGetInt(command, name + "EntityId", out _) ||
                TryGetInt(command, name + "Slot", out _) ||
                TryGetInt(command, name + "EntityIndex", out _);
        }

        private static bool AppendBeltLine(
            PlanetFactory factory,
            Vector3 start,
            Vector3 end,
            bool beginFlat,
            List<Vector3> pathPoints,
            out string errorMessage)
        {
            errorMessage = null;
            var snaps = new Vector3[256];
            var maxSlope = 0f;
            var count = factory.planet.aux.SnapLineNonAlloc(
                start,
                end,
                1,
                geodesic: false,
                begin_flat: beginFlat,
                snaps,
                forceVertical: false,
                ref maxSlope,
                useOldPath: false);
            if (count <= 0)
            {
                errorMessage = "Belt path could not be snapped to the planet grid.";
                return false;
            }

            // 与原生 BuildTool_Path 一致，吸附中间点后恢复实际端点，避免在偏离网格的已有带段旁重复建带。
            snaps[0] = start;
            snaps[count - 1] = end;

            for (var i = 0; i < count; i++)
            {
                if (pathPoints.Count > 0 &&
                    (pathPoints[pathPoints.Count - 1] - snaps[i]).sqrMagnitude < 0.01f)
                {
                    continue;
                }

                if (pathPoints.Count >= 256)
                {
                    errorMessage = "Belt path is too long; maximum point count is 256.";
                    return false;
                }

                pathPoints.Add(snaps[i]);
            }

            return true;
        }

        private static BuildPreview FirstFailedPreview(List<BuildPreview> previews)
        {
            foreach (var preview in previews)
            {
                if (preview.condition != EBuildCondition.Ok)
                {
                    return preview;
                }
            }

            return null;
        }

        private static bool ApplyBeltEndpointConnections(
            PlanetFactory factory,
            List<BuildPreview> previews,
            BeltEndpoint startEndpoint,
            BeltEndpoint endEndpoint,
            out string errorMessage)
        {
            errorMessage = null;
            if (previews.Count == 0)
            {
                errorMessage = "Belt path has no previews.";
                return false;
            }

            if (startEndpoint?.EntityId > 0)
            {
                if (!TryEnsureObjectSlotAvailable(factory, startEndpoint.EntityId, startEndpoint.Slot, out errorMessage))
                {
                    return false;
                }

                previews[0].input = null;
                previews[0].inputObjId = startEndpoint.EntityId;
                previews[0].inputFromSlot = startEndpoint.Slot;
                previews[0].inputToSlot = 1;
                previews[0].inputOffset = 0;
            }

            if (endEndpoint?.EntityId > 0)
            {
                if (!TryEnsureObjectSlotAvailable(factory, endEndpoint.EntityId, endEndpoint.Slot, out errorMessage))
                {
                    return false;
                }

                var last = previews[previews.Count - 1];
                last.output = null;
                last.outputObjId = endEndpoint.EntityId;
                last.outputFromSlot = 0;
                last.outputToSlot = endEndpoint.Slot;
                last.outputOffset = 0;
            }

            return true;
        }

        private static bool TryEnsureObjectSlotAvailable(
            PlanetFactory factory,
            int entityId,
            int slot,
            out string errorMessage)
        {
            errorMessage = null;
            if (slot < 0)
            {
                return true;
            }

            factory.ReadObjectConn(entityId, slot, out var _, out var otherObjId, out var _);
            if (otherObjId == 0)
            {
                return true;
            }

            errorMessage = $"Object slot is already connected: {entityId}/{slot}";
            return false;
        }

        private static bool TryGetBeltEndpoint(
            TaskState task,
            CommandState command,
            string name,
            out BeltEndpoint endpoint,
            out string errorMessage)
        {
            endpoint = null;
            errorMessage = null;

            var entityId = 0;
            var slot = 0;
            var entityIndex = 0;
            var hasPosition = false;
            var position = Vector3.zero;
            string commandId = null;

            if (TryGetToken(command, name + "Position", out var topLevelPositionToken))
            {
                if (!TryReadVectorToken(topLevelPositionToken, name + "Position", out position, out errorMessage))
                {
                    return false;
                }

                hasPosition = true;
            }

            if (TryGetInt(command, name + "EntityId", out var topLevelEntityId))
            {
                entityId = topLevelEntityId;
            }

            if (TryGetInt(command, name + "Slot", out var topLevelSlot))
            {
                slot = topLevelSlot;
            }

            if (TryGetInt(command, name + "EntityIndex", out var topLevelEntityIndex))
            {
                entityIndex = topLevelEntityIndex;
            }

            if (TryGetToken(command, name, out var token))
            {
                if (TryReadVectorToken(token, name, out var tokenPosition, out _))
                {
                    position = tokenPosition;
                    hasPosition = true;
                }
                else if (token.Type == JTokenType.Integer)
                {
                    entityId = token.Value<int>();
                }
                else if (token is JObject obj)
                {
                    if (obj.TryGetValue("entityId", StringComparison.OrdinalIgnoreCase, out var entityToken))
                    {
                        entityId = entityToken.Value<int>();
                    }

                    if (obj.TryGetValue("commandId", StringComparison.OrdinalIgnoreCase, out var commandToken))
                    {
                        commandId = commandToken.Value<string>();
                    }

                    if (obj.TryGetValue("entityIndex", StringComparison.OrdinalIgnoreCase, out var entityIndexToken))
                    {
                        entityIndex = entityIndexToken.Value<int>();
                    }

                    if (obj.TryGetValue("slot", StringComparison.OrdinalIgnoreCase, out var slotToken))
                    {
                        slot = slotToken.Value<int>();
                    }

                    if (obj.TryGetValue("position", StringComparison.OrdinalIgnoreCase, out var nestedPositionToken))
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
                    errorMessage = $"{name} must be a position, entity id, or endpoint object.";
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

            if (entityId <= 0 && !hasPosition)
            {
                errorMessage = $"placeBelt requires {name}Position, {name}.position, {name}.entityId, or {name}.commandId.";
                return false;
            }

            endpoint = new BeltEndpoint(entityId, slot, hasPosition, position);
            return true;
        }

        private static bool TryResolveBeltEndpoint(
            AutomationPathBuildTool tool,
            PlanetFactory factory,
            BeltEndpoint endpoint,
            out Vector3 position,
            out string errorMessage)
        {
            position = Vector3.zero;
            errorMessage = null;

            if (endpoint.EntityId <= 0)
            {
                position = endpoint.Position;
                return true;
            }

            if (factory.entityPool == null ||
                endpoint.EntityId >= factory.entityPool.Length ||
                factory.entityPool[endpoint.EntityId].id != endpoint.EntityId)
            {
                errorMessage = $"Belt endpoint entity not found: {endpoint.EntityId}";
                return false;
            }

            if (endpoint.HasPosition)
            {
                position = endpoint.Position;
                return true;
            }

            var objectPose = tool.GetObjectPose(endpoint.EntityId);
            if (tool.ObjectIsBelt(endpoint.EntityId))
            {
                position = objectPose.position;
                return true;
            }

            var ports = tool.GetLocalPorts(endpoint.EntityId);
            if (ports == null || ports.Length == 0)
            {
                errorMessage = $"Belt endpoint has no ports: {endpoint.EntityId}";
                return false;
            }

            if (endpoint.Slot < 0 || endpoint.Slot >= ports.Length)
            {
                errorMessage = $"Belt endpoint slot is out of range: {endpoint.EntityId}/{endpoint.Slot}";
                return false;
            }

            position = objectPose.position + objectPose.rotation * ports[endpoint.Slot].position;
            return true;
        }
    }
}
