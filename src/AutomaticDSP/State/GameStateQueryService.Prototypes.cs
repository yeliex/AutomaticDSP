using System;
using System.Collections.Generic;
using AutomaticDSP.Serialization;

namespace AutomaticDSP.State
{
    internal sealed partial class GameStateQueryService
    {
        private static readonly string[] PrefabQueryFields =
        {
            "workPowerW", "idlePowerW", "generationPowerW", "fuelPowerW",
            "assemblerSpeedMultiplier", "labSpeedMultiplier", "capacityJ",
            "chargePowerW", "dischargePowerW"
        };

        private static List<object> PrototypeFieldContracts()
        {
            return new List<object>
            {
                PrototypeField("items.heatValueJ", "J/item", "ItemProto.HeatValue", "每件基础热值，不含喷涂。"),
                PrototypeField("items.fuelType", "bitmask", "ItemProto.FuelType", "原生燃料类型；结合设备 fuelMask 判断接受范围。"),
                PrototypeField("items.reactorInc", "ratio", "ItemProto.ReactorInc", "机甲燃烧室基础功率倍率为 1 + reactorInc；喷涂和机甲状态另计。"),
                PrototypeField("recipes.timeSeconds", "game-second/cycle", "RecipeProto.TimeSpend / 60", "基础周期，特殊机制不能直接套用普通制造公式。"),
                PrototypeField("recipes.productive", "boolean", "RecipeProto.productive", "原生额外产出标记；特殊加工机制仍需核实。"),
                PrototypeField("items.prefabDesc.workPowerW", "W", "PrefabDesc.workEnergyPerTick * 60", "基础工作耗电，不含增产及实例修正。"),
                PrototypeField("items.prefabDesc.idlePowerW", "W", "PrefabDesc.idleEnergyPerTick * 60", "基础待机耗电。"),
                PrototypeField("items.prefabDesc.generationPowerW", "W", "PrefabDesc.genEnergyPerTick * 60", "额定发电能力，非当前出力。"),
                PrototypeField("items.prefabDesc.fuelPowerW", "W", "PrefabDesc.useFuelPerTick * 60", "基础燃料能量消耗率，非当前负载下的消耗。"),
                PrototypeField("items.prefabDesc.assemblerSpeedMultiplier", "ratio", "PrefabDesc.assemblerSpeed / 10000", "制造设备基础速度；非制造设备为 null。"),
                PrototypeField("items.prefabDesc.labSpeedMultiplier", "ratio", "PrefabDesc.labAssembleSpeed / 10000", "研究站制造模式基础速度，不是科研 hash 速度。"),
                PrototypeField("items.prefabDesc.capacityJ", "J", "PrefabDesc.maxAcuEnergy", "蓄电器基础容量，非蓄电器为 null。"),
                PrototypeField("items.prefabDesc.chargePowerW", "W", "PrefabDesc.inputEnergyPerTick * 60", "蓄电器基础充电功率。"),
                PrototypeField("items.prefabDesc.dischargePowerW", "W", "PrefabDesc.outputEnergyPerTick * 60", "蓄电器基础放电功率。")
            };
        }

        private static JsonObject PrototypeField(string path, string unit, string sourcePath, string description)
        {
            return new JsonObject
            {
                ["path"] = path, ["unit"] = unit, ["sourcePath"] = sourcePath,
                ["description"] = description, ["valueScope"] = "prototype",
                ["nullable"] = path.StartsWith("items.prefabDesc.", StringComparison.Ordinal)
            };
        }

        private static JsonObject CaptureCargoTables()
        {
            return new JsonObject
            {
                ["incTableMilli"] = Cargo.incTableMilli,
                ["accTableMilli"] = Cargo.accTableMilli,
                ["powerTableRatio"] = Cargo.powerTableRatio
            };
        }

        private static bool TryGetPrototypeMember(object source, string name, out object value)
        {
            value = null;
            if (!(source is PrefabDesc desc))
            {
                return false;
            }

            // 这些值是基础原型能力；供电、喷涂、科技和环境影响需从实例读取。
            switch (name)
            {
                case "workPowerW": value = desc.isPowerConsumer ? (object)(desc.workEnergyPerTick * 60.0) : null; return true;
                case "idlePowerW": value = desc.isPowerConsumer ? (object)(desc.idleEnergyPerTick * 60.0) : null; return true;
                case "generationPowerW": value = desc.isPowerGen ? (object)(desc.genEnergyPerTick * 60.0) : null; return true;
                case "fuelPowerW": value = desc.isPowerGen && desc.fuelMask != 0 ? (object)(desc.useFuelPerTick * 60.0) : null; return true;
                case "assemblerSpeedMultiplier": value = desc.isAssembler ? (object)(desc.assemblerSpeed / 10000.0) : null; return true;
                case "labSpeedMultiplier": value = desc.isLab ? (object)(desc.labAssembleSpeed / 10000.0) : null; return true;
                case "capacityJ": value = desc.isAccumulator ? (object)desc.maxAcuEnergy : null; return true;
                case "chargePowerW": value = desc.isAccumulator ? (object)(desc.inputEnergyPerTick * 60.0) : null; return true;
                case "dischargePowerW": value = desc.isAccumulator ? (object)(desc.outputEnergyPerTick * 60.0) : null; return true;
                default: return false;
            }
        }

        private static JsonObject CaptureObjectConnections(PlanetFactory factory, int entityId)
        {
            if (factory.entityPool == null || entityId >= factory.entityPool.Length ||
                factory.entityPool[entityId].id != entityId)
            {
                return null;
            }

            var entity = factory.entityPool[entityId];
            // 不初始化或打开工具：这些原生读取方法只依赖 factory，包含堆叠端口规则。
            var reader = new ConnectionReadTool { factory = factory };
            var connections = new List<object>();
            for (var slot = 0; slot < 16; slot++)
            {
                factory.ReadObjectConn(entityId, slot, out var isOutput, out var otherObjectId, out var otherSlot);
                connections.Add(new JsonObject
                {
                    ["slot"] = slot,
                    ["isOutput"] = otherObjectId == 0 ? null : (object)isOutput,
                    ["otherObjectId"] = otherObjectId,
                    ["otherSlot"] = otherObjectId == 0 ? null : (object)otherSlot
                });
            }

            return new JsonObject
            {
                ["entityId"] = entityId,
                ["protoId"] = entity.protoId,
                ["modelIndex"] = entity.modelIndex,
                ["isBelt"] = reader.ObjectIsBelt(entityId),
                ["objectPose"] = reader.GetObjectPose(entityId),
                ["objectPose2"] = reader.GetObjectPose2(entityId),
                ["tilt"] = reader.GetObjectTilt(entityId),
                ["beltPorts"] = IndexedConnectionPoses(reader.GetLocalPorts(entityId)),
                ["sorterSlots"] = IndexedConnectionPoses(reader.GetLocalSlots(entityId)),
                ["connections"] = connections
            };
        }

        private static List<object> IndexedConnectionPoses(UnityEngine.Pose[] poses)
        {
            if (poses == null)
            {
                return null;
            }

            var result = new List<object>(poses.Length);
            for (var index = 0; index < poses.Length; index++)
            {
                result.Add(new JsonObject { ["index"] = index, ["localPose"] = poses[index] });
            }

            return result;
        }

        private sealed class ConnectionReadTool : BuildTool
        {
        }
    }
}
