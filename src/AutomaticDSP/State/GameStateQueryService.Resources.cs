using System.Collections.Generic;
using AutomaticDSP.Serialization;

namespace AutomaticDSP.State
{
    internal sealed partial class GameStateQueryService
    {
        private static int RequiredObserveLevel(PlanetData planet)
        {
            // 与 UIPlanetDetail 的距离、当前天体和已建工厂例外保持一致。
            return planet == GameMain.localPlanet ? 1 : planet.star == GameMain.localStar ? 2 :
                (GameMain.mainPlayer.uPosition - planet.uPosition).magnitude < 14400000.0 ? 3 : 4;
        }

        private static bool CanObserveResources(PlanetData planet)
        {
            var level = GameMain.history.universeObserveLevel;
            return level >= RequiredObserveLevel(planet) || (planet.factory != null && level >= 1);
        }

        private static JsonObject CapturePlanetResources(PlanetData planet)
        {
            var observable = CanObserveResources(planet);
            if (observable && !planet.scanned && !planet.scanning)
                planet.RunScanThread();

            long[] amounts = null;
            int[] counts = null;
            var ready = observable && planet.scanned;
            if (ready && planet.type != EPlanetType.Gas)
            {
                var hashes = new HashSet<int>();
                ready = planet.SummarizeVeinAmountsByFilter(ref amounts, hashes, 0)
                    && planet.SummarizeVeinCountsByFilter(ref counts, hashes, 0);
            }

            var minerals = new List<object>();
            if (ready && amounts != null)
            {
                foreach (var vein in LDB.veins.dataArray)
                    minerals.Add(new JsonObject
                    {
                        ["typeId"] = vein.ID,
                        ["itemId"] = vein.MiningItem,
                        ["amount"] = amounts[vein.ID],
                        ["count"] = counts[vein.ID]
                    });
            }

            return new JsonObject
            {
                ["observable"] = observable,
                ["requiredObserveLevel"] = RequiredObserveLevel(planet),
                ["observeLevel"] = GameMain.history.universeObserveLevel,
                ["status"] = !observable ? "unknown" : ready ? "known" : "scanning",
                ["scanned"] = planet.scanned,
                ["minerals"] = ready ? minerals : null,
                // 海洋类型原生面板始终展示；气体产量仅在可观察时展示。
                ["waterItemId"] = planet.waterItemId,
                ["gasItems"] = observable ? planet.gasItems : null,
                ["gasSpeeds"] = observable ? planet.gasSpeeds : null
            };
        }

        private static bool IsResourceMemberRestricted(object source, string name)
        {
            if (source is PlanetData planet)
            {
                switch (name)
                {
                    case "seed":
                    case "infoSeed":
                        return true;
                    case "gasItems":
                    case "gasSpeeds":
                    case "gasHeatValues":
                    case "gasTotalHeat":
                        return !CanObserveResources(planet);
                    case "veinGroups":
                    case "runtimeVeinGroups":
                    case "veinBiasVector":
                    case "birthResourcePoint0":
                    case "birthResourcePoint1":
                        return !CanObserveResources(planet) || !planet.scanned;
                    case "data":
                    case "factory":
                    case "physics":
                    case "aux":
                        return planet != GameMain.localPlanet &&
                            (!CanObserveResources(planet) || !planet.scanned);
                }
            }

            // factories 等根可直接到达工厂，不能只守住 PlanetData.factory。
            if (source is PlanetFactory factory && factory.planet != GameMain.localPlanet)
                return !CanObserveResources(factory.planet) || !factory.planet.scanned;
            return source is StarData && name == "seed";
        }
    }
}
