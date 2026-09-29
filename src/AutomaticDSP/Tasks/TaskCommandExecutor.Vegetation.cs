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
        private void ExecuteVegetation(CommandState command, DateTimeOffset now)
        {
            if (!TryGetTerrainPlayer(command, now, out var player)) return;
            var factory = player.factory;
            var collection = player.vegetableCollection;
            var collecting = command.NormalizedType == "collectvegetation";
            var idName = collecting ? "vegeId" : "protoId";
            if (!TryGetToken(command, idName, out var idToken) || idToken.Type != JTokenType.Integer ||
                idToken.Value<double>() < 1 || idToken.Value<double>() > int.MaxValue)
            {
                finishCommand(command, CommandFailed, "invalid_command", idName + " 必须是正整数。", now, null);
                return;
            }
            var vegeId = GetInt(command, "vegeId", 0);
            var protoId = GetInt(command, "protoId", 0);
            Vector3 position;
            var rotation = (float)GetDouble(command, "rotation", 0);
            if (collecting)
            {
                if (vegeId <= 0 || vegeId >= factory.vegeCursor || factory.vegePool[vegeId].id != vegeId)
                {
                    finishCommand(command, CommandFailed, "target_not_found", "指定的植被不存在。", now, null);
                    return;
                }
                protoId = factory.vegePool[vegeId].protoId;
                position = factory.vegePool[vegeId].pos;
            }
            else if (!TryGetVector(command, "position", out position, out _) || position.sqrMagnitude < 0.001f ||
                float.IsNaN(position.sqrMagnitude) || float.IsInfinity(position.sqrMagnitude) || float.IsNaN(rotation) || float.IsInfinity(rotation))
            {
                finishCommand(command, CommandFailed, "invalid_command", "种植需要有限非零 position 和有限 rotation。", now, null);
                return;
            }
            var proto = LDB.veges.Select(protoId);
            if (proto == null || protoId <= 0 || protoId >= 9999 || proto.Type == EVegeType.VFX)
            {
                finishCommand(command, CommandFailed, "invalid_command", "目标必须是原生植被收藏支持的植被，不包括飞行仓、特效或矿脉。", now, null);
                return;
            }
            if (!collecting) position = position.normalized * factory.planet.data.QueryModifiedHeight(position);
            var distance = Vector3.Distance(player.position, position);
            if (distance > player.mecha.buildArea)
            {
                finishCommand(command, CommandFailed, "out_of_range", "请先移动到植被目标的建造范围内。", now,
                    RangeFailureResult(distance, player.mecha.buildArea, position));
                return;
            }
            collection.playerVegeDict.TryGetValue(protoId, out var before);
            if (!collecting && before == 0 && !GameMain.sandboxToolsEnabled)
            {
                finishCommand(command, CommandFailed, "missing_vegetation", "植被收藏中没有该原型。", now, null);
                return;
            }

            if (collecting)
            {
                // 与原生 RemoveAction 的植被分支一致，不触发采矿产物，也不允许删除矿脉。
                collection.AddVegeToPlayer(protoId);
                factory.RemoveVegeWithComponents(vegeId);
                collection.playerVegeDict.TryGetValue(protoId, out var after);
                var valid = factory.vegePool[vegeId].id == 0 && after == before + 1;
                finishCommand(command, valid ? CommandSucceeded : CommandFailed, valid ? null : "vegetation_failed",
                    valid ? null : "原生收取结果未通过校验。", now,
                    new JsonObject { ["vegeId"] = vegeId, ["protoId"] = protoId, ["position"] = Vector(position), ["collectionCount"] = after, ["collectionDelta"] = after - before });
                return;
            }

            var action = new PlayerAction_Plant();
            action.Init(player);
            try
            {
                action.SetFactoryReferences();
                action.castGroundPos = position;
                action.handPlantPreview = new ModelPreview
                {
                    type = EObjectType.Vegetable, protoId = protoId, modelIndex = proto.ModelIndex,
                    pos = position, lrot = Mathf.Repeat(rotation, 360f), rot = Maths.SphericalRotation(position, Mathf.Repeat(rotation, 360f))
                };
                action.UpdateCollidersForCursor();
                if (!action.CheckPlantCondition())
                {
                    var condition = action.handPlantPreview.condition;
                    finishCommand(command, CommandFailed, BuildConditionCode(condition), "原生植被放置校验失败。", now,
                        new JsonObject { ["nativeCondition"] = condition.ToString(), ["position"] = Vector(position) });
                    return;
                }
                var occupied = new HashSet<int>();
                for (var i = 1; i < factory.vegeCursor; i++)
                    if (factory.vegePool[i].id == i) occupied.Add(i);
                action.PlantVegeFinally();
                var createdId = 0;
                for (var i = 1; i < factory.vegeCursor; i++)
                    if (!occupied.Contains(i) && factory.vegePool[i].id == i && factory.vegePool[i].protoId == protoId &&
                        (factory.vegePool[i].pos - position).sqrMagnitude < 0.0001f)
                    {
                        createdId = i;
                        break;
                    }
                collection.playerVegeDict.TryGetValue(protoId, out var after);
                var valid = createdId > 0 && after == before - (GameMain.sandboxToolsEnabled ? 0 : 1);
                finishCommand(command, valid ? CommandSucceeded : CommandFailed, valid ? null : "vegetation_failed",
                    valid ? null : "原生种植未生成预期植被或收藏扣除不符。", now,
                    new JsonObject { ["vegeId"] = createdId, ["protoId"] = protoId, ["position"] = Vector(position),
                        ["nativeCondition"] = action.handPlantPreview.condition.ToString(), ["collectionCount"] = after, ["collectionDelta"] = after - before });
            }
            finally { action.Free(); }
        }
    }
}
