using System;
using System.Collections.Generic;
using System.IO;
using AutomaticDSP.Serialization;
using BepInEx.Logging;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace AutomaticDSP.Storage
{
    internal sealed class HistoryStore : IDisposable
    {
        private readonly string databasePath;
        private readonly ManualLogSource log;

        public HistoryStore(string configPath, ManualLogSource log)
        {
            this.log = log;
            databasePath = Path.Combine(configPath, "AutomaticDSP", "history.sqlite");
        }

        public void Initialize()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(databasePath));
            Batteries_V2.Init();

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
                        snapshot_game_tick INTEGER NULL
                    );";
                command.ExecuteNonQuery();
            }

            log.LogInfo($"AutomaticDSP history database ready: {databasePath}");
        }

        public JsonObject GetHistoryResponse(int limit)
        {
            var items = new List<object>();
            using (var connection = OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    @"SELECT id, task_id, command_id, command_type, status, started_at, completed_at,
                             error_code, error_message, snapshot_game_tick
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
                            ["snapshotGameTick"] = reader.IsDBNull(9) ? (object)null : reader.GetInt64(9)
                        });
                    }
                }
            }

            return new JsonObject
            {
                ["items"] = items
            };
        }

        public void Dispose()
        {
        }

        private SqliteConnection OpenConnection()
        {
            var connection = new SqliteConnection($"Data Source={databasePath}");
            connection.Open();
            return connection;
        }

        private static string NullableString(SqliteDataReader reader, int ordinal)
        {
            return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
        }
    }
}
