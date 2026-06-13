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
        private readonly string staleDumpDirectory;
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
            staleDumpDirectory = Path.Combine(cacheRootPath, "dumps");
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
                data["player"] = CapturePlayer();
                data["mecha"] = CaptureMecha();
                data["inventory"] = CaptureInventory();
                data["replicator"] = CaptureReplicator();
                data["research"] = CaptureResearch();
                data["currentPlanet"] = CaptureCurrentPlanet();
                data["factory"] = CaptureFactory();
                data["preferences"] = CapturePreferences();
                data["statistics"] = CaptureStatistics();
                data["spaceSector"] = CaptureSpaceSector();
                data["galaxy"] = CaptureGalaxy();
                data["dysonSpheres"] = CaptureDysonSpheres();
                data["history"] = CaptureHistory();
                data["galacticTransport"] = CaptureGalacticTransport();
                data["warningSystem"] = CaptureWarningSystem();
                data["trashSystem"] = CaptureTrashSystem();
                data["goalSystem"] = CaptureGoalSystem();
                data["milestoneSystem"] = CaptureMilestoneSystem();
                data["gameAchievement"] = CaptureGameAchievement();
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
            DeleteStaleGameDataDump();
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

        private void DeleteStaleGameDataDump()
        {
            try
            {
                var dumpPath = Path.Combine(staleDumpDirectory, "gameData.json");
                var tempPath = dumpPath + ".tmp";
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }

                if (File.Exists(dumpPath))
                {
                    File.Delete(dumpPath);
                }

                if (Directory.Exists(staleDumpDirectory) && Directory.GetFiles(staleDumpDirectory).Length == 0)
                {
                    Directory.Delete(staleDumpDirectory);
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
                ["desc"] = CaptureGameDesc(),
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

        private static JsonObject CaptureGameDesc()
        {
            var desc = GetGameDataMember("gameDesc");
            if (desc == null)
            {
                return Unavailable("game_desc_missing");
            }

            return new JsonObject
            {
                ["available"] = true,
                ["creationTime"] = MemberValue(desc, "creationTime"),
                ["creationVersion"] = MemberValue(desc, "creationVersion")?.ToString(),
                ["galaxyAlgo"] = MemberInt(desc, 0, "galaxyAlgo"),
                ["galaxySeed"] = MemberInt(desc, 0, "galaxySeed"),
                ["starCount"] = MemberInt(desc, 0, "starCount"),
                ["clusterString"] = MemberString(desc, "clusterString"),
                ["clusterStringLong"] = MemberString(desc, "clusterStringLong"),
                ["seedKey64"] = MemberLong(desc, 0, "seedKey64"),
                ["playerProto"] = MemberInt(desc, 0, "playerProto"),
                ["resourceMultiplier"] = MemberDouble(desc, 0, "resourceMultiplier"),
                ["oilAmountMultiplier"] = MemberDouble(desc, 0, "oilAmountMultiplier"),
                ["isInfiniteResource"] = MemberBool(desc, false, "isInfiniteResource"),
                ["isRareResource"] = MemberBool(desc, false, "isRareResource"),
                ["achievementEnable"] = MemberBool(desc, false, "achievementEnable"),
                ["isPeaceMode"] = MemberBool(desc, false, "isPeaceMode"),
                ["isSandboxMode"] = MemberBool(desc, false, "isSandboxMode"),
                ["isCombatMode"] = MemberBool(desc, false, "isCombatMode"),
                ["goalLevel"] = MemberValue(desc, "goalLevel")?.ToString(),
                ["hasCombatSettings"] = MemberValue(desc, "combatSettings") != null,
                ["enemyDropMultiplier"] = MemberDouble(desc, 0, "enemyDropMultiplier"),
                ["propertyMultiplier"] = MemberDouble(desc, 0, "propertyMultiplier"),
                ["combatModeDifficultyNumber"] = MemberInt(desc, 0, "combatModeDifficultyNumber"),
                ["savedThemeIds"] = IntArray(MemberValue(desc, "savedThemeIds") as Array, 128, true)
            };
        }

        private static JsonObject CapturePreferences()
        {
            var prefs = (object)GameMain.preferences ?? GetGameDataMember("preferences");
            if (prefs == null)
            {
                return Unavailable("preferences_missing");
            }

            return new JsonObject
            {
                ["available"] = true,
                ["camera"] = new JsonObject
                {
                    ["uPosition"] = VectorOrNull(MemberValue(prefs, "cameraUPos")),
                    ["uRotation"] = QuaternionOrNull(MemberValue(prefs, "cameraURot"))
                },
                ["reformBrush"] = new JsonObject
                {
                    ["size"] = MemberInt(prefs, 0, "reformBrushSize"),
                    ["type"] = MemberInt(prefs, 0, "reformBrushType"),
                    ["decalType"] = MemberInt(prefs, 0, "reformBrushDecalType"),
                    ["color"] = MemberInt(prefs, 0, "reformBrushColor"),
                    ["buryVeins"] = MemberBool(prefs, false, "reformBuryVeins")
                },
                ["details"] = new JsonObject
                {
                    ["power"] = MemberBool(prefs, false, "detailPower"),
                    ["vein"] = MemberBool(prefs, false, "detailVein"),
                    ["spaceGuide"] = MemberBool(prefs, false, "detailSpaceGuide"),
                    ["defense"] = MemberBool(prefs, false, "detailDefense"),
                    ["sign"] = MemberBool(prefs, false, "detailSign"),
                    ["icon"] = MemberBool(prefs, false, "detailIcon"),
                    ["light"] = MemberBool(prefs, false, "detailLight"),
                    ["hpBar"] = MemberBool(prefs, false, "detailHpBar")
                },
                ["upgrade"] = new JsonObject
                {
                    ["level"] = MemberInt(prefs, 0, "upgradeLevel"),
                    ["cursorType"] = MemberInt(prefs, 0, "upgradeCursorType"),
                    ["cursorSize"] = MemberInt(prefs, 0, "upgradeCursorSize"),
                    ["filterFacility"] = MemberBool(prefs, false, "upgradeFilterFacility"),
                    ["filterBelt"] = MemberBool(prefs, false, "upgradeFilterBelt"),
                    ["filterInserter"] = MemberBool(prefs, false, "upgradeFilterInserter")
                },
                ["dismantle"] = new JsonObject
                {
                    ["cursorType"] = MemberInt(prefs, 0, "dismantleCursorType"),
                    ["cursorSize"] = MemberInt(prefs, 0, "dismantleCursorSize"),
                    ["filterFacility"] = MemberBool(prefs, false, "dismantleFilterFacility"),
                    ["filterBelt"] = MemberBool(prefs, false, "dismantleFilterBelt"),
                    ["filterInserter"] = MemberBool(prefs, false, "dismantleFilterInserter"),
                    ["instantDismantle"] = MemberBool(prefs, false, "instantDismantle")
                },
                ["blueprint"] = new JsonObject
                {
                    ["selectReform"] = MemberBool(prefs, false, "blueprintSelectReform"),
                    ["selectUndecalReforms"] = MemberBool(prefs, false, "blueprintSelectUndecalReforms"),
                    ["reformPartialPaste"] = MemberBool(prefs, false, "blueprintReformPartialPaste"),
                    ["reformFillOnly"] = MemberBool(prefs, false, "blueprintReformFillOnly"),
                    ["saveReform"] = MemberBool(prefs, false, "blueprintSaveReform"),
                    ["quickPaste"] = MemberBool(prefs, false, "blueprintQuickPaste"),
                    ["useReforms"] = MemberBool(prefs, false, "blueprintUseReforms"),
                    ["usePalette"] = MemberBool(prefs, false, "blueprintUsePalette"),
                    ["autoBuryBase"] = MemberBool(prefs, false, "blueprintAutoBuryBase")
                },
                ["build"] = new JsonObject
                {
                    ["fastBuildBatchSize"] = MemberInt(prefs, 0, "fastBuildBatchSize")
                },
                ["signalPicker"] = new JsonObject
                {
                    ["autoClose"] = MemberBool(prefs, false, "signalPickerAutoClose"),
                    ["showUnlock"] = MemberBool(prefs, false, "signalPickerShowUnlock")
                },
                ["techTree"] = new JsonObject
                {
                    ["showProperty"] = MemberBool(prefs, false, "techTreeShowProperty")
                },
                ["dysonSphereEditor"] = new JsonObject
                {
                    ["hideFarSide"] = MemberBool(prefs, false, "dysonSphereHideFarSideInEditor"),
                    ["hideRocketBodies"] = MemberBool(prefs, false, "dysonSphereHideRocketBodies")
                },
                ["sandbox"] = new JsonObject
                {
                    ["isDirectlyObtain"] = MemberBool(prefs, false, "sandboxIsDirectlyObtain"),
                    ["directlyObtainIsStack"] = MemberBool(prefs, false, "sandboxDirectlyObtainIsStack")
                },
                ["combat"] = new JsonObject
                {
                    ["turretBurstMode"] = MemberInt(prefs, 0, "turretBurstMode"),
                    ["dfMonitorDisplayMode"] = MemberInt(prefs, 0, "dfMonitorDisplayMode"),
                    ["trackedEnemyClusterCount"] = CountOf(MemberValue(prefs, "trackedEnemyClusters")),
                    ["enemyDropBanCount"] = CountOf(MemberValue(prefs, "enemyDropBans"))
                },
                ["ui"] = new JsonObject
                {
                    ["goalPanelState"] = MemberValue(prefs, "uiGoalPanelState")?.ToString(),
                    ["controlPanelInterstellarPairing"] = MemberInt(prefs, 0, "uiControlPanelInterstellarPairing"),
                    ["controlPanelIntraplanetaryPairing"] = MemberInt(prefs, 0, "uiControlPanelIntraplanetaryPairing"),
                    ["controlPanelIntraplanetaryPairingPlanetId"] = MemberInt(prefs, 0, "uiControlPanelIntraplanetaryPairingPlanetId"),
                    ["controlPanelDispenserPairing"] = MemberInt(prefs, 0, "uiControlPanelDispenserPairing"),
                    ["controlPanelDispenserPairingPlanetId"] = MemberInt(prefs, 0, "uiControlPanelDispenserPairingPlanetId"),
                    ["controlPanelFilter"] = MemberInt(prefs, 0, "uiControlPanelFilter"),
                    ["autoOpenDashboardOnAddStatPlan"] = MemberBool(prefs, false, "autoOpenDashboardOnAddStatPlan")
                },
                ["collections"] = new JsonObject
                {
                    ["replicatorMultiplierCount"] = CountOf(MemberValue(prefs, "replicatorMultipliers")),
                    ["tutorialShowingCount"] = CountOf(MemberValue(prefs, "tutorialShowing")),
                    ["astroNameOverrideCount"] = CountOf(MemberValue(prefs, "astroNameOverride")),
                    ["pickFilterCount"] = CountOf(MemberValue(prefs, "pickFilters")),
                    ["colorPanelRecentColorCount"] = CountOf(MemberValue(prefs, "colorPanelRecentColors")),
                    ["uiControlPanelAstroExpandCount"] = CountOf(MemberValue(prefs, "uiControlPanelAstroExpands"))
                }
            };
        }

        private static JsonObject CaptureStatistics()
        {
            var statistics = (object)GameMain.statistics ?? GetGameDataMember("statistics");
            if (statistics == null)
            {
                return Unavailable("statistics_missing");
            }

            return new JsonObject
            {
                ["available"] = true,
                ["tech"] = new JsonObject
                {
                    ["hashedThisFrame"] = MemberInt(statistics, 0, "techHashedThisFrame"),
                    ["hashedFor10Frames"] = MemberInt(statistics, 0, "techHashedFor10Frames"),
                    ["hashedRecorded"] = MemberInt(statistics, 0, "techHashedRecorded"),
                    ["recentHashes"] = IntArray(MemberValue(statistics, "techHashedHistory") as Array, 60, true)
                },
                ["systems"] = new JsonObject
                {
                    ["production"] = MemberValue(statistics, "production") != null,
                    ["kill"] = MemberValue(statistics, "kill") != null,
                    ["traffic"] = MemberValue(statistics, "traffic") != null,
                    ["charts"] = MemberValue(statistics, "charts") != null
                }
            };
        }

        private static JsonObject CaptureHistory()
        {
            var history = (object)GameMain.history ?? GetGameDataMember("history");
            if (history == null)
            {
                return Unavailable("history_missing");
            }

            return new JsonObject
            {
                ["available"] = true,
                ["currentTechId"] = MemberInt(history, 0, "currentTech"),
                ["currentTechName"] = TechName(MemberInt(history, 0, "currentTech")),
                ["techQueueLength"] = MemberInt(history, 0, "techQueueLength"),
                ["techQueue"] = TechQueue(MemberValue(history, "techQueue") as IEnumerable),
                ["counts"] = new JsonObject
                {
                    ["recipeUnlocked"] = CountOf(MemberValue(history, "recipeUnlocked")),
                    ["enemyDropItemUnlocked"] = CountOf(MemberValue(history, "enemyDropItemUnlocked")),
                    ["tutorialUnlocked"] = CountOf(MemberValue(history, "tutorialUnlocked")),
                    ["techStates"] = CountOf(MemberValue(history, "techStates")),
                    ["features"] = CountOf(MemberValue(history, "featureKeys")),
                    ["featureValues"] = CountOf(MemberValue(history, "featureValues")),
                    ["pinnedPlanets"] = CountOf(MemberValue(history, "pinnedPlanets"))
                },
                ["unlocks"] = new JsonObject
                {
                    ["logisticsSystem"] = MemberBool(history, false, "logisticsSystemUnlocked"),
                    ["interstellarStation"] = MemberBool(history, false, "interstellarStationUnlocked"),
                    ["intraplanetaryStation"] = MemberBool(history, false, "intraplanetaryStationUnlocked"),
                    ["dispenser"] = MemberBool(history, false, "dispenserUnlocked"),
                    ["marker"] = MemberBool(history, false, "markerUnlocked"),
                    ["foundation"] = MemberBool(history, false, "foundationUnlocked"),
                    ["dysonSphereSystem"] = MemberBool(history, false, "dysonSphereSystemUnlocked"),
                    ["ultraPhoton"] = MemberBool(history, false, "ultraPhotonUnlocked"),
                    ["dysonSphereLayerPanel"] = MemberBool(history, false, "dysonSphereLayerPanelUnlocked"),
                    ["verticalConstruct"] = MemberBool(history, false, "verticalConstructUnlocked"),
                    ["combatDrone"] = MemberBool(history, false, "combatDroneUnlocked"),
                    ["combatShip"] = MemberBool(history, false, "combatShipUnlocked")
                },
                ["upgrades"] = new JsonObject
                {
                    ["constructionDroneSpeed"] = MemberDouble(history, 0, "constructionDroneSpeed"),
                    ["constructionDroneMovement"] = MemberInt(history, 0, "constructionDroneMovement"),
                    ["autoReconstructSpeed"] = MemberInt(history, 0, "autoReconstructSpeed"),
                    ["logisticDroneSpeed"] = MemberDouble(history, 0, "logisticDroneSpeed"),
                    ["logisticDroneSpeedModified"] = MemberDouble(history, 0, "logisticDroneSpeedModified"),
                    ["logisticDroneCarries"] = MemberInt(history, 0, "logisticDroneCarries"),
                    ["logisticShipSailSpeed"] = MemberDouble(history, 0, "logisticShipSailSpeed"),
                    ["logisticShipSailSpeedModified"] = MemberDouble(history, 0, "logisticShipSailSpeedModified"),
                    ["logisticShipWarpSpeed"] = MemberDouble(history, 0, "logisticShipWarpSpeed"),
                    ["logisticShipWarpSpeedModified"] = MemberDouble(history, 0, "logisticShipWarpSpeedModified"),
                    ["logisticShipWarpDrive"] = MemberBool(history, false, "logisticShipWarpDrive"),
                    ["logisticShipCarries"] = MemberInt(history, 0, "logisticShipCarries"),
                    ["logisticCourierSpeed"] = MemberDouble(history, 0, "logisticCourierSpeed"),
                    ["logisticCourierSpeedModified"] = MemberDouble(history, 0, "logisticCourierSpeedModified"),
                    ["logisticCourierCarries"] = MemberInt(history, 0, "logisticCourierCarries"),
                    ["miningCostRate"] = MemberDouble(history, 0, "miningCostRate"),
                    ["miningSpeedScale"] = MemberDouble(history, 0, "miningSpeedScale"),
                    ["storageLevel"] = MemberInt(history, 0, "storageLevel"),
                    ["labLevel"] = MemberInt(history, 0, "labLevel"),
                    ["techSpeed"] = MemberInt(history, 0, "techSpeed"),
                    ["buildMaxHeight"] = MemberDouble(history, 0, "buildMaxHeight"),
                    ["beltVerticalConstruction"] = MemberBool(history, false, "beltVerticalConstruction"),
                    ["inserterBidirectional"] = MemberBool(history, false, "inserterBidirectional"),
                    ["inserterStackInput"] = MemberInt(history, 0, "inserterStackInput"),
                    ["inserterStackOutput"] = MemberInt(history, 0, "inserterStackOutput"),
                    ["stationPilerLevel"] = MemberInt(history, 0, "stationPilerLevel"),
                    ["localStationExtraStorage"] = MemberInt(history, 0, "localStationExtraStorage"),
                    ["remoteStationExtraStorage"] = MemberInt(history, 0, "remoteStationExtraStorage"),
                    ["dispenserDeliveryMaxAngle"] = MemberDouble(history, 0, "dispenserDeliveryMaxAngle"),
                    ["planetaryATFieldEnergyRate"] = MemberDouble(history, 0, "planetaryATFieldEnergyRate"),
                    ["universeObserveLevel"] = MemberInt(history, 0, "universeObserveLevel"),
                    ["autoManageLabItems"] = MemberBool(history, false, "autoManageLabItems"),
                    ["logisticDroneSpeedScale"] = MemberDouble(history, 0, "logisticDroneSpeedScale"),
                    ["logisticShipSpeedScale"] = MemberDouble(history, 0, "logisticShipSpeedScale"),
                    ["logisticCourierSpeedScale"] = MemberDouble(history, 0, "logisticCourierSpeedScale"),
                    ["fighterInitializeSpeedScale"] = MemberDouble(history, 0, "fighterInitializeSpeedScale")
                },
                ["dyson"] = new JsonObject
                {
                    ["solarSailLife"] = MemberDouble(history, 0, "solarSailLife"),
                    ["solarEnergyLossRate"] = MemberDouble(history, 0, "solarEnergyLossRate"),
                    ["useIonLayer"] = MemberBool(history, false, "useIonLayer"),
                    ["dysonNodeLatitude"] = MemberDouble(history, 0, "dysonNodeLatitude"),
                    ["dysonNodeAbsorbInterval"] = MemberInt(history, 0, "dysonNodeAbsorbInterval"),
                    ["universeMatrixPointUploaded"] = MemberLong(history, 0, "universeMatrixPointUploaded")
                },
                ["blueprint"] = new JsonObject
                {
                    ["blueprintLimit"] = MemberInt(history, 0, "blueprintLimit"),
                    ["reformLimit"] = MemberInt(history, 0, "bpReformLimit")
                },
                ["combat"] = new JsonObject
                {
                    ["globalHpScale"] = MemberDouble(history, 0, "globalHPScale"),
                    ["kineticDamageScale"] = MemberDouble(history, 0, "kineticDamageScale"),
                    ["energyDamageScale"] = MemberDouble(history, 0, "energyDamageScale"),
                    ["blastDamageScale"] = MemberDouble(history, 0, "blastDamageScale"),
                    ["magneticDamageScale"] = MemberDouble(history, 0, "magneticDamageScale"),
                    ["enemyDropScale"] = MemberDouble(history, 0, "enemyDropScale"),
                    ["groundFleetPortCount"] = MemberInt(history, 0, "groundFleetPortCount"),
                    ["spaceFleetPortCount"] = MemberInt(history, 0, "spaceFleetPortCount"),
                    ["dfTruceTimer"] = MemberLong(history, 0, "dfTruceTimer"),
                    ["minimalDifficulty"] = MemberDouble(history, 0, "minimalDifficulty"),
                    ["minimalPropertyMultiplier"] = MemberDouble(history, 0, "minimalPropertyMultiplier"),
                    ["currentPropertyMultiplier"] = MemberDouble(history, 0, "currentPropertyMultiplier"),
                    ["globalHpEnhancement"] = MemberDouble(history, 0, "globalHpEnhancement"),
                    ["combatDroneDamageRatio"] = MemberDouble(history, 0, "combatDroneDamageRatio"),
                    ["combatDroneDurabilityRatio"] = MemberDouble(history, 0, "combatDroneDurabilityRatio"),
                    ["combatDroneROFRatio"] = MemberDouble(history, 0, "combatDroneROFRatio"),
                    ["combatDroneSpeedRatio"] = MemberDouble(history, 0, "combatDroneSpeedRatio"),
                    ["combatShipDamageRatio"] = MemberDouble(history, 0, "combatShipDamageRatio"),
                    ["combatShipDurabilityRatio"] = MemberDouble(history, 0, "combatShipDurabilityRatio"),
                    ["combatShipROFRatio"] = MemberDouble(history, 0, "combatShipROFRatio"),
                    ["combatShipSpeedRatio"] = MemberDouble(history, 0, "combatShipSpeedRatio")
                },
                ["hasTechLock"] = MemberValue(history, "techLock") != null,
                ["hasPropertyData"] = MemberValue(history, "propertyData") != null,
                ["hasCombatSettings"] = MemberValue(history, "combatSettings") != null,
                ["hasJournalSystem"] = MemberValue(history, "journalSystem") != null,
                ["missionAccomplished"] = MemberBool(history, false, "missionAccomplished"),
                ["createWithSandboxMode"] = MemberBool(history, false, "createWithSandboxMode"),
                ["hasUsedPropertyBanAchievement"] = MemberBool(history, false, "hasUsedPropertyBanAchievement")
            };
        }

        private static JsonObject CaptureGalaxy()
        {
            var galaxy = (object)GameMain.galaxy ?? GetGameDataMember("galaxy");
            if (galaxy == null)
            {
                return Unavailable("galaxy_missing");
            }

            var stars = MemberValue(galaxy, "stars") as Array;
            return new JsonObject
            {
                ["available"] = true,
                ["seed"] = MemberInt(galaxy, 0, "seed"),
                ["starCount"] = MemberInt(galaxy, stars?.Length ?? 0, "starCount"),
                ["habitableCount"] = MemberInt(galaxy, 0, "habitableCount"),
                ["birthStarId"] = MemberInt(galaxy, 0, "birthStarId"),
                ["birthPlanetId"] = MemberInt(galaxy, 0, "birthPlanetId"),
                ["unscannedStarCount"] = MemberInt(galaxy, 0, "unscannedStarCount"),
                ["needAutoScanning"] = MemberBool(galaxy, false, "_need_auto_scanning", "<_need_auto_scanning>k__BackingField"),
                ["scanPreparing"] = MemberBool(galaxy, false, "scan_preparing", "<scan_preparing>k__BackingField"),
                ["astroDataCount"] = CountOf(MemberValue(galaxy, "astrosData")),
                ["astroFactoryCount"] = CountOf(MemberValue(galaxy, "astrosFactory")),
                ["graphNodeCount"] = CountOf(MemberValue(galaxy, "graphNodes")),
                ["factoryPlanetCount"] = ReflectionReader.GetInt(GameMain.data, 0, "factoryCount"),
                ["stars"] = StarSummaries(stars, 128)
            };
        }

        private static JsonObject CaptureDysonSpheres()
        {
            var spheres = GetGameDataMember("dysonSpheres") as Array;
            if (spheres == null)
            {
                return Unavailable("dyson_spheres_missing");
            }

            var items = new List<object>();
            for (var i = 0; i < spheres.Length; i++)
            {
                var sphere = spheres.GetValue(i);
                if (sphere == null)
                {
                    continue;
                }

                items.Add(DysonSphereSummary(sphere, i));
            }

            return new JsonObject
            {
                ["available"] = true,
                ["capacity"] = spheres.Length,
                ["activeCount"] = items.Count,
                ["items"] = items
            };
        }

        private static JsonObject CaptureGalacticTransport()
        {
            var transport = GetGameDataMember("galacticTransport");
            if (transport == null)
            {
                return Unavailable("galactic_transport_missing");
            }

            var stationPool = MemberValue(transport, "stationPool") as Array;
            var stationSummary = StationSummaries(stationPool, 64);
            return new JsonObject
            {
                ["available"] = true,
                ["stationCursor"] = MemberInt(transport, 0, "stationCursor"),
                ["stationCapacity"] = MemberInt(transport, stationPool?.Length ?? 0, "stationCapacity"),
                ["stationRecycleCursor"] = MemberInt(transport, 0, "stationRecycleCursor"),
                ["stationCount"] = Convert.ToInt32(stationSummary["count"]),
                ["stationsByPlanet"] = stationSummary["byPlanet"],
                ["stationsSample"] = stationSummary["items"],
                ["remotePairCount"] = MemberInt(transport, 0, "remotePairCount"),
                ["stationToStationRouteCount"] = CountOf(MemberValue(transport, "station2stationRoutes")),
                ["astroToAstroRouteCount"] = CountOf(MemberValue(transport, "astro2astroRoutes")),
                ["astroToAstroBanCount"] = CountOf(MemberValue(transport, "astro2astroBans"))
            };
        }

        private static JsonObject CaptureSpaceSector()
        {
            var sector = (object)GameMain.spaceSector ?? GetGameDataMember("spaceSector");
            if (sector == null)
            {
                return Unavailable("space_sector_missing");
            }

            return new JsonObject
            {
                ["available"] = true,
                ["isCombatMode"] = MemberBool(sector, false, "isCombatMode"),
                ["astroCursor"] = MemberInt(sector, 0, "astroCursor"),
                ["astroCount"] = ActiveReferenceCount(MemberValue(sector, "astros") as Array),
                ["galaxyAstroCount"] = ActiveReferenceCount(MemberValue(sector, "galaxyAstros") as Array),
                ["enemyCount"] = MemberInt(sector, 0, "enemyCount"),
                ["enemyCursor"] = MemberInt(sector, 0, "enemyCursor"),
                ["enemyCapacity"] = MemberInt(sector, ArrayLength(MemberValue(sector, "enemyPool") as Array), "enemyCapacity"),
                ["enemyRecycleCursor"] = MemberInt(sector, 0, "enemyRecycleCursor"),
                ["enemyRecycleCount"] = CountOf(MemberValue(sector, "enemyRecycle")),
                ["craftCount"] = MemberInt(sector, 0, "craftCount"),
                ["craftCursor"] = MemberInt(sector, 0, "craftCursor"),
                ["craftCapacity"] = MemberInt(sector, ArrayLength(MemberValue(sector, "craftPool") as Array), "craftCapacity"),
                ["craftRecycleCursor"] = MemberInt(sector, 0, "craftRecycleCursor"),
                ["craftRecycleCount"] = CountOf(MemberValue(sector, "craftRecycle")),
                ["maxHiveCount"] = MemberInt(sector, 0, "maxHiveCount"),
                ["lastAliveHiveCount"] = MemberInt(sector, 0, "lastAliveHiveCount"),
                ["spaceRuins"] = PoolSummary(MemberValue(sector, "spaceRuins")),
                ["dfHiveCount"] = ActiveReferenceCount(MemberValue(sector, "dfHives") as Array),
                ["dfHiveByAstroCount"] = ActiveReferenceCount(MemberValue(sector, "dfHivesByAstro") as Array),
                ["enemyPoolCapacity"] = ArrayLength(MemberValue(sector, "enemyPool") as Array),
                ["craftPoolCapacity"] = ArrayLength(MemberValue(sector, "craftPool") as Array)
            };
        }

        private static JsonObject CaptureWarningSystem()
        {
            var warnings = GetGameDataMember("warningSystem");
            if (warnings == null)
            {
                return Unavailable("warning_system_missing");
            }

            var warningPool = MemberValue(warnings, "warningPool") as Array;
            return new JsonObject
            {
                ["available"] = true,
                ["warningTotalCount"] = MemberInt(warnings, 0, "warningTotalCount"),
                ["broadcastUIAlertCount"] = MemberInt(warnings, 0, "broadcastUIAlertCount"),
                ["hasCriticalWarning"] = MemberBool(warnings, false, "hasCriticalWarning"),
                ["criticalWarningTexts"] = MemberString(warnings, "criticalWarningTexts"),
                ["focusTargetId"] = MemberInt(warnings, 0, "focusTargetId"),
                ["focusSignalId"] = MemberInt(warnings, 0, "focusSignalId"),
                ["focusDetailSignalCount"] = MemberInt(warnings, 0, "focusDetailSignalCount"),
                ["warningSignalCount"] = MemberInt(warnings, 0, "warningSignalCount"),
                ["warningCursor"] = MemberInt(warnings, 0, "warningCursor"),
                ["warningCapacity"] = MemberInt(warnings, warningPool?.Length ?? 0, "warningCapacity"),
                ["warningRecycleCursor"] = MemberInt(warnings, 0, "warningRecycleCursor"),
                ["activeWarnings"] = WarningSummaries(warningPool, 64),
                ["criticalWarningCount"] = CountOf(MemberValue(warnings, "criticalWarnings")),
                ["broadcastCount"] = CountOf(MemberValue(warnings, "broadcasts")),
                ["broadcastConfigCount"] = CountOf(MemberValue(warnings, "broadcastConfigs")),
                ["warningCountEntries"] = CountOf(MemberValue(warnings, "warningCounts"))
            };
        }

        private static JsonObject CaptureTrashSystem()
        {
            var trash = GetGameDataMember("trashSystem");
            if (trash == null)
            {
                return Unavailable("trash_system_missing");
            }

            return new JsonObject
            {
                ["available"] = true,
                ["trashCount"] = MemberInt(trash, 0, "trashCount"),
                ["randSeed"] = MemberInt(trash, 0, "randSeed"),
                ["enemyDropBanCount"] = CountOf(MemberValue(trash, "enemyDropBans"))
            };
        }

        private static JsonObject CaptureGoalSystem()
        {
            var goals = GetGameDataMember("goalSystem");
            if (goals == null)
            {
                return Unavailable("goal_system_missing");
            }

            return new JsonObject
            {
                ["available"] = true,
                ["goalCount"] = CountOf(MemberValue(goals, "goalDatas")),
                ["queueCursor"] = MemberInt(goals, 0, "queueCursor"),
                ["queuedGoalIds"] = IntArray(MemberValue(goals, "goalQueue") as Array, 64, false)
            };
        }

        private static JsonObject CaptureMilestoneSystem()
        {
            var milestones = GetGameDataMember("milestoneSystem");
            if (milestones == null)
            {
                return Unavailable("milestone_system_missing");
            }

            return new JsonObject
            {
                ["available"] = true,
                ["milestoneCount"] = CountOf(MemberValue(milestones, "milestoneDatas"))
            };
        }

        private static JsonObject CaptureGameAchievement()
        {
            var achievement = GetGameDataMember("gameAchievement");
            var desc = GetGameDataMember("gameDesc");
            var history = (object)GameMain.history ?? GetGameDataMember("history");
            if (achievement == null)
            {
                return Unavailable("game_achievement_missing");
            }

            return new JsonObject
            {
                ["available"] = true,
                ["runtimeAsmLoaded"] = MemberValue(achievement, "runtimeAsm") != null,
                ["achievementEnable"] = MemberBool(desc, false, "achievementEnable"),
                ["runtimeDataCount"] = CountOf(MemberValue(achievement, "runtimeDatas")),
                ["propertyMultiplier"] = MemberDouble(desc, 0, "propertyMultiplier"),
                ["currentPropertyMultiplier"] = MemberDouble(history, 0, "currentPropertyMultiplier"),
                ["hasUsedPropertyBanAchievement"] = MemberBool(history, false, "hasUsedPropertyBanAchievement")
            };
        }

        private static object GetGameDataMember(params string[] names)
        {
            return GetMemberValue(GameMain.data, names);
        }

        private static object GetMemberValue(object target, params string[] names)
        {
            if (target == null)
            {
                return null;
            }

            var type = target.GetType();
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            foreach (var name in names)
            {
                var field = type.GetField(name, flags);
                if (field != null)
                {
                    try
                    {
                        return field.GetValue(target);
                    }
                    catch
                    {
                        continue;
                    }
                }

                var property = type.GetProperty(name, flags);
                if (property != null && property.GetIndexParameters().Length == 0)
                {
                    try
                    {
                        return property.GetValue(target, null);
                    }
                    catch
                    {
                        continue;
                    }
                }
            }

            return null;
        }

        private static object MemberValue(object target, params string[] names)
        {
            return GetMemberValue(target, names);
        }

        private static string MemberString(object target, params string[] names)
        {
            return MemberValue(target, names)?.ToString();
        }

        private static int MemberInt(object target, int defaultValue, params string[] names)
        {
            var value = MemberValue(target, names);
            if (value == null)
            {
                return defaultValue;
            }

            try
            {
                return Convert.ToInt32(value);
            }
            catch
            {
                return defaultValue;
            }
        }

        private static long MemberLong(object target, long defaultValue, params string[] names)
        {
            var value = MemberValue(target, names);
            if (value == null)
            {
                return defaultValue;
            }

            try
            {
                return Convert.ToInt64(value);
            }
            catch
            {
                return defaultValue;
            }
        }

        private static double MemberDouble(object target, double defaultValue, params string[] names)
        {
            var value = MemberValue(target, names);
            if (value == null)
            {
                return defaultValue;
            }

            try
            {
                return Convert.ToDouble(value);
            }
            catch
            {
                return defaultValue;
            }
        }

        private static bool MemberBool(object target, bool defaultValue, params string[] names)
        {
            var value = MemberValue(target, names);
            if (value == null)
            {
                return defaultValue;
            }

            try
            {
                return Convert.ToBoolean(value);
            }
            catch
            {
                return defaultValue;
            }
        }

        private static int CountOf(object value)
        {
            if (value == null)
            {
                return 0;
            }

            if (value is Array array)
            {
                return array.Length;
            }

            if (value is ICollection collection)
            {
                return collection.Count;
            }

            return MemberInt(value, 0, "count", "Count", "length", "Length");
        }

        private static int ArrayLength(Array array)
        {
            return array?.Length ?? 0;
        }

        private static int ActiveReferenceCount(Array array)
        {
            if (array == null)
            {
                return 0;
            }

            var count = 0;
            for (var i = 0; i < array.Length; i++)
            {
                if (array.GetValue(i) != null)
                {
                    count++;
                }
            }

            return count;
        }

        private static List<object> IntArray(Array array, int limit, bool includeZeros)
        {
            var result = new List<object>();
            if (array == null)
            {
                return result;
            }

            for (var i = 0; i < array.Length && result.Count < limit; i++)
            {
                var value = Convert.ToInt32(array.GetValue(i));
                if (!includeZeros && value == 0)
                {
                    continue;
                }

                result.Add(value);
            }

            return result;
        }

        private static List<object> TechQueue(IEnumerable values)
        {
            var result = new List<object>();
            if (values == null)
            {
                return result;
            }

            foreach (var value in values)
            {
                var techId = Convert.ToInt32(value);
                if (techId <= 0)
                {
                    continue;
                }

                result.Add(new JsonObject
                {
                    ["techId"] = techId,
                    ["name"] = TechName(techId)
                });
            }

            return result;
        }

        private static List<object> StarSummaries(Array stars, int limit)
        {
            var result = new List<object>();
            if (stars == null)
            {
                return result;
            }

            for (var i = 0; i < stars.Length && result.Count < limit; i++)
            {
                var star = stars.GetValue(i);
                if (star == null)
                {
                    continue;
                }

                var planets = MemberValue(star, "planets") as Array;
                result.Add(new JsonObject
                {
                    ["id"] = MemberInt(star, 0, "id"),
                    ["index"] = MemberInt(star, i, "index"),
                    ["name"] = MemberString(star, "name"),
                    ["displayName"] = MemberString(star, "displayName"),
                    ["type"] = MemberValue(star, "type")?.ToString(),
                    ["spectr"] = MemberValue(star, "spectr")?.ToString(),
                    ["planetCount"] = MemberInt(star, planets?.Length ?? 0, "planetCount"),
                    ["luminosity"] = MemberDouble(star, 0, "luminosity"),
                    ["level"] = MemberDouble(star, 0, "level")
                });
            }

            return result;
        }

        private static JsonObject DysonSphereSummary(object sphere, int index)
        {
            var stars = GameMain.galaxy == null ? null : MemberValue(GameMain.galaxy, "stars") as Array;
            var star = stars != null && index >= 0 && index < stars.Length ? stars.GetValue(index) : null;
            var layers = FirstArray(sphere, "layersIdBased", "layersSorted", "layerPool", "layers");

            return new JsonObject
            {
                ["index"] = index,
                ["starId"] = star == null ? 0 : MemberInt(star, 0, "id"),
                ["starName"] = star == null ? null : MemberString(star, "displayName", "name"),
                ["layerCount"] = ActiveReferenceCount(layers),
                ["nodeCount"] = MemberInt(sphere, 0, "nodeCursor", "nodeCount"),
                ["frameCount"] = MemberInt(sphere, 0, "frameCursor", "frameCount"),
                ["shellCount"] = MemberInt(sphere, 0, "shellCursor", "shellCount"),
                ["rocketCount"] = MemberInt(sphere, 0, "rocketCursor", "rocketCount"),
                ["sailCount"] = MemberInt(sphere, 0, "swarmSailCount", "sailCount")
            };
        }

        private static JsonObject StationSummaries(Array stationPool, int limit)
        {
            var items = new List<object>();
            var byPlanet = new Dictionary<int, int>();
            var count = 0;

            if (stationPool != null)
            {
                for (var i = 0; i < stationPool.Length; i++)
                {
                    var station = stationPool.GetValue(i);
                    if (station == null)
                    {
                        continue;
                    }

                    var id = MemberInt(station, 0, "id");
                    if (id <= 0)
                    {
                        continue;
                    }

                    count++;
                    var planetId = MemberInt(station, 0, "planetId");
                    byPlanet.TryGetValue(planetId, out var planetCount);
                    byPlanet[planetId] = planetCount + 1;

                    if (items.Count < limit)
                    {
                        items.Add(new JsonObject
                        {
                            ["index"] = i,
                            ["id"] = id,
                            ["planetId"] = planetId,
                            ["gid"] = MemberInt(station, 0, "gid"),
                            ["isStellar"] = MemberBool(station, false, "isStellar")
                        });
                    }
                }
            }

            var byPlanetItems = new List<object>();
            foreach (var pair in byPlanet)
            {
                byPlanetItems.Add(new JsonObject
                {
                    ["planetId"] = pair.Key,
                    ["count"] = pair.Value
                });
            }

            return new JsonObject
            {
                ["count"] = count,
                ["items"] = items,
                ["byPlanet"] = byPlanetItems
            };
        }

        private static List<object> WarningSummaries(Array warningPool, int limit)
        {
            var result = new List<object>();
            if (warningPool == null)
            {
                return result;
            }

            for (var i = 0; i < warningPool.Length && result.Count < limit; i++)
            {
                var warning = warningPool.GetValue(i);
                var id = MemberInt(warning, 0, "id");
                if (id <= 0)
                {
                    continue;
                }

                result.Add(new JsonObject
                {
                    ["index"] = i,
                    ["id"] = id,
                    ["type"] = MemberValue(warning, "type", "warningType")?.ToString(),
                    ["astroId"] = MemberInt(warning, 0, "astroId"),
                    ["factoryId"] = MemberInt(warning, 0, "factoryId"),
                    ["entityId"] = MemberInt(warning, 0, "entityId"),
                    ["itemId"] = MemberInt(warning, 0, "itemId")
                });
            }

            return result;
        }

        private static JsonObject PoolSummary(object pool)
        {
            if (pool == null)
            {
                return Unavailable("pool_missing");
            }

            return new JsonObject
            {
                ["available"] = true,
                ["count"] = CountOf(pool),
                ["cursor"] = MemberInt(pool, 0, "cursor")
            };
        }

        private static Array FirstArray(object target, params string[] names)
        {
            foreach (var name in names)
            {
                var array = MemberValue(target, name) as Array;
                if (array != null)
                {
                    return array;
                }
            }

            return null;
        }

        private static object VectorOrNull(object value)
        {
            if (value is Vector3 vector3)
            {
                return Vector(vector3);
            }

            if (value is VectorLF3 vectorLf3)
            {
                return Vector(vectorLf3);
            }

            return null;
        }

        private static object QuaternionOrNull(object value)
        {
            if (value is Quaternion quaternion)
            {
                return new JsonObject
                {
                    ["x"] = quaternion.x,
                    ["y"] = quaternion.y,
                    ["z"] = quaternion.z,
                    ["w"] = quaternion.w
                };
            }

            return null;
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
            var byItemInc = new Dictionary<int, int>();
            var byItemSlots = new Dictionary<int, int>();
            var emptySlots = 0;
            var lockedSlots = 0;
            var gridLength = package.grids?.Length ?? 0;
            var slotCount = Math.Max(package.size, gridLength);

            for (var i = 0; i < slotCount; i++)
            {
                if (i >= gridLength)
                {
                    if (i < package.size)
                    {
                        emptySlots++;
                    }

                    continue;
                }

                var grid = package.grids[i];
                if (grid.itemId <= 0 || grid.count <= 0)
                {
                    if (i < package.size)
                    {
                        emptySlots++;
                    }
                    else
                    {
                        lockedSlots++;
                    }

                    continue;
                }

                items.Add(new JsonObject
                {
                    ["slot"] = i,
                    ["locked"] = i >= package.size,
                    ["itemId"] = grid.itemId,
                    ["name"] = ItemName(grid.itemId),
                    ["count"] = grid.count,
                    ["stackSize"] = grid.stackSize,
                    ["inc"] = grid.inc
                });

                byItem.TryGetValue(grid.itemId, out var count);
                byItem[grid.itemId] = count + grid.count;
                byItemInc.TryGetValue(grid.itemId, out var inc);
                byItemInc[grid.itemId] = inc + grid.inc;
                byItemSlots.TryGetValue(grid.itemId, out var slots);
                byItemSlots[grid.itemId] = slots + 1;
            }

            return new JsonObject
            {
                ["available"] = true,
                ["size"] = package.size,
                ["gridLength"] = gridLength,
                ["slotCount"] = slotCount,
                ["emptySlots"] = emptySlots,
                ["lockedSlots"] = lockedSlots,
                ["nonEmptySlots"] = items.Count,
                ["totalItemCount"] = TotalItemCount(byItem),
                ["items"] = items,
                ["summary"] = ItemSummary(byItem, byItemInc, byItemSlots)
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

        private static int TotalItemCount(Dictionary<int, int> items)
        {
            var total = 0;
            foreach (var pair in items)
            {
                total += pair.Value;
            }

            return total;
        }

        private static JsonObject ItemSummary(Dictionary<int, int> items)
        {
            return ItemSummary(items, null, null);
        }

        private static JsonObject ItemSummary(Dictionary<int, int> items, Dictionary<int, int> incs, Dictionary<int, int> slots)
        {
            var result = new List<object>();
            foreach (var pair in items)
            {
                var item = new JsonObject
                {
                    ["itemId"] = pair.Key,
                    ["name"] = ItemName(pair.Key),
                    ["count"] = pair.Value
                };

                if (incs != null)
                {
                    incs.TryGetValue(pair.Key, out var inc);
                    item["inc"] = inc;
                }

                if (slots != null)
                {
                    slots.TryGetValue(pair.Key, out var slotCount);
                    item["slots"] = slotCount;
                }

                result.Add(item);
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
