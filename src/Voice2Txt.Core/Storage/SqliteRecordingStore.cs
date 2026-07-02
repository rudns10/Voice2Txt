using Microsoft.Data.Sqlite;
using Voice2Txt.Core.Abstractions;

namespace Voice2Txt.Core.Storage;

/// <summary>SQLite 기반 녹음 메타데이터 저장소.</summary>
public sealed class SqliteRecordingStore : IRecordingStore
{
    private readonly string _connectionString;

    /// <param name="dbPath">DB 파일 경로(미지정 시 기본 경로).</param>
    public SqliteRecordingStore(string? dbPath = null)
    {
        var path = dbPath ?? StoragePaths.DbPath;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS Recordings (
                Id              TEXT PRIMARY KEY,
                Name            TEXT NOT NULL,
                CreatedAt       TEXT NOT NULL,
                DurationSeconds REAL NOT NULL,
                FilePath        TEXT NOT NULL,
                IsTranscribed   INTEGER NOT NULL DEFAULT 0,
                TranscriptPath  TEXT NULL
            );
            """;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<Recording>> GetAllAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT Id, Name, CreatedAt, DurationSeconds, FilePath, IsTranscribed, TranscriptPath
            FROM Recordings
            ORDER BY CreatedAt DESC;
            """;

        var list = new List<Recording>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new Recording
            {
                Id = reader.GetString(0),
                Name = reader.GetString(1),
                CreatedAt = DateTimeOffset.Parse(reader.GetString(2)),
                Duration = TimeSpan.FromSeconds(reader.GetDouble(3)),
                FilePath = reader.GetString(4),
                IsTranscribed = reader.GetInt64(5) != 0,
                TranscriptPath = reader.IsDBNull(6) ? null : reader.GetString(6)
            });
        }
        return list;
    }

    public async Task AddAsync(Recording r, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO Recordings
                (Id, Name, CreatedAt, DurationSeconds, FilePath, IsTranscribed, TranscriptPath)
            VALUES
                ($id, $name, $createdAt, $duration, $filePath, $isTranscribed, $transcriptPath);
            """;
        Bind(cmd, r);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task UpdateAsync(Recording r, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE Recordings SET
                Name = $name,
                CreatedAt = $createdAt,
                DurationSeconds = $duration,
                FilePath = $filePath,
                IsTranscribed = $isTranscribed,
                TranscriptPath = $transcriptPath
            WHERE Id = $id;
            """;
        Bind(cmd, r);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM Recordings WHERE Id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        return conn;
    }

    private static void Bind(SqliteCommand cmd, Recording r)
    {
        cmd.Parameters.AddWithValue("$id", r.Id);
        cmd.Parameters.AddWithValue("$name", r.Name);
        cmd.Parameters.AddWithValue("$createdAt", r.CreatedAt.ToString("o"));
        cmd.Parameters.AddWithValue("$duration", r.Duration.TotalSeconds);
        cmd.Parameters.AddWithValue("$filePath", r.FilePath);
        cmd.Parameters.AddWithValue("$isTranscribed", r.IsTranscribed ? 1 : 0);
        cmd.Parameters.AddWithValue("$transcriptPath", (object?)r.TranscriptPath ?? DBNull.Value);
    }
}
