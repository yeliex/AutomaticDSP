using System;
using System.Collections.Generic;
using AutomaticDSP.Serialization;
using UnityEngine;
using static AutomaticDSP.Tasks.TaskStatusNames;

namespace AutomaticDSP.Tasks
{
    internal sealed partial class TaskCommandExecutor
    {
        private void CompleteBuildSubmission(CommandState command, DateTimeOffset now)
        {
            if (command.BuildPlanetId == 0) command.BuildPlanetId = GameMain.localPlanet.id;
            if (command.BuildTargets == null)
                command.BuildTargets = new List<BuildWaitTarget>
                {
                    new BuildWaitTarget(command.BuildObjectId, command.BuildItemId, command.BuildPosition, command.BuildPreview)
                };
            var objectIds = new List<int>();
            var prebuildIds = new List<int>();
            var entityIds = new List<int>();
            foreach (var target in command.BuildTargets)
            {
                objectIds.Add(target.ObjectId);
                if (target.ObjectId < 0) prebuildIds.Add(-target.ObjectId);
                else entityIds.Add(target.ObjectId);
            }
            var result = command.Result as JsonObject ?? new JsonObject { ["itemId"] = command.BuildItemId };
            result["planetId"] = command.BuildPlanetId;
            result["objectIds"] = objectIds;
            result["prebuildIds"] = prebuildIds;
            result["entityIds"] = entityIds;
            result["submitted"] = true;
            if (objectIds.Count == 1)
            {
                if (objectIds[0] < 0) result["prebuildId"] = -objectIds[0];
                else result["entityId"] = objectIds[0];
            }
            finishCommand(command, CommandSucceeded, null, null, now, result);
        }

        private static bool TryResolveBuildTarget(CommandState source, int index, out int entityId, out bool pending)
        {
            entityId = 0;
            pending = false;
            if (source.BuildTargets == null || index < 0 || index >= source.BuildTargets.Count) return false;
            var factory = GameMain.galaxy?.PlanetById(source.BuildPlanetId)?.factory;
            if (factory == null) { pending = true; return false; }
            var target = source.BuildTargets[index];
            var id = target.EntityId > 0 ? target.EntityId : target.ObjectId;
            if (id > 0 && id < factory.entityPool.Length && factory.entityPool[id].id == id &&
                factory.entityPool[id].protoId == target.ItemId &&
                (factory.entityPool[id].pos - target.Position).sqrMagnitude < 0.01f)
            {
                entityId = id;
                return true;
            }
            if (id < 0 && -id < factory.prebuildPool.Length && factory.prebuildPool[-id].id == -id &&
                !factory.prebuildPool[-id].isDestroyed)
            {
                pending = true;
                return false;
            }
            if (target.Preview?.desc?.isInserter == true)
            {
                // 只解析被后续指令引用的分拣器及其两端，不逐帧遍历整张蓝图。
                foreach (var endpoint in new[] { target.Preview.input, target.Preview.output })
                {
                    if (endpoint == null) continue;
                    var endpointIndex = source.BuildTargets.FindIndex(t => ReferenceEquals(t.Preview, endpoint));
                    if (endpointIndex < 0) continue;
                    if (!TryResolveBuildTarget(source, endpointIndex, out var endpointId, out pending)) return false;
                    if (ReferenceEquals(endpoint, target.Preview.input)) target.Preview.inputObjId = endpointId;
                    if (ReferenceEquals(endpoint, target.Preview.output)) target.Preview.outputObjId = endpointId;
                }
            }
            if (!TryFindBuiltEntity(factory, target.ItemId, target.Position, out entityId, target.Preview, 0.1f)) return false;
            target.EntityId = entityId;
            return true;
        }
        private static bool TryFindBuiltEntity(PlanetFactory factory, int itemId, Vector3 position, out int entityId, BuildPreview preview = null, float maxDistance = 1.5f)
        {
            entityId = 0;
            if (factory.entityPool == null)
            {
                return false;
            }

            var bestDistance = maxDistance * maxDistance;
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

                if (preview?.desc?.isInserter == true)
                {
                    // 多个分拣器可以共用同一取物位置，必须通过两端连接区分实体。
                    factory.ReadObjectConn(i, 1, out var _, out var inputObjectId, out var inputSlot);
                    factory.ReadObjectConn(i, 0, out var _, out var outputObjectId, out var outputSlot);
                    if (inputObjectId != preview.inputObjId || outputObjectId != preview.outputObjId ||
                        (preview.inputFromSlot >= 0 && inputSlot != preview.inputFromSlot) ||
                        (preview.outputToSlot >= 0 && outputSlot != preview.outputToSlot))
                    {
                        continue;
                    }
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
