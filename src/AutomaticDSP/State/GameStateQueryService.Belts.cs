using System;
using System.Collections.Generic;
using System.Linq;
using AutomaticDSP.Serialization;

namespace AutomaticDSP.State
{
    internal sealed partial class GameStateQueryService
    {
        private static JsonObject CaptureBeltTopology(PlanetFactory factory, StateQueryField field)
        {
            var traffic = factory.cargoTraffic;
            var paths = new HashSet<int>();
            var matches = new List<object>();
            var sourceConnections = new List<JsonObject>();
            if (field.PathId.HasValue) paths.Add(field.PathId.Value);
            if (field.EntityId.HasValue && ValidBeltEntity(factory, field.EntityId.Value))
            {
                var id = field.EntityId.Value;
                var beltId = factory.entityPool[id].beltId;
                if (beltId > 0) paths.Add(traffic.beltPool[beltId].segPathId);
                else
                {
                    sourceConnections = BeltObjectConnections(factory, id)
                        .Where(c => factory.entityPool[(int)c["otherEntityId"]].beltId > 0).ToList();
                    foreach (var connection in sourceConnections)
                    {
                        var other = (int)connection["otherEntityId"];
                        var otherBelt = factory.entityPool[other].beltId;
                        if (otherBelt > 0) paths.Add(traffic.beltPool[otherBelt].segPathId);
                    }
                }
            }
            if (field.Position.HasValue)
            {
                var radiusSquared = field.Radius.Value * field.Radius.Value;
                for (var i = 1; i < traffic.beltCursor; i++)
                {
                    var belt = traffic.beltPool[i];
                    if (belt.id != i || !ValidBeltEntity(factory, belt.entityId)) continue;
                    var distanceSquared = (factory.entityPool[belt.entityId].pos - field.Position.Value).sqrMagnitude;
                    if (distanceSquared > radiusSquared) continue;
                    paths.Add(belt.segPathId);
                    matches.Add(new JsonObject { ["entityId"] = belt.entityId, ["beltId"] = i,
                        ["pathId"] = belt.segPathId, ["distance"] = Math.Sqrt(distanceSquared) });
                }
            }
            var resultPaths = new List<object>();
            foreach (var pathId in paths.OrderBy(x => x))
            {
                if (pathId <= 0 || pathId >= traffic.pathPool.Length) continue;
                var path = traffic.pathPool[pathId];
                if (path == null || path.id != pathId) continue;
                var nodes = new List<object>();
                var connections = new List<object>();
                // belts 只是成员列表；沿原生 segIndex 排序，不能把插入顺序当输送方向。
                foreach (var beltId in path.belts.Where(id => id > 0 && id < traffic.beltPool.Length &&
                    traffic.beltPool[id].id == id && traffic.beltPool[id].segPathId == pathId).OrderBy(id => traffic.beltPool[id].segIndex))
                {
                    var belt = traffic.beltPool[beltId];
                    if (!ValidBeltEntity(factory, belt.entityId)) continue;
                    var entity = factory.entityPool[belt.entityId];
                    nodes.Add(new JsonObject { ["beltId"] = beltId, ["entityId"] = belt.entityId,
                        ["segIndex"] = belt.segIndex, ["segLength"] = belt.segLength,
                        ["protoId"] = (int)entity.protoId, ["position"] = entity.pos });
                    foreach (var connection in BeltObjectConnections(factory, belt.entityId))
                    {
                        var other = (int)connection["otherEntityId"];
                        if (factory.entityPool[other].beltId > 0 && (int)connection["viaSorterEntityId"] == 0) continue;
                        connections.Add(connection);
                    }
                }
                resultPaths.Add(new JsonObject { ["pathId"] = pathId, ["closed"] = path.closed,
                    ["inputPathIds"] = path.inputPaths.ToArray(), ["outputPathId"] = path.outputPath?.id ?? 0,
                    ["outputIndex"] = path.outputIndex, ["nodeCount"] = nodes.Count, ["nodes"] = nodes,
                    ["connectionCount"] = connections.Count, ["connections"] = connections });
            }
            return new JsonObject { ["planetId"] = factory.planetId, ["pathCount"] = resultPaths.Count,
                ["paths"] = resultPaths, ["matchCount"] = matches.Count, ["matches"] = matches,
                ["sourceConnectionCount"] = sourceConnections.Count, ["sourceConnections"] = sourceConnections };
        }

        private static bool ValidBeltEntity(PlanetFactory factory, int id) =>
            id > 0 && id < factory.entityPool.Length && factory.entityPool[id].id == id;

        private static List<JsonObject> BeltObjectConnections(PlanetFactory factory, int id)
        {
            var result = new List<JsonObject>();
            for (var slot = 0; slot < 16; slot++)
            {
                factory.ReadObjectConn(id, slot, out var isOutput, out var other, out var otherSlot);
                if (!ValidBeltEntity(factory, other)) continue;
                var via = 0;
                if (factory.entityPool[other].inserterId > 0)
                {
                    via = other;
                    // 分拣器槽 0 输出、槽 1 输入；读取另一端，不沿工厂网络递归扩展。
                    factory.ReadObjectConn(via, otherSlot == 0 ? 1 : 0, out _, out other, out otherSlot);
                    if (!ValidBeltEntity(factory, other)) continue;
                }
                result.Add(new JsonObject { ["entityId"] = id, ["slot"] = slot,
                    ["isOutput"] = isOutput, ["otherEntityId"] = other, ["otherSlot"] = otherSlot,
                    ["viaSorterEntityId"] = via, ["otherProtoId"] = (int)factory.entityPool[other].protoId });
            }
            return result;
        }
    }
}
