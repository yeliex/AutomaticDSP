using System;
using System.IO;
using System.IO.Compression;
using AutomaticDSP.Serialization;
using Newtonsoft.Json.Linq;
using static AutomaticDSP.Tasks.TaskStatusNames;

namespace AutomaticDSP.Tasks
{
    internal sealed partial class TaskCommandExecutor
    {
        private void ExecuteBlueprint(CommandState command, DateTimeOffset now)
        {
            var export = command.NormalizedType == "exportdysonblueprint";
            var apply = command.NormalizedType == "applydysonblueprint";
            var code = "";
            var blueprint = new DysonBlueprintData();
            if (!export)
            {
                if (!TryGetToken(command, "blueprint", out var codeToken) || codeToken.Type != JTokenType.String ||
                    string.IsNullOrWhiteSpace(code = codeToken.Value<string>()) || code.Length > 16 * 1024 * 1024)
                {
                    finishCommand(command, CommandFailed, "invalid_command", "blueprint 必须为不超过 16 MiB 字符的原生蓝图字符串。", now, null);
                    return;
                }
                code = code.Trim();
                if (!CheckBlueprintCompression(code, out var compressionError))
                {
                    finishCommand(command, CommandFailed, "invalid_blueprint", compressionError, now, null);
                    return;
                }
                if (code.StartsWith("BLUEPRINT:", StringComparison.Ordinal))
                {
                    if (apply)
                    {
                        finishCommand(command, CommandFailed, "blueprint_type_mismatch", "工厂蓝图不能作为戴森设计蓝图应用。", now, null);
                        return;
                    }
                    var factoryBlueprint = new BlueprintData();
                    var factoryError = factoryBlueprint.FromBase64String(code);
                    finishCommand(command, factoryError == BlueprintDataIOError.OK ? CommandSucceeded : CommandFailed,
                        factoryError == BlueprintDataIOError.OK ? null : "invalid_blueprint", null, now,
                        new JsonObject { ["blueprintType"] = "factory", ["nativeCondition"] = factoryError.ToString(),
                            ["validationScope"] = "nativeParse", ["placementValidated"] = false,
                            ["buildingCount"] = factoryBlueprint.buildings?.Length ?? 0,
                            ["gameVersion"] = factoryBlueprint.gameVersion });
                    return;
                }
                var error = blueprint.HeaderFromBase64String(code);
                if (error == DysonBlueprintDataIOError.OK) error = blueprint.CheckSignature(code);
                if (error != DysonBlueprintDataIOError.OK)
                {
                    finishCommand(command, CommandFailed, "invalid_blueprint", "原生蓝图头或校验签名无效。", now,
                        new JsonObject { ["nativeCondition"] = error.ToString() });
                    return;
                }
            }
            if (!TryGetToken(command, "blueprintType", out var typeToken) || typeToken.Type != JTokenType.String ||
                !TryDysonBlueprintType(typeToken.Value<string>(), out var type))
            {
                finishCommand(command, CommandFailed, "invalid_command", "戴森蓝图需要显式 blueprintType：sphere、layers、layer 或 swarm。", now, null);
                return;
            }
            if (!export && blueprint.CheckType(type) != DysonBlueprintDataIOError.OK)
            {
                finishCommand(command, CommandFailed, "blueprint_type_mismatch", "原生蓝图类型与请求不匹配。", now,
                    new JsonObject { ["nativeCondition"] = blueprint.CheckType(type).ToString() });
                return;
            }
            if (GameMain.history == null || !GameMain.history.dysonSphereSystemUnlocked ||
                (type != EDysonBlueprintType.SwarmOrbits && !GameMain.history.dysonSphereLayerPanelUnlocked))
            {
                finishCommand(command, CommandFailed, "tech_locked", "对应戴森编辑能力尚未解锁。", now, null);
                return;
            }
            if (!export && blueprint.CheckLatLimit() != DysonBlueprintDataIOError.OK)
            {
                finishCommand(command, CommandFailed, "native_validation_failed", "蓝图超过已解锁的应力纬度。", now,
                    new JsonObject { ["nativeCondition"] = blueprint.CheckLatLimit().ToString(), ["latitudeLimit"] = blueprint.latLimit });
                return;
            }
            if (!apply && !export)
            {
                finishCommand(command, CommandSucceeded, null, null, now, new JsonObject
                {
                    ["blueprintType"] = typeToken.Value<string>(), ["nativeCondition"] = "OK", ["gameVersion"] = blueprint.gameVersion,
                    ["latitudeLimit"] = blueprint.latLimit, ["validationScope"] = "headerSignatureLatitudeAndCompression",
                    ["placementValidated"] = false
                });
                return;
            }
            if (!TryDysonId(command, "starId", out var starId) || GameMain.galaxy?.StarById(starId) == null)
            {
                finishCommand(command, CommandFailed, "invalid_command", "需要有效整数 starId。", now, null);
                return;
            }
            var layerId = 0;
            if (type == EDysonBlueprintType.SingleLayer && (!TryDysonId(command, "layerId", out layerId) || layerId > 10))
            {
                finishCommand(command, CommandFailed, "invalid_command", "单层蓝图需要 layerId（1–10）。", now, null);
                return;
            }
            var sphere = GameMain.data.CreateDysonSphere(GameMain.galaxy.StarById(starId).index);
            var layer = layerId == 0 ? null : sphere.layersIdBased[layerId];
            if (layerId != 0 && layer == null)
            {
                finishCommand(command, CommandFailed, "target_not_found", "单层蓝图的目标层不存在。", now, null);
                return;
            }
            if (export)
            {
                // 原生方法的布尔返回值为字符串是否为空，使用实际生成数据与原生有效性判断。
                blueprint.GenerateBlueprintCode(type, sphere, layer);
                var valid = blueprint.isValid(type) && !string.IsNullOrEmpty(blueprint.dataString);
                finishCommand(command, valid ? CommandSucceeded : CommandFailed, valid ? null : "native_export_failed", null, now,
                    new JsonObject { ["blueprintType"] = typeToken.Value<string>(), ["blueprint"] = blueprint.dataString });
                return;
            }
            // 原生粘贴按钮拒绝覆盖已有节点；直接调用 FromBase64String 不会自行执行该前置检查。
            if ((type == EDysonBlueprintType.SingleLayer && layer.nodeCount > 0) ||
                ((type == EDysonBlueprintType.DysonSphere || type == EDysonBlueprintType.Layers) && sphere.totalNodeCount > 0))
            {
                finishCommand(command, CommandFailed, "target_not_empty", "原生蓝图只能粘贴到没有节点的目标。", now, null);
                return;
            }
            // 无节点的层仍可能有网格画布，原生 UI 会提示此覆盖；接口明确反馈同一副作用。
            var overwritesGridCanvas = type == EDysonBlueprintType.SingleLayer ? layer.cellColors != null : false;
            if (type == EDysonBlueprintType.DysonSphere || type == EDysonBlueprintType.Layers)
                foreach (var existingLayer in sphere.layersIdBased)
                    overwritesGridCanvas |= existingLayer?.cellColors != null;
            var result = blueprint.FromBase64String(code, type, sphere, layer);
            finishCommand(command, result == DysonBlueprintDataIOError.OK ? CommandSucceeded : CommandFailed,
                result == DysonBlueprintDataIOError.OK ? null : "native_import_failed", null, now, new JsonObject
                {
                    ["starId"] = starId, ["layerId"] = layerId, ["blueprintType"] = typeToken.Value<string>(),
                    ["nativeCondition"] = result.ToString(), ["completion"] = "design", ["atomic"] = false,
                    ["stateMayHaveChanged"] = true, ["nodeCount"] = sphere.totalNodeCount,
                    ["overwritesGridCanvas"] = overwritesGridCanvas
                });
        }

        private static bool TryDysonBlueprintType(string value, out EDysonBlueprintType type)
        {
            switch (value)
            {
                case "sphere": type = EDysonBlueprintType.DysonSphere; return true;
                case "layers": type = EDysonBlueprintType.Layers; return true;
                case "layer": type = EDysonBlueprintType.SingleLayer; return true;
                case "swarm": type = EDysonBlueprintType.SwarmOrbits; return true;
                default: type = EDysonBlueprintType.None; return false;
            }
        }

        private static bool CheckBlueprintCompression(string code, out string error)
        {
            error = null;
            try
            {
                var begin = code.IndexOf('"');
                var end = code.LastIndexOf('"');
                if (begin < 0 || end <= begin) throw new FormatException();
                using (var input = new MemoryStream(Convert.FromBase64String(code.Substring(begin + 1, end - begin - 1))))
                using (var gzip = new GZipStream(input, CompressionMode.Decompress))
                {
                    var buffer = new byte[8192];
                    long size = 0;
                    int read;
                    while ((read = gzip.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        size += read;
                        if (size > 64 * 1024 * 1024) throw new InvalidDataException();
                    }
                    if (size < 4) throw new InvalidDataException();
                }
                return true;
            }
            catch (Exception ex) when (ex is FormatException || ex is InvalidDataException || ex is IOException)
            {
                error = "蓝图压缩数据无效或解压后超过 64 MiB。";
                return false;
            }
        }
    }
}
