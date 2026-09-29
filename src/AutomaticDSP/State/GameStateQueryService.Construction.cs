using System.Collections.Generic;
using AutomaticDSP.Serialization;

namespace AutomaticDSP.State
{
    internal sealed partial class GameStateQueryService
    {
        private static JsonObject CaptureConstructionSummary(GameData gameData, int localPlanetId)
        {
            if (gameData?.factories == null) return Unavailable("game_data_missing");
            var pending = 0;
            var destroyed = 0;
            var localPending = 0;
            var localDestroyed = 0;
            var items = new Dictionary<int, JsonObject>();
            var itemsTruncated = false;
            // 与原生全局预建提醒一样读取已有工厂，不加载或探索其他行星。
            foreach (var factory in gameData.factories)
            {
                if (factory == null) continue;
                var isLocal = localPlanetId > 0 && factory.planetId == localPlanetId;
                for (var id = 1; id < factory.prebuildCursor; id++)
                {
                    var prebuild = factory.prebuildPool[id];
                    if (prebuild.id != id || prebuild.protoId <= 0) continue;
                    // 距离、缺料或无人机未出发不等于被摧毁，分类只采用原生标记。
                    if (prebuild.isDestroyed) destroyed++;
                    else pending++;
                    if (isLocal)
                    {
                        if (prebuild.isDestroyed) localDestroyed++;
                        else localPending++;
                    }
                    if (!items.TryGetValue(prebuild.protoId, out var item))
                    {
                        if (items.Count >= 8)
                        {
                            itemsTruncated = true;
                            continue;
                        }
                        item = new JsonObject
                        {
                            ["itemId"] = (int)prebuild.protoId, ["name"] = LDB.items.Select(prebuild.protoId)?.name,
                            ["pendingCount"] = 0, ["destroyedCount"] = 0,
                            ["localPendingCount"] = 0, ["localDestroyedCount"] = 0
                        };
                        items.Add(prebuild.protoId, item);
                    }
                    var field = prebuild.isDestroyed ? "destroyedCount" : "pendingCount";
                    item[field] = (int)item[field] + 1;
                    if (isLocal)
                    {
                        var localField = prebuild.isDestroyed ? "localDestroyedCount" : "localPendingCount";
                        item[localField] = (int)item[localField] + 1;
                    }
                }
            }
            return new JsonObject
            {
                ["localPlanetId"] = localPlanetId, ["count"] = pending + destroyed,
                ["pendingCount"] = pending, ["destroyedCount"] = destroyed,
                ["localCount"] = localPending + localDestroyed,
                ["localPendingCount"] = localPending, ["localDestroyedCount"] = localDestroyed,
                ["items"] = new List<JsonObject>(items.Values), ["itemsTruncated"] = itemsTruncated
            };
        }
    }
}
