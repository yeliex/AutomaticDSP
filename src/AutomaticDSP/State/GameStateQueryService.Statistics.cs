using System;
using System.Collections.Generic;
using System.Linq;
using AutomaticDSP.Serialization;

namespace AutomaticDSP.State
{
    internal sealed partial class GameStateQueryService
    {
        private static JsonObject CapturePanelStatistics(StateQueryField field, long tick)
        {
            var data = GameMain.data;
            var production = GameMain.statistics?.production;
            if (data == null || production == null) return new JsonObject { ["available"] = false, ["reason"] = "statistics_missing" };
            var astro = field.AstroFilter ?? 0;
            var time = field.TimeLevel ?? 0;
            if (astro == 0) astro = GameMain.localPlanet?.id ?? (GameMain.localStar?.id * 100 ?? -1);
            var planet = astro > 0 && astro % 100 != 0 ? data.galaxy.PlanetById(astro) : null;
            var star = astro > 0 && astro % 100 == 0 ? data.galaxy.StarById(astro / 100) : null;
            if (astro > 0 && planet == null && star == null) return new JsonObject { ["available"] = false, ["reason"] = "astro_not_found" };
            var factories = new List<PlanetFactory>();
            for (var i = 0; i < data.factoryCount; i++)
            {
                var factory = data.factories[i];
                if (factory != null && (astro == -1 || factory.planetId == astro || factory.planet.star == star))
                    factories.Add(factory);
            }
            var stats = factories.Select(f => production.factoryStatPool[f.index]).ToList();
            if (stats.Any(s => s == null)) return new JsonObject { ["available"] = false, ["reason"] = "factory_statistics_missing" };
            var trafficSystem = GameMain.statistics?.traffic;
            var traffic = astro == -1 || trafficSystem == null ? null
                : star != null ? trafficSystem.starTrafficPool[star.id]
                : planet.factoryIndex >= 0 ? trafficSystem.factoryTrafficPool[planet.factoryIndex] : null;
            var includePlayer = astro == -1 || (star != null && GameMain.localStar == star)
                || (planet != null && GameMain.localPlanet == planet && !data.mainPlayer.sailing);
            var result = new JsonObject
            {
                ["available"] = true, ["gameTick"] = tick, ["astroFilter"] = astro,
                ["scope"] = astro == -1 ? "galaxy" : star != null ? "star" : "planet",
                ["factoryCount"] = factories.Count, ["timeLevel"] = time,
                ["windowSeconds"] = time == 5 ? (object)null : StatisticsWindow.Seconds[time],
                ["historyLevel"] = StatisticsWindow.HistoryLevel(time, tick),
                ["windowFilled"] = time == 5 || tick >= StatisticsWindow.Seconds[time] * 60L,
                ["trafficAvailable"] = astro != -1 && trafficSystem != null,
                ["trafficScope"] = astro == -1 ? null : star != null ? "interstellar" : "interplanetary",
                ["includesPlayerStorage"] = includePlayer,
                ["products"] = null, ["power"] = null, ["research"] = null, ["dyson"] = null,
                ["productionExtraInfo"] = new JsonObject
                {
                    ["source"] = "nativeCache", ["freshness"] = "unknown",
                    ["calculating"] = production.extraInfoCalculator?.calculating,
                    ["lastCalculationMilliseconds"] = production.extraInfoCalculator?.lastCalcCostTime
                }
            };
            var projected = new JsonObject();
            foreach (var selection in field.Children)
            {
                switch (selection.Name)
                {
                    case "products":
                        // 和面板共用原生缓存；查询不触发全厂扫描，也不干预原生刷新队列。
                        result["products"] = PanelProducts(stats, traffic, astro != -1 && trafficSystem != null,
                            includePlayer ? production.playerStorageCounts : null, time, tick, selection, false);
                        break;
                    case "power":
                        result["power"] = PanelPower(stats, factories, time, tick, selection);
                        break;
                    case "research":
                        result["research"] = PanelResearch(stats, time, tick, selection);
                        break;
                    case "dyson":
                        var dyson = new JsonObject();
                        foreach (var child in selection.Children)
                        {
                            object value = child.Name == "products"
                                ? PanelProducts(stats, null, false, null, time, tick, child, true)
                                : child.Name == "spheres" ? PanelSpheres(data, astro == -1 ? null : star ?? planet.star)
                                : child.Name == "_fields" ? DiscoverFields(new JsonObject { ["products"] = new List<object>(), ["spheres"] = new List<object>() }) : null;
                            dyson[child.ResponseName] = ResolveQueryValue(value, child, 2);
                        }
                        projected[selection.ResponseName] = dyson;
                        continue;
                }
                projected[selection.ResponseName] = ResolveQueryValue(
                    selection.Name == "_fields" ? DiscoverFields(result) : result.ContainsKey(selection.Name) ? result[selection.Name] : null,
                    selection, 1);
            }
            return field.Children.Count == 0 ? result : projected;
        }

        private static List<object> PanelProducts(List<FactoryProductionStat> stats, AstroTrafficStat traffic,
            bool trafficAvailable, long[] playerStorage, int time, long tick, StateQueryField selection, bool dyson)
        {
            var ids = new SortedSet<int>();
            if (dyson) ids.UnionWith(new[] { 11901, 11902, 11903 });
            else
            {
                foreach (var stat in stats)
                    for (var i = 1; i < stat.productCursor; i++) ids.Add(stat.productPool[i].itemId);
                if (traffic != null)
                    for (var i = 1; i < traffic.trafficCursor; i++) ids.Add(traffic.trafficPool[i].itemId);
                if (playerStorage != null)
                    for (var i = 1; i < playerStorage.Length; i++) if (playerStorage[i] > 0) ids.Add(i);
                ids.ExceptWith(new[] { 11901, 11902, 11903 });
            }
            var rows = new List<object>();
            foreach (var id in ids)
            {
                var products = stats.Select(s => s.productIndices[id] > 0 ? s.productPool[s.productIndices[id]] : null)
                    .Where(p => p != null).ToList();
                var transport = traffic != null && traffic.itemIndices[id] > 0 ? traffic.trafficPool[traffic.itemIndices[id]] : null;
                var produced = products.Sum(p => p.total[time + 1]);
                var consumed = products.Sum(p => p.total[time + 8]);
                var imported = transport?.total[time + 1] ?? 0;
                var exported = transport?.total[time + 8] ?? 0;
                var row = new JsonObject
                {
                    ["itemId"] = id, ["name"] = dyson ? (id == 11901 ? "太阳帆" : id == 11902 ? "结构点" : "细胞点") : ItemName(id),
                    ["produced"] = produced, ["consumed"] = consumed,
                    ["productionPerMinute"] = StatisticsWindow.PerMinute(produced, time),
                    ["consumptionPerMinute"] = StatisticsWindow.PerMinute(consumed, time),
                    ["imported"] = trafficAvailable ? (object)imported : null,
                    ["exported"] = trafficAvailable ? (object)exported : null,
                    ["importPerMinute"] = trafficAvailable ? StatisticsWindow.PerMinute(imported, time) : null,
                    ["exportPerMinute"] = trafficAvailable ? StatisticsWindow.PerMinute(exported, time) : null
                };
                if (!dyson)
                {
                    row["referenceProductionPerMinute"] = products.Sum(p => (double)p.refProductSpeed);
                    row["referenceConsumptionPerMinute"] = products.Sum(p => (double)p.refConsumeSpeed);
                    row["storage"] = products.Sum(p => p.storageCount) + (playerStorage?[id] ?? 0);
                    row["playerStorage"] = playerStorage?[id] ?? 0;
                    row["trash"] = products.Sum(p => p.trashCount);
                    row["importStorage"] = products.Sum(p => p.importStorageCount);
                    row["exportStorage"] = products.Sum(p => p.exportStorageCount);
                }
                rows.Add(row);
            }
            if (selection == null) return rows;
            var historyLevel = StatisticsWindow.HistoryLevel(time, tick);
            // 只为筛选及分页后会返回的行展开历史；最终字段投影仍由通用查询器处理。
            foreach (JsonObject row in rows.Where(r => MatchesFilters(r, selection.Filters))
                .Skip(Math.Max(0, selection.Offset ?? 0)).Take(Math.Min(MaxQueryListLimit, Math.Max(0, selection.Limit ?? DefaultQueryListLimit))))
            {
                var id = (int)row["itemId"];
                foreach (var child in selection.Children)
                {
                    var isTraffic = child.Name == "importHistory" || child.Name == "exportHistory";
                    if (!isTraffic && child.Name != "productionHistory" && child.Name != "consumptionHistory") continue;
                    if (isTraffic && !trafficAvailable) { row[child.Name] = null; continue; }
                    var group = child.Name == "consumptionHistory" || child.Name == "exportHistory" ? 1 : 0;
                    var series = new long[isTraffic ? 60 : 600];
                    if (isTraffic)
                    {
                        var index = traffic?.itemIndices[id] ?? 0;
                        if (index > 0)
                        {
                            var t = traffic.trafficPool[index];
                            AddSeries(series, StatisticsWindow.ProductHistory(t.count, t.cursor, historyLevel, group, tick, true));
                        }
                    }
                    else foreach (var stat in stats)
                    {
                        var index = stat.productIndices[id];
                        if (index <= 0) continue;
                        var p = stat.productPool[index];
                        AddSeries(series, StatisticsWindow.ProductHistory(p.count, p.cursor, historyLevel, group, tick, false));
                    }
                    row[child.Name] = PanelHistory(series, historyLevel, isTraffic, "items", true);
                }
            }
            return rows;
        }

        private static JsonObject PanelPower(List<FactoryProductionStat> stats, List<PlanetFactory> factories,
            int time, long tick, StateQueryField selection)
        {
            var result = new JsonObject { ["currentWindowSeconds"] = 1 };
            var names = new[] { "generationCapacity", "consumptionDemand", "charge", "discharge", "research", "shieldCharge" };
            for (var i = 0; i < names.Length; i++)
            {
                if (i == 4) continue;
                result[names[i] + "Watts"] = stats.Sum(s => StatisticsWindow.RecentPower(s.powerPool[i].energy, s.powerPool[i].cursor[0]));
                result[names[i] + "Joules"] = stats.Sum(s => s.powerPool[i].total[time + 1]);
                if (selection.Children.Any(c => c.Name == names[i] + "History"))
                    result[names[i] + "History"] = PanelPowerHistory(stats, i, time, tick, "J");
            }
            result["consumptionJoulesTotal"] = stats.Sum(s => s.energyConsumption);
            long stored = 0;
            foreach (var factory in factories)
                for (var i = 1; i < factory.powerSystem.netCursor; i++)
                {
                    var network = factory.powerSystem.netPool[i];
                    if (network != null && network.id != 0) stored += network.energyStored;
                }
            result["storedJoules"] = stored;
            return result;
        }

        private static JsonObject PanelResearch(List<FactoryProductionStat> stats, int time, long tick, StateQueryField selection)
        {
            var hashes = stats.Sum(s => s.powerPool[4].total[time + 1]);
            var result = new JsonObject
            {
                ["hashes"] = hashes, ["hashesPerSecond"] = time == 5 ? (double?)null : hashes / (double)StatisticsWindow.Seconds[time]
            };
            if (selection.Children.Any(c => c.Name == "history")) result["history"] = PanelPowerHistory(stats, 4, time, tick, "hash");
            return result;
        }

        private static JsonObject PanelPowerHistory(List<FactoryProductionStat> stats, int index, int time, long tick, string unit)
        {
            var level = StatisticsWindow.HistoryLevel(time, tick);
            var series = new long[600];
            foreach (var stat in stats) AddSeries(series, StatisticsWindow.PowerHistory(stat.powerPool[index].energy, stat.powerPool[index].cursor, level));
            return PanelHistory(series, level, false, unit, false);
        }

        private static void AddSeries(long[] target, long[] values)
        {
            for (var i = 0; i < target.Length; i++) target[i] += values[i];
        }

        private static JsonObject PanelHistory(long[] values, int level, bool traffic, string unit, bool lastPartial)
        {
            return new JsonObject
            {
                ["timeLevel"] = level, ["sampleTicks"] = StatisticsWindow.SampleTicks(level, traffic),
                ["pointCount"] = values.Length, ["unit"] = unit, ["lastPointPartial"] = lastPartial,
                ["points"] = values.Select((value, index) => new JsonObject { ["index"] = index, ["value"] = value }).ToList()
            };
        }

        private static List<object> PanelSpheres(GameData data, StarData star)
        {
            var result = new List<object>();
            foreach (var sphere in data.dysonSpheres)
            {
                if (sphere == null || (star != null && sphere.starData != star)) continue;
                long structure = 0, structureMax = 0, cells = 0, cellsMax = 0;
                foreach (var layer in sphere.layersIdBased)
                {
                    if (layer == null) continue;
                    for (var i = 1; i < layer.nodeCursor; i++)
                    {
                        var node = layer.nodePool[i];
                        if (node == null || node.id != i) continue;
                        structure += node.totalSp; structureMax += node.totalSpMax;
                        cells += node.totalCp; cellsMax += node.totalCpMax;
                    }
                }
                result.Add(new JsonObject
                {
                    ["starId"] = sphere.starData.id, ["name"] = sphere.starData.displayName,
                    ["generationWatts"] = sphere.energyGenCurrentTick * 60L, ["requestedWatts"] = sphere.energyReqCurrentTick * 60L,
                    ["sailCount"] = sphere.swarm.sailCount, ["structurePoints"] = structure, ["structurePointsRequired"] = structureMax,
                    ["cellPoints"] = cells, ["cellPointsRequired"] = cellsMax
                });
            }
            return result;
        }
    }
}
