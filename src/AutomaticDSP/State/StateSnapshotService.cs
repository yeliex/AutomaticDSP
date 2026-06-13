using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using AutomaticDSP.Serialization;
using BepInEx.Logging;
using Newtonsoft.Json;
using UnityEngine;

namespace AutomaticDSP.State
{
    internal sealed class StateSnapshotService
    {
        private const int NearbyBuildingLimit = 200;
        private const float NearbyBuildingRadius = 160f;
        private const int NearbyBuildContextResourceLimit = 80;
        private const float NearbyBuildContextRadius = 160f;
        private static readonly int[] BuildContextBuildingItemIds =
        {
            2001, 2002, 2003,
            2011, 2012, 2013,
            2020,
            2101, 2102, 2106,
            2201, 2202, 2203, 2204, 2205, 2206, 2210, 2211,
            2301, 2302, 2303, 2304, 2305, 2306, 2307, 2308, 2309, 2313, 2314,
            2901
        };
        private readonly ManualLogSource log;
        private readonly int snapshotIntervalTicks;
        private readonly JsonSerializerSettings jsonSettings = new JsonSerializerSettings
        {
            NullValueHandling = NullValueHandling.Include
        };
        private readonly string dumpDirectory;
        private readonly string staleDiagnosticsDirectory;
        private readonly string snapshotDirectory;
        private long lastCaptureGameTick = -1;
        private long nextSnapshotId = 1;
        private bool inactiveLogged;
        private bool inactiveSnapshotFileChecked;
        private string inactiveReasonLogged;
        private bool latestGameLoaded;
        private JsonObject latestSessionGate;
        private StateSnapshot latestSnapshot;

        public StateSnapshotService(int snapshotIntervalTicks, string cacheRootPath, ManualLogSource log)
        {
            this.snapshotIntervalTicks = Math.Max(1, snapshotIntervalTicks);
            dumpDirectory = Path.Combine(cacheRootPath, "dumps");
            staleDiagnosticsDirectory = Path.Combine(cacheRootPath, "diagnostics");
            snapshotDirectory = Path.Combine(cacheRootPath, "snapshots");
            this.log = log;
            latestSessionGate = SessionGate("not_observed");
            ClearPersistedSnapshots();
        }

        public StateSnapshot GetLatestSnapshot()
        {
            return latestSnapshot;
        }

        public JsonObject GetHealth()
        {
            var snapshot = latestSnapshot;
            return new JsonObject
            {
                ["status"] = "ok",
                ["gameLoaded"] = latestGameLoaded,
                ["hasState"] = snapshot != null,
                ["latestSnapshotId"] = snapshot?.Id,
                ["latestGameTick"] = snapshot?.GameTick,
                ["snapshotIntervalTicks"] = snapshotIntervalTicks,
                ["sessionGate"] = latestSessionGate
            };
        }

        public void Update()
        {
            var unavailableReason = GetSessionUnavailableReason();
            latestSessionGate = CaptureSessionGate(unavailableReason);
            if (unavailableReason != null)
            {
                MarkSessionUnavailable(unavailableReason);
                return;
            }

            var gameTick = GameMain.gameTick;
            if (latestSnapshot != null && gameTick >= lastCaptureGameTick &&
                gameTick - lastCaptureGameTick < snapshotIntervalTicks)
            {
                return;
            }

            Capture(gameTick);
        }

        private void Capture(long gameTick)
        {
            var stopwatch = Stopwatch.StartNew();
            try
            {
                var data = new JsonObject();
                var metadata = CaptureMetadata(gameTick);
                data["metadata"] = metadata;
                data["game"] = CaptureGameState();
                var gameData = CaptureGameDataState();
                data["data"] = gameData;
                data["player"] = CapturePlayer();
                data["mecha"] = CaptureMecha();
                data["inventory"] = CaptureInventory();
                data["replicator"] = CaptureReplicator();
                data["research"] = CaptureResearch();
                data["currentPlanet"] = CaptureCurrentPlanet();
                data["factory"] = CaptureFactory();
                data["production"] = CaptureProduction();
                data["power"] = CapturePower();
                data["alerts"] = CaptureAlerts(data);
                data["buildContext"] = CaptureBuildContext(data);

                stopwatch.Stop();
                ((JsonObject)data["metadata"])["captureDurationMs"] = stopwatch.Elapsed.TotalMilliseconds;

                var snapshot = new StateSnapshot(nextSnapshotId++, gameTick, data);
                latestGameLoaded = Convert.ToBoolean(metadata["gameLoaded"]);
                latestSnapshot = snapshot;
                lastCaptureGameTick = gameTick;
                inactiveLogged = false;
                inactiveSnapshotFileChecked = false;
                WriteLatestSnapshot(snapshot);
                WriteGameDataDump(gameData);

                if (snapshot.Id == 1 || snapshot.Id % 60 == 0)
                {
                    log.LogInfo($"Captured state snapshot {snapshot.Id} at gameTick {gameTick}.");
                }
            }
            catch (Exception ex)
            {
                log.LogWarning($"State snapshot capture failed: {ex}");
            }
        }

        private void MarkSessionUnavailable(string reason)
        {
            latestGameLoaded = false;
            latestSnapshot = null;
            lastCaptureGameTick = -1;

            if (!inactiveSnapshotFileChecked)
            {
                ClearPersistedSnapshots();
                inactiveSnapshotFileChecked = true;
            }

            if (!inactiveLogged || inactiveReasonLogged != reason)
            {
                log.LogInfo($"AutomaticDSP state snapshot is waiting for a loaded game session: {reason}.");
                inactiveLogged = true;
                inactiveReasonLogged = reason;
            }
        }

        private void WriteLatestSnapshot(StateSnapshot snapshot)
        {
            try
            {
                Directory.CreateDirectory(snapshotDirectory);
                var snapshotPath = Path.Combine(snapshotDirectory, "latest.json");
                var tempPath = snapshotPath + ".tmp";
                var json = JsonConvert.SerializeObject(snapshot.Data, Formatting.Indented, jsonSettings);
                File.WriteAllText(tempPath, json, Encoding.UTF8);

                if (File.Exists(snapshotPath))
                {
                    File.Delete(snapshotPath);
                }

                File.Move(tempPath, snapshotPath);
            }
            catch (Exception ex)
            {
                log.LogWarning($"Failed to write latest state snapshot: {ex.Message}");
            }
        }

        private void ClearPersistedSnapshots()
        {
            DeleteLatestSnapshot();
            DeleteGameDataDump();
            DeleteStaleDiagnostics();
        }

        private void DeleteLatestSnapshot()
        {
            try
            {
                var snapshotPath = Path.Combine(snapshotDirectory, "latest.json");
                var tempPath = snapshotPath + ".tmp";
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }

                if (File.Exists(snapshotPath))
                {
                    File.Delete(snapshotPath);
                }
            }
            catch (Exception ex)
            {
                log.LogWarning($"Failed to delete stale state snapshot: {ex.Message}");
            }
        }

        private void WriteGameDataDump(JsonObject gameData)
        {
            try
            {
                Directory.CreateDirectory(dumpDirectory);
                var dumpPath = Path.Combine(dumpDirectory, "gameData.json");
                var tempPath = dumpPath + ".tmp";
                var json = JsonConvert.SerializeObject(gameData, Formatting.Indented, jsonSettings);
                File.WriteAllText(tempPath, json, Encoding.UTF8);

                if (File.Exists(dumpPath))
                {
                    File.Delete(dumpPath);
                }

                File.Move(tempPath, dumpPath);
            }
            catch (Exception ex)
            {
                log.LogWarning($"Failed to write GameData dump: {ex.Message}");
            }
        }

        private void DeleteGameDataDump()
        {
            try
            {
                var dumpPath = Path.Combine(dumpDirectory, "gameData.json");
                var tempPath = dumpPath + ".tmp";
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }

                if (File.Exists(dumpPath))
                {
                    File.Delete(dumpPath);
                }

                if (Directory.Exists(dumpDirectory) && Directory.GetFiles(dumpDirectory).Length == 0)
                {
                    Directory.Delete(dumpDirectory);
                }
            }
            catch (Exception ex)
            {
                log.LogWarning($"Failed to delete stale GameData dump: {ex.Message}");
            }
        }

        private void DeleteStaleDiagnostics()
        {
            try
            {
                var diagnosticsPath = Path.Combine(staleDiagnosticsDirectory, "gameMain.json");
                var tempPath = diagnosticsPath + ".tmp";
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }

                if (File.Exists(diagnosticsPath))
                {
                    File.Delete(diagnosticsPath);
                }

                if (Directory.Exists(staleDiagnosticsDirectory) && Directory.GetFiles(staleDiagnosticsDirectory).Length == 0)
                {
                    Directory.Delete(staleDiagnosticsDirectory);
                }
            }
            catch (Exception ex)
            {
                log.LogWarning($"Failed to delete stale diagnostics: {ex.Message}");
            }
        }

        private JsonObject CaptureMetadata(long gameTick)
        {
            return new JsonObject
            {
                ["snapshotId"] = nextSnapshotId,
                ["gameTick"] = gameTick,
                ["capturedAt"] = DateTimeOffset.UtcNow,
                ["captureDurationMs"] = 0,
                ["gameLoaded"] = IsGameLoaded(),
                ["currentPlanetId"] = GameMain.localPlanet?.id,
                ["currentStarId"] = GameMain.localStar?.id,
                ["schemaVersion"] = 1
            };
        }

        private JsonObject CaptureGameState()
        {
            var data = GameMain.data;
            var galaxy = GameMain.galaxy;

            return new JsonObject
            {
                ["available"] = true,
                ["name"] = GameMain.gameName,
                ["creationTime"] = GameMain.creationTime,
                ["gameTick"] = GameMain.gameTick,
                ["gameTime"] = GameMain.gameTime,
                ["onceGameTick"] = GameMain.onceGameTick,
                ["onceGameTime"] = GameMain.onceGameTime,
                ["sandboxToolsEnabled"] = GameMain.sandboxToolsEnabled,
                ["lifecycle"] = new JsonObject
                {
                    ["notNull"] = GameMain.notNull,
                    ["isRunning"] = GameMain.isRunning,
                    ["isLoading"] = GameMain.isLoading,
                    ["isPaused"] = GameMain.isPaused,
                    ["isFullscreenPaused"] = GameMain.isFullscreenPaused,
                    ["isEnded"] = GameMain.isEnded,
                    ["loadErrored"] = GameMain.loadErrored,
                    ["inOtherScene"] = GameMain.inOtherScene,
                    ["isMenuDemo"] = DSPGame.IsMenuDemo,
                    ["menuDemoLoaded"] = DSPGame.MenuDemoLoaded,
                    ["gameMainIsMenuDemo"] = IsGameMainMenuDemo()
                },
                ["location"] = new JsonObject
                {
                    ["localStarId"] = GameMain.localStar?.id,
                    ["localStarName"] = GameMain.localStar?.displayName,
                    ["localPlanetId"] = GameMain.localPlanet?.id,
                    ["localPlanetName"] = GameMain.localPlanet?.displayName,
                    ["playerOnPlanet"] = GameMain.localPlanet != null
                },
                ["galaxy"] = new JsonObject
                {
                    ["available"] = galaxy != null,
                    ["starCount"] = ReflectionReader.GetInt(galaxy, 0, "starCount")
                },
                ["data"] = new JsonObject
                {
                    ["available"] = data != null,
                    ["factoryCount"] = ReflectionReader.GetInt(data, 0, "factoryCount"),
                    ["dysonSphereCount"] = data?.dysonSpheres?.Length ?? 0,
                    ["guideRunning"] = ReflectionReader.GetBool(data, false, "guideRunning"),
                    ["guideComplete"] = ReflectionReader.GetBool(data, false, "guideComplete"),
                    ["isLandReady"] = ReflectionReader.GetBool(data, false, "isLandReady")
                },
                ["systems"] = new JsonObject
                {
                    ["hasMainPlayer"] = GameMain.mainPlayer != null,
                    ["hasHistory"] = GameMain.history != null,
                    ["hasStatistics"] = GameMain.statistics != null,
                    ["hasPreferences"] = GameMain.preferences != null,
                    ["hasSpaceSector"] = GameMain.spaceSector != null,
                    ["hasGalacticTransport"] = data?.galacticTransport != null,
                    ["hasWarningSystem"] = data?.warningSystem != null,
                    ["hasTrashSystem"] = data?.trashSystem != null
                }
            };
        }

        private JsonObject CaptureGameDataState()
        {
            var data = GameMain.data;
            if (data == null)
            {
                return Unavailable("game_data_missing");
            }

            var result = CaptureMembers(data, typeof(GameData), BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            result["available"] = true;
            return result;
        }

        private static JsonObject CaptureMembers(object target, Type type, BindingFlags flags)
        {
            var result = new JsonObject
            {
                ["type"] = type.FullName
            };
            var fields = new JsonObject();
            var properties = new JsonObject();

            foreach (var field in type.GetFields(flags))
            {
                fields[field.Name] = ReadMember(() => field.GetValue(target));
            }

            foreach (var property in type.GetProperties(flags))
            {
                if (property.GetIndexParameters().Length > 0)
                {
                    continue;
                }

                properties[property.Name] = ReadMember(() => property.GetValue(target, null));
            }

            result["fields"] = fields;
            result["properties"] = properties;
            return result;
        }

        private static object ReadMember(Func<object> read)
        {
            try
            {
                return ToStateValue(read());
            }
            catch (Exception ex)
            {
                return new JsonObject
                {
                    ["error"] = ex.GetType().Name,
                    ["message"] = ex.Message
                };
            }
        }

        private static object ToStateValue(object value)
        {
            if (value == null)
            {
                return null;
            }

            var type = value.GetType();
            if (type.IsPrimitive || value is string || value is decimal || value is DateTime || value is DateTimeOffset)
            {
                return value;
            }

            if (type.IsEnum)
            {
                return value.ToString();
            }

            if (value is Vector3 vector3)
            {
                return Vector(vector3);
            }

            if (value is VectorLF3 vectorLf3)
            {
                return Vector(vectorLf3);
            }

            if (value is Array array)
            {
                return ArraySummary(array);
            }

            if (value is ICollection collection)
            {
                return new JsonObject
                {
                    ["type"] = type.FullName,
                    ["count"] = collection.Count
                };
            }

            return ObjectSummary(value);
        }

        private static JsonObject ArraySummary(Array array)
        {
            var result = new JsonObject
            {
                ["type"] = array.GetType().FullName,
                ["elementType"] = array.GetType().GetElementType()?.FullName,
                ["length"] = array.Length,
                ["rank"] = array.Rank
            };

            var sample = new List<object>();
            if (array.Rank == 1)
            {
                for (var i = 0; i < array.Length && sample.Count < 16; i++)
                {
                    var value = array.GetValue(i);
                    sample.Add(value == null ? null : ToStateValue(value));
                }
            }

            result["sample"] = sample;
            return result;
        }

        private static JsonObject ObjectSummary(object value)
        {
            var type = value.GetType();
            var result = new JsonObject
            {
                ["type"] = type.FullName
            };

            AddSummaryMember(result, value, "id");
            AddSummaryMember(result, value, "index");
            AddSummaryMember(result, value, "name");
            AddSummaryMember(result, value, "displayName");
            AddSummaryMember(result, value, "planetId");
            AddSummaryMember(result, value, "starId");
            AddSummaryMember(result, value, "factoryIndex");
            AddSummaryMember(result, value, "count");
            AddSummaryMember(result, value, "length");
            AddSummaryMember(result, value, "cursor");
            AddSummaryMember(result, value, "entityCursor");
            AddSummaryMember(result, value, "factoryCount");
            AddSummaryMember(result, value, "starCount");
            return result;
        }

        private static void AddSummaryMember(JsonObject result, object target, string name)
        {
            var value = ReflectionReader.Get(target, name);
            if (value == null)
            {
                return;
            }

            var type = value.GetType();
            if (type.IsPrimitive || value is string || type.IsEnum)
            {
                result[name] = ToStateValue(value);
            }
        }

        private static JsonObject CaptureSessionGate(string reason)
        {
            try
            {
                return new JsonObject
                {
                    ["loaded"] = reason == null,
                    ["reason"] = reason,
                    ["gameMainData"] = GameMain.data != null,
                    ["gameMainNotNull"] = GameMain.notNull,
                    ["gameMainRunning"] = GameMain.isRunning,
                    ["gameMainLoading"] = GameMain.isLoading,
                    ["gameMainEnded"] = GameMain.isEnded,
                    ["gameMainLoadErrored"] = GameMain.loadErrored,
                    ["gameMainInOtherScene"] = GameMain.inOtherScene,
                    ["dspGameIsMenuDemo"] = DSPGame.IsMenuDemo,
                    ["gameMainIsMenuDemo"] = IsGameMainMenuDemo(),
                    ["mainPlayer"] = GameMain.mainPlayer != null,
                    ["history"] = GameMain.history != null,
                    ["statistics"] = GameMain.statistics != null,
                    ["galaxy"] = GameMain.galaxy != null,
                    ["gameTick"] = GameMain.gameTick,
                    ["gameTime"] = GameMain.gameTime,
                    ["gameName"] = GameMain.gameName
                };
            }
            catch (Exception ex)
            {
                return SessionGate("exception_" + ex.GetType().Name);
            }
        }

        private static JsonObject SessionGate(string reason)
        {
            return new JsonObject
            {
                ["loaded"] = false,
                ["reason"] = reason
            };
        }

        private JsonObject CapturePlayer()
        {
            var player = GameMain.mainPlayer;
            if (player == null)
            {
                return Unavailable("main_player_missing");
            }

            return new JsonObject
            {
                ["available"] = true,
                ["planetId"] = GameMain.localPlanet?.id,
                ["starId"] = GameMain.localStar?.id,
                ["position"] = Vector(player.position),
                ["uPosition"] = Vector(player.uPosition),
                ["rotation"] = Vector(player.transform.eulerAngles),
                ["isOnPlanet"] = GameMain.localPlanet != null,
                ["movementState"] = player.movementState.ToString(),
                ["buildRange"] = player.mecha?.buildArea,
                ["interactionRange"] = ReflectionReader.GetDouble(player.mecha, 0, "reactorArea", "actionArea", "interactArea")
            };
        }

        private JsonObject CaptureMecha()
        {
            var player = GameMain.mainPlayer;
            var mecha = player?.mecha;
            if (mecha == null)
            {
                return Unavailable("mecha_missing");
            }

            var construction = mecha.constructionModule;
            return new JsonObject
            {
                ["available"] = true,
                ["hp"] = mecha.hp,
                ["hpMax"] = mecha.hpMaxApplied,
                ["coreEnergy"] = mecha.coreEnergy,
                ["coreEnergyCap"] = mecha.coreEnergyCap,
                ["reactorEnergy"] = mecha.reactorEnergy,
                ["sandCount"] = player.sandCount,
                ["buildArea"] = mecha.buildArea,
                ["constructionDroneEnabled"] = construction.droneEnabled,
                ["constructionDroneCount"] = ReflectionReader.GetInt(construction, 0, "droneCount", "droneCursor"),
                ["idleConstructionDroneCount"] = ReflectionReader.GetInt(construction, 0, "idleDroneCount"),
                ["workingConstructionDroneCount"] = ReflectionReader.GetInt(construction, 0, "workDroneCount", "workingDroneCount")
            };
        }

        private JsonObject CaptureInventory()
        {
            var package = GameMain.mainPlayer?.package;
            if (package == null)
            {
                return Unavailable("inventory_missing");
            }

            var items = new List<object>();
            var byItem = new Dictionary<int, int>();
            var emptySlots = 0;

            for (var i = 0; i < package.size; i++)
            {
                var grid = package.grids[i];
                if (grid.itemId <= 0 || grid.count <= 0)
                {
                    emptySlots++;
                    continue;
                }

                items.Add(new JsonObject
                {
                    ["slot"] = i,
                    ["itemId"] = grid.itemId,
                    ["name"] = ItemName(grid.itemId),
                    ["count"] = grid.count,
                    ["stackSize"] = grid.stackSize,
                    ["inc"] = grid.inc
                });

                byItem.TryGetValue(grid.itemId, out var count);
                byItem[grid.itemId] = count + grid.count;
            }

            return new JsonObject
            {
                ["available"] = true,
                ["size"] = package.size,
                ["emptySlots"] = emptySlots,
                ["items"] = items,
                ["summary"] = ItemSummary(byItem)
            };
        }

        private JsonObject CaptureReplicator()
        {
            var forge = GameMain.mainPlayer?.mecha?.forge;
            if (forge == null)
            {
                return Unavailable("replicator_missing");
            }

            return new JsonObject
            {
                ["available"] = true,
                ["queue"] = new List<object>(),
                ["current"] = null,
                ["notes"] = new List<object>
                {
                    "Replicator queue shape is reserved for M2 field mapping."
                }
            };
        }

        private JsonObject CaptureResearch()
        {
            var history = GameMain.history;
            if (history == null)
            {
                return Unavailable("history_missing");
            }

            var queue = new List<object>();
            var rawQueue = ReflectionReader.Get(history, "techQueue") as IEnumerable;
            if (rawQueue != null)
            {
                foreach (var item in rawQueue)
                {
                    var techId = Convert.ToInt32(item);
                    if (techId > 0)
                    {
                        queue.Add(new JsonObject
                        {
                            ["techId"] = techId,
                            ["name"] = TechName(techId)
                        });
                    }
                }
            }

            var currentTech = ReflectionReader.GetInt(history, 0, "currentTech");
            return new JsonObject
            {
                ["available"] = true,
                ["currentTechId"] = currentTech,
                ["currentTechName"] = TechName(currentTech),
                ["queueLength"] = ReflectionReader.GetInt(history, queue.Count, "techQueueLength"),
                ["queue"] = queue,
                ["techHashedFor10Frames"] = GameMain.statistics?.techHashedFor10Frames ?? 0,
                ["isStalled"] = currentTech > 0 && (GameMain.statistics?.techHashedFor10Frames ?? 0) == 0
            };
        }

        private JsonObject CaptureCurrentPlanet()
        {
            var planet = GameMain.localPlanet;
            if (planet == null)
            {
                return Unavailable("current_planet_missing");
            }

            return new JsonObject
            {
                ["available"] = true,
                ["id"] = planet.id,
                ["name"] = planet.displayName,
                ["type"] = planet.typeString,
                ["radius"] = planet.radius,
                ["realRadius"] = planet.realRadius,
                ["starId"] = planet.star?.id,
                ["starName"] = planet.star?.displayName,
                ["windEnergyFactor"] = planet.windStrength,
                ["solarEnergyFactor"] = planet.luminosity,
                ["factoryIndex"] = planet.factoryIndex,
                ["resources"] = CaptureVeins(planet.factory)
            };
        }

        private JsonObject CaptureFactory()
        {
            var factory = GameMain.localPlanet?.factory;
            if (factory == null)
            {
                return Unavailable("factory_missing");
            }

            var playerPosition = GameMain.mainPlayer != null ? GameMain.mainPlayer.position : Vector3.zero;
            var buildings = new List<object>();
            var byProto = new Dictionary<int, int>();
            var missingPower = 0;

            for (var i = 1; i < factory.entityCursor && buildings.Count < NearbyBuildingLimit; i++)
            {
                var entity = factory.entityPool[i];
                if (entity.id != i)
                {
                    continue;
                }

                byProto.TryGetValue(entity.protoId, out var protoCount);
                byProto[entity.protoId] = protoCount + 1;

                if ((entity.pos - playerPosition).sqrMagnitude > NearbyBuildingRadius * NearbyBuildingRadius)
                {
                    continue;
                }

                if (entity.powerNodeId == 0)
                {
                    missingPower++;
                }

                buildings.Add(new JsonObject
                {
                    ["entityId"] = entity.id,
                    ["protoId"] = entity.protoId,
                    ["name"] = ItemName(entity.protoId),
                    ["modelIndex"] = entity.modelIndex,
                    ["position"] = Vector(entity.pos),
                    ["powerNodeId"] = entity.powerNodeId
                });
            }

            return new JsonObject
            {
                ["available"] = true,
                ["planetId"] = factory.planetId,
                ["entityCursor"] = factory.entityCursor,
                ["nearbyRadius"] = NearbyBuildingRadius,
                ["nearbyBuildings"] = buildings,
                ["buildingSummary"] = ItemSummary(byProto),
                ["missingPowerBuildingCountNearby"] = missingPower,
                ["beltCount"] = ReflectionReader.GetInt(factory.cargoTraffic, 0, "beltCursor"),
                ["sorterCount"] = ReflectionReader.GetInt(factory.cargoTraffic, 0, "sorterCursor")
            };
        }

        private JsonObject CaptureProduction()
        {
            var stat = CurrentFactoryStat();
            if (stat == null)
            {
                return Unavailable("production_stat_missing");
            }

            return new JsonObject
            {
                ["available"] = true,
                ["productRegister"] = NonZeroArray(ReflectionReader.Get(stat, "productRegister") as Array, 120),
                ["consumeRegister"] = NonZeroArray(ReflectionReader.Get(stat, "consumeRegister") as Array, 120),
                ["powerGenerationRegister"] = ReflectionReader.GetLong(stat, 0, "powerGenRegister"),
                ["powerConsumptionRegister"] = ReflectionReader.GetLong(stat, 0, "powerConRegister"),
                ["powerChargingRegister"] = ReflectionReader.GetLong(stat, 0, "powerChaRegister"),
                ["powerDischargingRegister"] = ReflectionReader.GetLong(stat, 0, "powerDisRegister")
            };
        }

        private JsonObject CapturePower()
        {
            var factory = GameMain.localPlanet?.factory;
            var stat = CurrentFactoryStat();
            if (factory == null)
            {
                return Unavailable("factory_missing");
            }

            long storedEnergy = 0;
            var powerSystem = factory.powerSystem;
            for (var i = 1; i < powerSystem.netCursor; i++)
            {
                storedEnergy += powerSystem.netPool[i].energyStored;
            }

            return new JsonObject
            {
                ["available"] = true,
                ["networkCount"] = powerSystem.netCursor,
                ["storedEnergy"] = storedEnergy,
                ["generationRegister"] = stat == null ? 0 : ReflectionReader.GetLong(stat, 0, "powerGenRegister"),
                ["consumptionRegister"] = stat == null ? 0 : ReflectionReader.GetLong(stat, 0, "powerConRegister"),
                ["chargingRegister"] = stat == null ? 0 : ReflectionReader.GetLong(stat, 0, "powerChaRegister"),
                ["dischargingRegister"] = stat == null ? 0 : ReflectionReader.GetLong(stat, 0, "powerDisRegister")
            };
        }

        private static JsonObject CaptureAlerts(JsonObject data)
        {
            var alerts = new List<object>();
            var power = data["power"] as JsonObject;
            var research = data["research"] as JsonObject;

            if (power != null && Convert.ToBoolean(power["available"]) &&
                Convert.ToInt64(power["generationRegister"]) < Convert.ToInt64(power["consumptionRegister"]))
            {
                alerts.Add(new JsonObject
                {
                    ["type"] = "power_shortage",
                    ["severity"] = "warning"
                });
            }

            if (research != null && Convert.ToBoolean(research["available"]) &&
                Convert.ToBoolean(research["isStalled"]))
            {
                alerts.Add(new JsonObject
                {
                    ["type"] = "research_stalled",
                    ["severity"] = "info"
                });
            }

            return new JsonObject
            {
                ["items"] = alerts
            };
        }

        private static JsonObject CaptureBuildContext(JsonObject data)
        {
            var player = GameMain.mainPlayer;
            var planet = GameMain.localPlanet;
            var factory = planet?.factory;
            var playerPosition = player != null ? player.position : Vector3.zero;
            var inventoryCounts = CaptureInventoryCounts();

            return new JsonObject
            {
                ["available"] = true,
                ["scope"] = new JsonObject
                {
                    ["planetId"] = planet?.id,
                    ["planetName"] = planet?.displayName,
                    ["factoryAvailable"] = factory != null,
                    ["playerOnPlanet"] = planet != null,
                    ["buildRange"] = player?.mecha?.buildArea,
                    ["nearbyRadius"] = NearbyBuildContextRadius
                },
                ["inventoryBuildings"] = CaptureBuildInventory(inventoryCounts),
                ["craftableBuildings"] = CaptureCraftableBuildings(inventoryCounts),
                ["basicProductionLine"] = CaptureBasicProductionLineNeeds(inventoryCounts),
                ["nearbyResources"] = factory == null ? Unavailable("factory_missing") : CaptureNearbyResources(factory, playerPosition),
                ["nearbyInfrastructure"] = factory == null ? Unavailable("factory_missing") : CaptureNearbyInfrastructure(factory, playerPosition),
                ["power"] = CapturePowerContext(data)
            };
        }

        private static Dictionary<int, int> CaptureInventoryCounts()
        {
            var result = new Dictionary<int, int>();
            var package = GameMain.mainPlayer?.package;
            if (package == null)
            {
                return result;
            }

            for (var i = 0; i < package.size; i++)
            {
                var grid = package.grids[i];
                if (grid.itemId <= 0 || grid.count <= 0)
                {
                    continue;
                }

                result.TryGetValue(grid.itemId, out var count);
                result[grid.itemId] = count + grid.count;
            }

            return result;
        }

        private static JsonObject CaptureBuildInventory(Dictionary<int, int> inventoryCounts)
        {
            var items = new List<object>();
            var categoryCounts = new Dictionary<string, int>();

            foreach (var itemId in BuildContextBuildingItemIds)
            {
                var proto = LDB.items.Select(itemId);
                if (proto == null)
                {
                    continue;
                }

                var count = InventoryCount(inventoryCounts, itemId);
                var category = BuildCategory(itemId);
                if (count > 0)
                {
                    categoryCounts.TryGetValue(category, out var categoryCount);
                    categoryCounts[category] = categoryCount + count;
                }

                items.Add(new JsonObject
                {
                    ["itemId"] = itemId,
                    ["name"] = proto.name,
                    ["category"] = category,
                    ["count"] = count,
                    ["stackSize"] = proto.StackSize,
                    ["canBuild"] = proto.CanBuild
                });
            }

            return new JsonObject
            {
                ["items"] = items,
                ["categories"] = CategorySummary(categoryCounts)
            };
        }

        private static JsonObject CaptureCraftableBuildings(Dictionary<int, int> inventoryCounts)
        {
            var items = new List<object>();
            foreach (var itemId in BuildContextBuildingItemIds)
            {
                var proto = LDB.items.Select(itemId);
                var recipe = proto?.handcraft;
                if (proto == null || recipe == null)
                {
                    continue;
                }

                items.Add(new JsonObject
                {
                    ["itemId"] = itemId,
                    ["name"] = proto.name,
                    ["category"] = BuildCategory(itemId),
                    ["handcraft"] = CaptureHandcraft(recipe, itemId, inventoryCounts)
                });
            }

            return new JsonObject
            {
                ["items"] = items
            };
        }

        private static JsonObject CaptureHandcraft(RecipeProto recipe, int resultItemId, Dictionary<int, int> inventoryCounts)
        {
            var missing = new List<object>();
            var ingredients = new List<object>();
            var canSatisfyMaterials = true;
            var itemIds = recipe.Items ?? new int[0];
            var itemCounts = recipe.ItemCounts ?? new int[0];

            for (var i = 0; i < itemIds.Length && i < itemCounts.Length; i++)
            {
                var itemId = itemIds[i];
                var required = itemCounts[i];
                var available = InventoryCount(inventoryCounts, itemId);
                var shortfall = Math.Max(0, required - available);
                if (shortfall > 0)
                {
                    canSatisfyMaterials = false;
                    missing.Add(new JsonObject
                    {
                        ["itemId"] = itemId,
                        ["name"] = ItemName(itemId),
                        ["count"] = shortfall
                    });
                }

                ingredients.Add(new JsonObject
                {
                    ["itemId"] = itemId,
                    ["name"] = ItemName(itemId),
                    ["required"] = required,
                    ["available"] = available,
                    ["missing"] = shortfall
                });
            }

            var unlocked = RecipeUnlocked(recipe.ID);
            return new JsonObject
            {
                ["available"] = true,
                ["recipeId"] = recipe.ID,
                ["recipeName"] = recipe.name,
                ["handcraft"] = recipe.Handcraft,
                ["unlocked"] = unlocked,
                ["craftableNow"] = recipe.Handcraft && unlocked && canSatisfyMaterials,
                ["resultCount"] = RecipeResultCount(recipe, resultItemId),
                ["timeSpend"] = recipe.TimeSpend,
                ["ingredients"] = ingredients,
                ["missingItems"] = missing
            };
        }

        private static JsonObject CaptureBasicProductionLineNeeds(Dictionary<int, int> inventoryCounts)
        {
            var requirements = new List<object>();
            var canStartFromInventory = true;
            AddRequirement(requirements, ref canStartFromInventory, inventoryCounts, 2301, 1);
            AddRequirement(requirements, ref canStartFromInventory, inventoryCounts, 2302, 1);
            AddRequirement(requirements, ref canStartFromInventory, inventoryCounts, 2201, 2);
            AddRequirement(requirements, ref canStartFromInventory, inventoryCounts, 2001, 12);
            AddRequirement(requirements, ref canStartFromInventory, inventoryCounts, 2011, 3);

            return new JsonObject
            {
                ["target"] = "iron_ingot_starter_line",
                ["canStartFromInventory"] = canStartFromInventory,
                ["requirements"] = requirements
            };
        }

        private static void AddRequirement(List<object> requirements, ref bool canStartFromInventory, Dictionary<int, int> inventoryCounts, int itemId, int required)
        {
            var available = InventoryCount(inventoryCounts, itemId);
            var missing = Math.Max(0, required - available);
            if (missing > 0)
            {
                canStartFromInventory = false;
            }

            requirements.Add(new JsonObject
            {
                ["itemId"] = itemId,
                ["name"] = ItemName(itemId),
                ["required"] = required,
                ["available"] = available,
                ["missing"] = missing
            });
        }

        private static JsonObject CaptureNearbyResources(PlanetFactory factory, Vector3 playerPosition)
        {
            var resources = new List<object>();
            var byType = new Dictionary<int, int>();
            var byTypeAmount = new Dictionary<int, long>();

            for (var i = 1; i < factory.veinCursor; i++)
            {
                var vein = factory.veinPool[i];
                if (vein.id != i)
                {
                    continue;
                }

                var distance = (vein.pos - playerPosition).magnitude;
                if (distance > NearbyBuildContextRadius)
                {
                    continue;
                }

                var type = (int)vein.type;
                byType.TryGetValue(type, out var count);
                byType[type] = count + 1;
                byTypeAmount.TryGetValue(type, out var amount);
                byTypeAmount[type] = amount + vein.amount;

                if (resources.Count < NearbyBuildContextResourceLimit)
                {
                    resources.Add(new JsonObject
                    {
                        ["id"] = vein.id,
                        ["type"] = vein.type.ToString(),
                        ["typeId"] = type,
                        ["amount"] = vein.amount,
                        ["distance"] = distance,
                        ["position"] = Vector(vein.pos)
                    });
                }
            }

            return new JsonObject
            {
                ["available"] = true,
                ["radius"] = NearbyBuildContextRadius,
                ["resources"] = resources,
                ["summary"] = ResourceSummary(byType, byTypeAmount)
            };
        }

        private static JsonObject CaptureNearbyInfrastructure(PlanetFactory factory, Vector3 playerPosition)
        {
            var categoryCounts = new Dictionary<string, int>();
            var entityCount = 0;
            var missingPowerCount = 0;

            for (var i = 1; i < factory.entityCursor; i++)
            {
                var entity = factory.entityPool[i];
                if (entity.id != i)
                {
                    continue;
                }

                if ((entity.pos - playerPosition).sqrMagnitude > NearbyBuildContextRadius * NearbyBuildContextRadius)
                {
                    continue;
                }

                entityCount++;
                var category = BuildCategory(entity.protoId);
                categoryCounts.TryGetValue(category, out var count);
                categoryCounts[category] = count + 1;

                if (entity.powerNodeId == 0)
                {
                    missingPowerCount++;
                }
            }

            return new JsonObject
            {
                ["available"] = true,
                ["radius"] = NearbyBuildContextRadius,
                ["entityCount"] = entityCount,
                ["missingPowerBuildingCount"] = missingPowerCount,
                ["categories"] = CategorySummary(categoryCounts)
            };
        }

        private static JsonObject CapturePowerContext(JsonObject data)
        {
            var power = data["power"] as JsonObject;
            if (power == null || !JsonBool(power, "available"))
            {
                return Unavailable("power_unavailable");
            }

            var generation = JsonLong(power, "generationRegister");
            var consumption = JsonLong(power, "consumptionRegister");
            return new JsonObject
            {
                ["available"] = true,
                ["networkCount"] = JsonLong(power, "networkCount"),
                ["storedEnergy"] = JsonLong(power, "storedEnergy"),
                ["generationRegister"] = generation,
                ["consumptionRegister"] = consumption,
                ["satisfactionRatio"] = consumption <= 0 ? 1.0 : Math.Min(1.0, (double)generation / consumption),
                ["hasShortage"] = consumption > generation
            };
        }

        private static JsonObject CaptureVeins(PlanetFactory factory)
        {
            if (factory == null)
            {
                return Unavailable("factory_missing");
            }

            var veins = new List<object>();
            var byType = new Dictionary<int, int>();
            var byTypeAmount = new Dictionary<int, long>();
            var totalVeinCount = 0;

            for (var i = 1; i < factory.veinCursor; i++)
            {
                var vein = factory.veinPool[i];
                if (vein.id != i)
                {
                    continue;
                }

                totalVeinCount++;
                var type = (int)vein.type;
                byType.TryGetValue(type, out var count);
                byType[type] = count + 1;
                byTypeAmount.TryGetValue(type, out var amount);
                byTypeAmount[type] = amount + vein.amount;

                if (veins.Count < 200)
                {
                    veins.Add(new JsonObject
                    {
                        ["id"] = vein.id,
                        ["type"] = vein.type.ToString(),
                        ["typeId"] = type,
                        ["amount"] = vein.amount,
                        ["position"] = Vector(vein.pos)
                    });
                }
            }

            var summary = new List<object>();
            foreach (var pair in byType)
            {
                summary.Add(new JsonObject
                {
                    ["typeId"] = pair.Key,
                    ["count"] = pair.Value,
                    ["amount"] = byTypeAmount[pair.Key]
                });
            }

            return new JsonObject
            {
                ["veinCount"] = totalVeinCount,
                ["veins"] = veins,
                ["summary"] = summary
            };
        }

        private static List<object> NonZeroArray(Array values, int limit)
        {
            var result = new List<object>();
            if (values == null)
            {
                return result;
            }

            for (var i = 0; i < values.Length && result.Count < limit; i++)
            {
                var value = Convert.ToInt64(values.GetValue(i));
                if (value == 0)
                {
                    continue;
                }

                result.Add(new JsonObject
                {
                    ["itemId"] = i,
                    ["name"] = ItemName(i),
                    ["value"] = value
                });
            }

            return result;
        }

        private static object CurrentFactoryStat()
        {
            var planet = GameMain.localPlanet;
            if (planet == null || GameMain.statistics?.production?.factoryStatPool == null)
            {
                return null;
            }

            var index = planet.factoryIndex;
            var pool = GameMain.statistics.production.factoryStatPool;
            return index >= 0 && index < pool.Length ? pool[index] : null;
        }

        private static bool IsGameLoaded()
        {
            return IsGameSessionLoaded();
        }

        private static bool IsGameSessionLoaded()
        {
            return GetSessionUnavailableReason() == null;
        }

        private static string GetSessionUnavailableReason()
        {
            try
            {
                if (GameMain.data == null)
                {
                    return "game_data_missing";
                }

                if (GameMain.isLoading)
                {
                    return "game_loading";
                }

                if (GameMain.isEnded)
                {
                    return "game_ended";
                }

                if (GameMain.loadErrored)
                {
                    return "game_load_errored";
                }

                if (GameMain.inOtherScene)
                {
                    return "game_in_other_scene";
                }

                if (DSPGame.IsMenuDemo || IsGameMainMenuDemo())
                {
                    return "menu_demo";
                }

                if (GameMain.mainPlayer == null)
                {
                    return "main_player_missing";
                }

                if (GameMain.history == null)
                {
                    return "history_missing";
                }

                if (GameMain.statistics == null)
                {
                    return "statistics_missing";
                }

                if (GameMain.galaxy == null)
                {
                    return "galaxy_missing";
                }

                return null;
            }
            catch (Exception ex)
            {
                return "exception_" + ex.GetType().Name;
            }
        }

        private static bool IsGameMainMenuDemo()
        {
            try
            {
                return ReflectionReader.GetBool(GameMain.instance, false, "isMenuDemo");
            }
            catch
            {
                return false;
            }
        }

        private static int InventoryCount(Dictionary<int, int> inventoryCounts, int itemId)
        {
            inventoryCounts.TryGetValue(itemId, out var count);
            return count;
        }

        private static bool RecipeUnlocked(int recipeId)
        {
            try
            {
                return recipeId > 0 && GameMain.history != null && GameMain.history.RecipeUnlocked(recipeId);
            }
            catch
            {
                return false;
            }
        }

        private static int RecipeResultCount(RecipeProto recipe, int resultItemId)
        {
            var results = recipe.Results ?? new int[0];
            var resultCounts = recipe.ResultCounts ?? new int[0];
            for (var i = 0; i < results.Length && i < resultCounts.Length; i++)
            {
                if (results[i] == resultItemId)
                {
                    return resultCounts[i];
                }
            }

            return 0;
        }

        private static string BuildCategory(int itemId)
        {
            if (itemId >= 2001 && itemId <= 2003)
            {
                return "belt";
            }

            if (itemId >= 2011 && itemId <= 2013)
            {
                return "sorter";
            }

            if (itemId == 2020)
            {
                return "splitter";
            }

            if (itemId == 2101 || itemId == 2102 || itemId == 2106)
            {
                return "storage";
            }

            if (itemId >= 2201 && itemId <= 2211)
            {
                return "power";
            }

            if (itemId == 2301)
            {
                return "miner";
            }

            if (itemId == 2302)
            {
                return "smelter";
            }

            if (itemId >= 2303 && itemId <= 2305)
            {
                return "assembler";
            }

            if (itemId == 2306 || itemId == 2307)
            {
                return "resource_collector";
            }

            if (itemId == 2308 || itemId == 2309 || itemId == 2314)
            {
                return "fluid_production";
            }

            if (itemId == 2313)
            {
                return "spray_coater";
            }

            if (itemId == 2901)
            {
                return "lab";
            }

            return "other";
        }

        private static List<object> CategorySummary(Dictionary<string, int> categoryCounts)
        {
            var result = new List<object>();
            foreach (var pair in categoryCounts)
            {
                result.Add(new JsonObject
                {
                    ["category"] = pair.Key,
                    ["count"] = pair.Value
                });
            }

            return result;
        }

        private static List<object> ResourceSummary(Dictionary<int, int> byType, Dictionary<int, long> byTypeAmount)
        {
            var result = new List<object>();
            foreach (var pair in byType)
            {
                result.Add(new JsonObject
                {
                    ["typeId"] = pair.Key,
                    ["count"] = pair.Value,
                    ["amount"] = byTypeAmount[pair.Key]
                });
            }

            return result;
        }

        private static bool JsonBool(JsonObject data, string key)
        {
            return data.ContainsKey(key) && Convert.ToBoolean(data[key]);
        }

        private static long JsonLong(JsonObject data, string key)
        {
            return data.ContainsKey(key) && data[key] != null ? Convert.ToInt64(data[key]) : 0;
        }

        private static JsonObject ItemSummary(Dictionary<int, int> items)
        {
            var result = new List<object>();
            foreach (var pair in items)
            {
                result.Add(new JsonObject
                {
                    ["itemId"] = pair.Key,
                    ["name"] = ItemName(pair.Key),
                    ["count"] = pair.Value
                });
            }

            return new JsonObject
            {
                ["items"] = result
            };
        }

        private static string ItemName(int itemId)
        {
            if (itemId <= 0)
            {
                return null;
            }

            try
            {
                return LDB.items.Select(itemId)?.name;
            }
            catch
            {
                return null;
            }
        }

        private static string TechName(int techId)
        {
            if (techId <= 0)
            {
                return null;
            }

            try
            {
                return LDB.techs.Select(techId)?.name;
            }
            catch
            {
                return null;
            }
        }

        private static JsonObject Unavailable(string reason)
        {
            return new JsonObject
            {
                ["available"] = false,
                ["reason"] = reason
            };
        }

        private static JsonObject Vector(Vector3 value)
        {
            return new JsonObject
            {
                ["x"] = value.x,
                ["y"] = value.y,
                ["z"] = value.z
            };
        }

        private static JsonObject Vector(VectorLF3 value)
        {
            return new JsonObject
            {
                ["x"] = value.x,
                ["y"] = value.y,
                ["z"] = value.z
            };
        }
    }
}
