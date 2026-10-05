using System;
using AutomaticDSP.Serialization;
using Newtonsoft.Json.Linq;
using UnityEngine;
using static AutomaticDSP.Tasks.TaskStatusNames;

namespace AutomaticDSP.Tasks
{
    internal sealed partial class TaskCommandExecutor
    {
        private void ExecuteStationOption(CommandState command, Player player, PlanetFactory factory,
            EntityData entity, StationComponent station, DateTimeOffset now)
        {
            var setting = GetString(command, "setting", "");
            if (!TryGetToken(command, "value", out var value))
            {
                finishCommand(command, CommandFailed, "invalid_command", "需要 setting 和 value。", now, null);
                return;
            }
            var integer = value.Type == JTokenType.Integer && int.TryParse(value.ToString(), out _);
            var number = integer ? value.Value<int>() : -1;
            var boolean = value.Type == JTokenType.Boolean;
            var result = new JsonObject { ["planetId"] = factory.planetId, ["entityId"] = entity.id, ["setting"] = setting };
            // 读取原生控件的静态范围；不切换面板，也不依赖面板当前选中的运输站。

            switch (setting)
            {
                case "droneRangeDegrees":
                    if (!integer || number < UIRoot.instance.uiGame.controlPanelWindow.stationInspector.maxTripDroneSlider.minValue || number > UIRoot.instance.uiGame.controlPanelWindow.stationInspector.maxTripDroneSlider.maxValue) break;
                    station.tripRangeDrones = Math.Cos(number / 180.0 * Math.PI);
                    result["tripRangeDrones"] = station.tripRangeDrones;
                    goto success;
                case "shipRangeLightYears":
                    if (!station.isStellar || !integer || number < 1 ||
                        !(number <= 20 || (number <= 60 && number % 2 == 0) || number == 10000)) break;
                    station.tripRangeShips = number * 2400000.0;
                    result["tripRangeShips"] = station.tripRangeShips;
                    goto success;
                case "warpDistanceAU":
                    if (!station.isStellar || (value.Type != JTokenType.Integer && value.Type != JTokenType.Float)) break;
                    var au = value.Value<double>();
                    if (!((au >= 0.5 && au <= 3 && au * 2 == Math.Floor(au * 2)) ||
                        (au >= 4 && au <= 12 && au == Math.Floor(au)) ||
                        (au >= 14 && au <= 20 && au % 2 == 0) || au == 60)) break;
                    station.warpEnableDist = au * 40000.0;
                    result["warpEnableDist"] = station.warpEnableDist;
                    goto success;
                case "deliveryDrones":
                case "deliveryShips":
                    if (!integer || (setting == "deliveryShips" && !station.isStellar) ||
                        !(number == 1 || (number >= 10 && number <= 100 && number % 10 == 0))) break;
                    if (setting == "deliveryShips") station.deliveryShips = number;
                    else station.deliveryDrones = number;
                    goto success;
                case "warperNecessary":
                case "includeOrbitCollector":
                    if (!station.isStellar || !boolean) break;
                    if (setting == "warperNecessary") station.warperNecessary = value.Value<bool>();
                    else station.includeOrbitCollector = value.Value<bool>();
                    goto success;
                case "pilerCount":
                    var max = 1;
                    for (var tech = 3801; tech <= 3803; tech++)
                        if (GameMain.history.TechUnlocked(tech)) max += (int)LDB.techs.Select(tech).UnlockValues[0];
                    result["maxPilerCount"] = max;
                    if (!integer || number < 0 || number > max) break;
                    station.pilerCount = number;
                    goto success;
                case "droneAutoReplenish":
                case "shipAutoReplenish":
                    var drone = setting == "droneAutoReplenish";
                    if (!boolean || (!drone && !station.isStellar)) break;
                    var itemId = drone ? 5001 : 5002;
                    var before = player.package.GetItemCount(itemId);
                    if (drone) station.droneAutoReplenish = value.Value<bool>();
                    else station.shipAutoReplenish = value.Value<bool>();
                    // 与总控按钮一致，原生方法从普通背包补至工具容量；不开辟货槽转运力路径。
                    factory.StationAutoReplenishIfNeeded(entity.id, Vector2.zero, drone);
                    result["itemId"] = itemId;
                    result["consumedCount"] = before - player.package.GetItemCount(itemId);
                    result["inventoryCount"] = player.package.GetItemCount(itemId);
                    result["vehicleCount"] = drone ? station.idleDroneCount + station.workDroneCount : station.idleShipCount + station.workShipCount;
                    goto success;
                case "remoteGroupMask":
                    if (!station.isStellar || value.Type != JTokenType.Integer || !long.TryParse(value.ToString(), out var mask) ||
                        mask < 0 || (mask >> UIRoot.instance.uiGame.controlPanelWindow.stationInspector.groupBtns.Length) != 0) break;
                    station.remoteGroupMask = mask;
                    GameMain.data.galacticTransport.RefreshTraffic();
                    goto success;
                case "routePriority":
                    if (!station.isStellar || value.Type != JTokenType.String ||
                        !Enum.TryParse(value.Value<string>(), true, out ERemoteRoutePriority priority) ||
                        !Enum.IsDefined(typeof(ERemoteRoutePriority), priority)) break;
                    station.routePriority = priority;
                    GameMain.data.galacticTransport.RefreshTraffic();
                    goto success;
            }
            finishCommand(command, CommandFailed, "invalid_command", "设置名称、类型、原生刻度、科技容量或运输站类型不允许此值。", now, result);
            return;
        success:
            result["value"] = ((JValue)value).Value;
            finishCommand(command, CommandSucceeded, null, null, now, result);
        }

        private void ExecuteLogisticsRoute(CommandState command, DateTimeOffset now)
        {
            var transport = GameMain.data?.galacticTransport;
            if (transport == null)
            {
                finishCommand(command, CommandFailed, "game_not_ready", "星际物流系统不可用。", now, null);
                return;
            }
            var kind = GetString(command, "kind", "");
            if (!TryGetToken(command, "present", out var presentToken) || presentToken.Type != JTokenType.Boolean ||
                !TryGetToken(command, "fromId", out var fromToken) || fromToken.Type != JTokenType.Integer ||
                !TryGetToken(command, "toId", out var toToken) || toToken.Type != JTokenType.Integer ||
                !int.TryParse(fromToken.ToString(), out var from) || !int.TryParse(toToken.ToString(), out var to) || from <= 0 || to <= 0 || from == to)
            {
                finishCommand(command, CommandFailed, "invalid_command", "需要不同的正整数 fromId/toId 及布尔 present。", now, null);
                return;
            }
            var present = presentToken.Value<bool>();
            var result = new JsonObject { ["kind"] = kind, ["fromId"] = from, ["toId"] = to, ["present"] = present };
            if (kind == "station")
            {
                if (from >= transport.stationPool.Length || to >= transport.stationPool.Length ||
                    transport.stationPool[from] == null || transport.stationPool[to] == null ||
                    transport.stationPool[from].gid != from || transport.stationPool[to].gid != to ||
                    !transport.stationPool[from].isStellar || !transport.stationPool[to].isStellar ||
                    transport.stationPool[from].planetId == transport.stationPool[to].planetId)
                {
                    finishCommand(command, CommandFailed, "invalid_command", "需要不同星球的有效星际运输站 gid。", now, null);
                    return;
                }
                if (present) transport.AddStation2StationRoute(from, to);
                else transport.RemoveStation2StationRoute(from, to);
                result["present"] = transport.IsStation2StationRouteExist(from, to);
            }
            else
            {
                var galaxy = GameMain.galaxy;
                var fromExists = from % 100 == 0 ? galaxy.StarById(from / 100) != null : galaxy.PlanetById(from) != null;
                var toExists = to % 100 == 0 ? galaxy.StarById(to / 100) != null : galaxy.PlanetById(to) != null;
                if ((kind != "astro" && kind != "ban") || !fromExists || !toExists ||
                    !TryGetToken(command, "itemId", out var itemToken) || itemToken.Type != JTokenType.Integer ||
                    !int.TryParse(itemToken.ToString(), out var itemId) || itemId < 0 ||
                    (itemId > 0 && (LDB.items.Select(itemId) == null || itemId == 11901 || itemId == 11902 || itemId == 11903)))
                {
                    finishCommand(command, CommandFailed, "invalid_command", "需要 astro/ban、有效天体 ID 和物品 ID；0 表示全部物流物品。", now, null);
                    return;
                }
                if (kind == "astro")
                {
                    if (present) transport.AddAstro2AstroRoute(from, to, itemId);
                    else transport.RemoveAstro2AstroRoute(from, to, itemId);
                }
                else
                {
                    if (present) transport.AddAstro2AstroBan(from, to, itemId);
                    else transport.RemoveAstro2AstroBan(from, to, itemId);
                }
                result["itemId"] = itemId;
                if (itemId > 0) result["present"] = kind == "astro" ? transport.IsAstro2AstroRouteExist(from, to, itemId) : transport.IsAstro2AstroBanExist(from, to, itemId);
            }
            finishCommand(command, CommandSucceeded, null, null, now, result);
        }
    }
}
