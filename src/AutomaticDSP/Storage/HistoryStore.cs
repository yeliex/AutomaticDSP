using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using AutomaticDSP.Serialization;
using BepInEx.Logging;

namespace AutomaticDSP.Storage
{
    internal sealed class HistoryStore : IDisposable
    {
        private readonly string databasePath;
        private readonly ManualLogSource log;
        private bool available;
        private string unavailableReason;

        public HistoryStore(string cacheRootPath, ManualLogSource log)
        {
            this.log = log;
            databasePath = Path.Combine(cacheRootPath, "data", "history.sqlite");
        }

        public void Initialize()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(databasePath));

                using (var connection = OpenConnection())
                using (var command = connection.CreateCommand())
                {
                    command.CommandText =
                        @"CREATE TABLE IF NOT EXISTS command_history (
                            id INTEGER PRIMARY KEY AUTOINCREMENT,
                            task_id TEXT NULL,
                            command_id TEXT NULL,
                            command_type TEXT NULL,
                            status TEXT NOT NULL,
                            started_at TEXT NULL,
                            completed_at TEXT NULL,
                            error_code TEXT NULL,
                            error_message TEXT NULL,
                            game_tick INTEGER NULL
                        );";
                    command.ExecuteNonQuery();
                    EnsureHistorySchema(connection);
                }

                available = true;
                unavailableReason = null;
                log.LogInfo($"AutomaticDSP history database ready: {databasePath}");
            }
            catch (Exception ex)
            {
                available = false;
                unavailableReason = ex.GetType().Name + ": " + ex.Message;
                log.LogWarning($"AutomaticDSP history database unavailable: {unavailableReason}");
            }
        }

        public JsonObject GetHistoryResponse(int limit)
        {
            var items = new List<object>();
            if (!available)
            {
                return HistoryResponse(items);
            }

            try
            {
                using (var connection = OpenConnection())
                using (var command = connection.CreateCommand())
                {
                    command.CommandText =
                        @"SELECT id, task_id, command_id, command_type, status, started_at, completed_at,
                                 error_code, error_message, game_tick
                          FROM command_history
                          ORDER BY id DESC
                          LIMIT $limit;";
                    command.Parameters.AddWithValue("$limit", limit);

                    using (var reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            items.Add(new JsonObject
                            {
                                ["id"] = reader.GetInt64(0),
                                ["taskId"] = NullableString(reader, 1),
                                ["commandId"] = NullableString(reader, 2),
                                ["commandType"] = NullableString(reader, 3),
                                ["status"] = reader.GetString(4),
                                ["startedAt"] = NullableString(reader, 5),
                                ["completedAt"] = NullableString(reader, 6),
                                ["errorCode"] = NullableString(reader, 7),
                                ["errorMessage"] = NullableString(reader, 8),
                                ["gameTick"] = reader.IsDBNull(9) ? (object)null : reader.GetInt64(9)
                            });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                available = false;
                unavailableReason = ex.GetType().Name + ": " + ex.Message;
                log.LogWarning($"AutomaticDSP history query failed: {unavailableReason}");
            }

            return HistoryResponse(items);
        }

        private static void EnsureHistorySchema(SQLiteConnection connection)
        {
            var hasGameTick = false;
            var hasLegacySnapshotGameTick = false;
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "PRAGMA table_info(command_history);";
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var name = reader.GetString(1);
                        if (name == "game_tick")
                        {
                            hasGameTick = true;
                        }
                        else if (name == "snapshot_game_tick")
                        {
                            hasLegacySnapshotGameTick = true;
                        }
                    }
                }
            }

            if (!hasGameTick)
            {
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "ALTER TABLE command_history ADD COLUMN game_tick INTEGER NULL;";
                    command.ExecuteNonQuery();
                }
            }

            if (hasLegacySnapshotGameTick)
            {
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "UPDATE command_history SET game_tick = snapshot_game_tick WHERE game_tick IS NULL;";
                    command.ExecuteNonQuery();
                }
            }
        }

        public void Dispose()
        {
        }

        private SQLiteConnection OpenConnection()
        {
            var connection = new SQLiteConnection($"Data Source={databasePath};Version=3;");
            connection.Open();
            return connection;
        }

        private JsonObject HistoryResponse(List<object> items)
        {
            return new JsonObject
            {
                ["available"] = available,
                ["unavailableReason"] = unavailableReason,
                ["items"] = items
            };
        }

        private static string NullableString(SQLiteDataReader reader, int ordinal)
        {
            return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
        }
    }
}
