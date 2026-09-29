using System.Collections.Generic;
using UnityEngine;
using AutomaticDSP.Serialization;

namespace AutomaticDSP.State
{
    internal sealed partial class GameStateQueryService
    {
        private static JsonObject CaptureTrash(bool includeDetails)
        {
            var container = GameMain.data?.trashSystem?.container;
            if (container == null) return Unavailable("trash_system_missing");
            var player = GameMain.mainPlayer;
            var planet = GameMain.localPlanet;
            var planetId = planet?.id ?? 0;
            var height = player == null ? 0f : planet == null ? 1000f : player.position.magnitude - planet.realRadius;
            var rangeSquared = Mathf.Clamp(height * height * 0.25f, 4900f, 250000f);
            var entries = includeDetails ? new List<object>() : null;
            var items = includeDetails ? null : new Dictionary<int, JsonObject>();
            var itemsTruncated = false;
            var total = 0;
            var local = 0;
            var other = 0;
            var floating = 0;
            for (var id = 0; id < container.trashCursor; id++)
            {
                var obj = container.trashObjPool[id];
                if (obj.item <= 0) continue;
                var data = container.trashDataPool[id];
                total++;
                var isLocal = planetId > 0 && data.landPlanetId == planetId;
                if (isLocal) local++;
                else if (data.landPlanetId > 0) other++;
                else floating++;
                if (!includeDetails)
                {
                    if (!items.TryGetValue(obj.item, out var item))
                    {
                        if (items.Count >= 8)
                        {
                            itemsTruncated = true;
                            continue;
                        }
                        item = new JsonObject
                        {
                            ["itemId"] = obj.item, ["name"] = LDB.items.Select(obj.item)?.name,
                            ["count"] = 0L, ["localCount"] = 0L
                        };
                        items.Add(obj.item, item);
                    }
                    // 摘要计物品数量；外层 count 计垃圾块数，截断不影响已列种类的汇总。
                    item["count"] = (long)item["count"] + obj.count;
                    if (isLocal) item["localCount"] = (long)item["localCount"] + obj.count;
                    continue;
                }
                var distance = player == null ? (float?)null : Vector3.Distance(player.position, obj.rPos);
                entries.Add(new JsonObject
                {
                    ["trashId"] = id, ["itemId"] = obj.item, ["count"] = obj.count, ["inc"] = obj.inc,
                    ["landPlanetId"] = data.landPlanetId, ["nearPlanetId"] = data.nearPlanetId,
                    ["nearStarId"] = data.nearStarId, ["localPosition"] = Vector(data.lPos),
                    ["universalPosition"] = Vector(data.uPos), ["relativePosition"] = Vector(obj.rPos),
                    ["isLocalPlanet"] = isLocal, ["distance"] = distance,
                    // 只表达几何范围，不承诺筛选、玩家状态或背包允许拾取。
                    ["withinPickupRange"] = player != null && (data.landPlanetId == 0 || isLocal) &&
                        distance.Value * distance.Value < rangeSquared,
                    ["expire"] = obj.expire, ["life"] = data.life
                });
            }
            if (!includeDetails)
                return new JsonObject
                {
                    ["count"] = total, ["localPlanetCount"] = local,
                    ["items"] = new List<JsonObject>(items.Values), ["itemsTruncated"] = itemsTruncated
                };
            return new JsonObject
            {
                ["localPlanetId"] = planetId, ["count"] = total, ["localPlanetCount"] = local,
                ["otherPlanetCount"] = other, ["floatingCount"] = floating,
                ["entries"] = entries, ["truncated"] = entries.Count < total
            };
        }
    }
}
