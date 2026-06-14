using System;
using System.Collections.Generic;
using AutomaticDSP.Serialization;
using UnityEngine;
using static AutomaticDSP.Tasks.TaskStatusNames;

namespace AutomaticDSP.Tasks
{
    internal sealed partial class TaskCommandExecutor
    {
        private void WaitForBuiltObjectLocked(CommandState command, DateTimeOffset now)
        {
            command.Phase = "waitingBuilt";
            var factory = GameMain.localPlanet?.factory;
            if (factory == null)
            {
                finishCommand(command, CommandFailed, "game_not_ready", "Current planet factory is not loaded.", now, null);
                return;
            }

            var objId = command.BuildPreview?.objId ?? command.BuildObjectId;
            if (objId > 0 &&
                factory.entityPool != null &&
                objId < factory.entityPool.Length &&
                factory.entityPool[objId].id == objId)
            {
                finishCommand(
                    command,
                    CommandSucceeded,
                    null,
                    null,
                    now,
                    new JsonObject
                    {
                        ["entityId"] = objId,
                        ["itemId"] = command.BuildItemId
                    });
                return;
            }

            if (objId < 0)
            {
                var prebuildId = -objId;
                if (factory.prebuildPool != null &&
                    prebuildId < factory.prebuildPool.Length &&
                    factory.prebuildPool[prebuildId].id == prebuildId &&
                    !factory.prebuildPool[prebuildId].isDestroyed)
                {
                    return;
                }
            }

            if (TryFindBuiltEntity(factory, command.BuildItemId, command.BuildPosition, out var entityId))
            {
                finishCommand(
                    command,
                    CommandSucceeded,
                    null,
                    null,
                    now,
                    new JsonObject
                    {
                        ["entityId"] = entityId,
                        ["itemId"] = command.BuildItemId
                    });
                return;
            }

            finishCommand(command, CommandFailed, "build_failed", "Prebuild disappeared before a matching entity was found.", now, null);
        }

        private void WaitForBuiltObjectsLocked(CommandState command, DateTimeOffset now)
        {
            command.Phase = "waitingBuilt";
            var factory = GameMain.localPlanet?.factory;
            if (factory == null)
            {
                finishCommand(command, CommandFailed, "game_not_ready", "Current planet factory is not loaded.", now, null);
                return;
            }

            if (command.BuildTargets == null || command.BuildTargets.Count == 0)
            {
                finishCommand(command, CommandFailed, "build_failed", "Command has no build targets to wait for.", now, null);
                return;
            }

            var entityIds = new List<int>();
            foreach (var target in command.BuildTargets)
            {
                var objId = target.Preview?.objId ?? target.ObjectId;
                if (objId > 0 &&
                    factory.entityPool != null &&
                    objId < factory.entityPool.Length &&
                    factory.entityPool[objId].id == objId)
                {
                    target.EntityId = objId;
                    entityIds.Add(objId);
                    continue;
                }

                if (target.EntityId > 0 &&
                    factory.entityPool != null &&
                    target.EntityId < factory.entityPool.Length &&
                    factory.entityPool[target.EntityId].id == target.EntityId)
                {
                    entityIds.Add(target.EntityId);
                    continue;
                }

                if (objId < 0)
                {
                    var prebuildId = -objId;
                    if (factory.prebuildPool != null &&
                        prebuildId < factory.prebuildPool.Length &&
                        factory.prebuildPool[prebuildId].id == prebuildId &&
                        !factory.prebuildPool[prebuildId].isDestroyed)
                    {
                        return;
                    }
                }

                if (TryFindBuiltEntity(factory, target.ItemId, target.Position, out var entityId))
                {
                    target.EntityId = entityId;
                    entityIds.Add(entityId);
                    continue;
                }

                finishCommand(command, CommandFailed, "build_failed", "Prebuild disappeared before a matching entity was found.", now, null);
                return;
            }

            var result = new JsonObject
            {
                ["itemId"] = command.BuildItemId,
                ["entityIds"] = entityIds
            };
            if (entityIds.Count == 1)
            {
                result["entityId"] = entityIds[0];
            }

            finishCommand(command, CommandSucceeded, null, null, now, result);
        }

        private static bool TryFindBuiltEntity(PlanetFactory factory, int itemId, Vector3 position, out int entityId)
        {
            entityId = 0;
            if (factory.entityPool == null)
            {
                return false;
            }

            var bestDistance = 1.5f * 1.5f;
            for (var i = 1; i < factory.entityCursor && i < factory.entityPool.Length; i++)
            {
                var entity = factory.entityPool[i];
                if (entity.id != i || entity.protoId != itemId)
                {
                    continue;
                }

                var distance = (entity.pos - position).sqrMagnitude;
                if (distance >= bestDistance)
                {
                    continue;
                }

                bestDistance = distance;
                entityId = i;
            }

            return entityId > 0;
        }

        private static bool TryApproachBuildTarget(Player player, Vector3 target)
        {
            var buildArea = Math.Max(5f, player.mecha?.buildArea ?? 5f);
            var delta = target - player.position;
            if (delta.sqrMagnitude < buildArea * buildArea * 0.64f)
            {
                return false;
            }

            var standDistance = Math.Min(delta.magnitude, buildArea * 0.75f);
            var standPosition = target - delta.normalized * standDistance;
            standPosition = standPosition.normalized * target.magnitude;
            player.Order(OrderNode.MoveTo(standPosition), false);
            return true;
        }

        private static string BuildConditionCode(EBuildCondition condition)
        {
            switch (condition)
            {
                case EBuildCondition.Ok:
                    return null;
                case EBuildCondition.NotEnoughItem:
                    return "missing_item";
                case EBuildCondition.NeedTech:
                case EBuildCondition.BlueprintNeedTech:
                case EBuildCondition.BlueprintReformNeedTech:
                    return "tech_locked";
                case EBuildCondition.OutOfReach:
                case EBuildCondition.TooFar:
                    return "out_of_range";
                case EBuildCondition.TooShort:
                    return "invalid_connection";
                case EBuildCondition.Collide:
                case EBuildCondition.Occupied:
                case EBuildCondition.TooClose:
                case EBuildCondition.PowerTooClose:
                case EBuildCondition.WindTooClose:
                case EBuildCondition.TowerTooClose:
                case EBuildCondition.EjectorTooClose:
                case EBuildCondition.MK2MinerTooClose:
                case EBuildCondition.BlockTooClose:
                case EBuildCondition.PlasmaTooClose:
                case EBuildCondition.GeothermalTooClose:
                    return "collision";
                case EBuildCondition.NeedGround:
                case EBuildCondition.NeedWater:
                case EBuildCondition.NeedGeothermalResource:
                case EBuildCondition.TooSteep:
                case EBuildCondition.MayBeBuried:
                    return "terrain_blocked";
                case EBuildCondition.NeedResource:
                case EBuildCondition.NeedSingleResource:
                    return "missing_resource";
                case EBuildCondition.NeedConn:
                case EBuildCondition.NeedExport:
                case EBuildCondition.InputConflict:
                case EBuildCondition.InputFull:
                case EBuildCondition.BeltCannotConnectToBuilding:
                case EBuildCondition.BeltCannotConnectToBuildingWithInserterTip:
                case EBuildCondition.ConnWithErrorBuilding:
                    return "invalid_connection";
                default:
                    return "build_condition_failed";
            }
        }
    }
}
