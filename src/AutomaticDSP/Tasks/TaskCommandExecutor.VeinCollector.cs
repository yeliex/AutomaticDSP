using System;
using AutomaticDSP.Serialization;
using Newtonsoft.Json.Linq;
using static AutomaticDSP.Tasks.TaskStatusNames;

namespace AutomaticDSP.Tasks
{
    internal sealed partial class TaskCommandExecutor
    {
        private void ExecuteVeinCollectorSpeed(CommandState command, PlanetFactory factory, EntityData entity, DateTimeOffset now)
        {
            var station = entity.stationId > 0 ? factory.transport.stationPool[entity.stationId] : null;
            if (station == null || !station.isVeinCollector || station.minerId <= 0 ||
                station.minerId >= factory.factorySystem.minerCursor || factory.factorySystem.minerPool[station.minerId].id != station.minerId)
            {
                finishCommand(command, CommandFailed, "invalid_miner", "目标必须为原生大矿机及其有效采矿组件。", now, null);
                return;
            }
            // 与原生总控滑块一致；用电、采矿和消耗由游戏后续 tick 计算。
            var slider = UIRoot.instance.uiGame.controlPanelWindow.stationInspector.maxMiningSpeedSlider;
            var min = 100 + (int)slider.minValue * 10;
            var max = 100 + (int)slider.maxValue * 10;
            var result = new JsonObject { ["planetId"] = factory.planetId, ["entityId"] = entity.id,
                ["minSpeedPercent"] = min, ["maxSpeedPercent"] = max };
            if (!TryGetToken(command, "speedPercent", out var value) || value.Type != JTokenType.Integer ||
                !int.TryParse(value.ToString(), out var percent) || percent < min || percent > max || percent % 10 != 0)
            {
                finishCommand(command, CommandFailed, "invalid_command", "speedPercent 必须为原生范围内的整数百分比，且为 10 的倍数。", now, result);
                return;
            }
            factory.factorySystem.minerPool[station.minerId].speed = percent * 100;
            result["speedPercent"] = percent;
            result["speed"] = factory.factorySystem.minerPool[station.minerId].speed;
            finishCommand(command, CommandSucceeded, null, null, now, result);
        }
    }
}
