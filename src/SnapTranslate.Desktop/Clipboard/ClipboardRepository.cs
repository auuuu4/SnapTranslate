using System.Globalization;
using Microsoft.Data.Sqlite;
using SnapTranslate.Desktop.Platform;

namespace SnapTranslate.Desktop.Clipboard;

/// <summary>
/// 剪贴板历史的 SQLite 存储，库文件位于 &lt;AppData&gt;/Data/clipboard.db。
/// 相同哈希只保留一条；超过上限（默认 500 条）时裁剪最旧的未收藏记录。
/// </summary>
public sealed class ClipboardRepository : IDisposable
{
    /// <summary>默认保留的最大记录数。</summary>
    public const int DefaultMaxItems = 500;

    private const string SelectColumns = """
        SELECT id, kind, text, file_path, hash, created_at, favorite, note
        FROM clipboard_items
        """;

    private readonly object _gate = new();
    private readonly SqliteConnection _connection;
    private bool _disposed;

    /// <summary>创建仓库。<paramref name="dataDirectory"/> 为空时使用 &lt;AppData&gt;/Data。</summary>
    public ClipboardRepository(string? dataDirectory = null, int maxItems = DefaultMaxItems)
    {
        DataDirectory = string.IsNullOrWhiteSpace(dataDirectory)
            ? Path.Combine(AppDataPaths.ApplicationDataDirectory, "Data")
            : dataDirectory;
        MaxItems = Math.Max(1, maxItems);
        ImagesDirectory = Path.Combine(DataDirectory, "clipboard-images");
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(ImagesDirectory);

        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(DataDirectory, "clipboard.db"),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString());
        _connection.Open();
        InitializeSchema();
    }

    /// <summary>数据目录（含数据库与图片子目录）。</summary>
    public string DataDirectory { get; }

    /// <summary>剪贴板图片落盘目录。</summary>
    public string ImagesDirectory { get; }

    /// <summary>最大记录条数。</summary>
    public int MaxItems { get; }

    /// <summary>写入一条捕获内容；哈希重复时移动到最前面而不新增。</summary>
    public ClipboardItem InsertOrUpdate(Platform.ClipboardCapture capture)
    {
        ArgumentNullException.ThrowIfNull(capture);
        lock (_gate)
        {
            ThrowIfDisposed();
            var hash = string.IsNullOrWhiteSpace(capture.Hash) ? ComputeCaptureHash(capture) : capture.Hash;
            var createdAt = capture.CapturedAt == default ? DateTimeOffset.Now : capture.CapturedAt;
            using (var command = _connection.CreateCommand())
            {
                command.CommandText = """
                    INSERT INTO clipboard_items (id, kind, text, file_path, hash, created_at, favorite, note)
                    VALUES ($id, $kind, $text, $file, $hash, $created, 0, '')
                    ON CONFLICT(hash) DO UPDATE SET
                        text = excluded.text,
                        file_path = excluded.file_path,
                        created_at = excluded.created_at;
                    """;
                command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
                command.Parameters.AddWithValue("$kind", (int)capture.Kind);
                command.Parameters.AddWithValue("$text", capture.Text);
                command.Parameters.AddWithValue("$file", (object?)capture.FilePath ?? DBNull.Value);
                command.Parameters.AddWithValue("$hash", hash);
                command.Parameters.AddWithValue("$created", createdAt.ToString("O"));
                command.ExecuteNonQuery();
            }

            TrimToMaxItems();
            return GetByHashLocked(hash) ?? throw new InvalidOperationException("剪贴板记录写入失败。");
        }
    }

    /// <summary>按关键词（模糊匹配文本、文件路径、备注）和类型查询，按时间倒序分页。</summary>
    public IReadOnlyList<ClipboardItem> Search(
        string? keyword = null,
        ClipboardCaptureKind? kind = null,
        int limit = DefaultMaxItems,
        int offset = 0)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            using var command = _connection.CreateCommand();
            var where = BuildWhere(command, keyword, kind);
            command.CommandText = SelectColumns
                + where
                + " ORDER BY created_at DESC, rowid DESC LIMIT $limit OFFSET $offset";
            command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 1000));
            command.Parameters.AddWithValue("$offset", Math.Max(0, offset));
            using var reader = command.ExecuteReader();
            var items = new List<ClipboardItem>();
            while (reader.Read()) items.Add(ReadItem(reader));
            return items;
        }
    }

    /// <summary>统计匹配条数。</summary>
    public int Count(string? keyword = null, ClipboardCaptureKind? kind = null)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM clipboard_items" + BuildWhere(command, keyword, kind);
            return Convert.ToInt32(command.ExecuteScalar());
        }
    }

    /// <summary>按 Id 取一条记录。</summary>
    public ClipboardItem? GetById(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        lock (_gate)
        {
            ThrowIfDisposed();
            return GetByIdLocked(id);
        }
    }

    /// <summary>设置收藏状态。</summary>
    public void SetFavorite(string id, bool favorite)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        lock (_gate)
        {
            ThrowIfDisposed();
            ExecuteUpdateLocked(
                "UPDATE clipboard_items SET favorite = $favorite WHERE id = $id",
                ("$id", id),
                ("$favorite", favorite ? 1 : 0));
        }
    }

    /// <summary>切换收藏状态，返回切换后的状态；记录不存在时返回 false。</summary>
    public bool ToggleFavorite(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        lock (_gate)
        {
            ThrowIfDisposed();
            var affected = ExecuteUpdateLocked(
                "UPDATE clipboard_items SET favorite = 1 - favorite WHERE id = $id",
                ("$id", id));
            if (affected == 0) return false;
            return GetByIdLocked(id)?.Favorite == true;
        }
    }

    /// <summary>更新备注。</summary>
    public void SetNote(string id, string note)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(note);
        lock (_gate)
        {
            ThrowIfDisposed();
            ExecuteUpdateLocked(
                "UPDATE clipboard_items SET note = $note WHERE id = $id",
                ("$id", id),
                ("$note", note.Trim()));
        }
    }

    /// <summary>删除一条记录及其独占的图片文件。</summary>
    public bool Delete(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        lock (_gate)
        {
            ThrowIfDisposed();
            var item = GetByIdLocked(id);
            if (item is null) return false;
            ExecuteUpdateLocked("DELETE FROM clipboard_items WHERE id = $id", ("$id", id));
            DeleteOrphanImage(item.FilePath);
            return true;
        }
    }

    /// <summary>清空所有记录和图片文件。</summary>
    public void Clear()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            ExecuteUpdateLocked("DELETE FROM clipboard_items");
            TryDeleteImageFiles();
        }
    }

    /// <summary>删除超出上限的最旧未收藏记录，返回删除条数。</summary>
    public int TrimToMaxItems()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            using var command = _connection.CreateCommand();
            command.CommandText = """
                DELETE FROM clipboard_items
                WHERE favorite = 0 AND id IN (
                    SELECT id FROM clipboard_items
                    WHERE favorite = 0
                    ORDER BY created_at DESC, rowid DESC
                    LIMIT -1 OFFSET $max
                );
                """;
            command.Parameters.AddWithValue("$max", MaxItems);
            var deleted = command.ExecuteNonQuery();
            if (deleted > 0) CleanupOrphanImages();
            return deleted;
        }
    }

    /// <summary>释放数据库连接。</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _connection.Dispose();
        }
    }

    private void InitializeSchema()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;
            CREATE TABLE IF NOT EXISTS clipboard_items (
                id TEXT PRIMARY KEY NOT NULL,
                kind INTEGER NOT NULL,
                text TEXT NOT NULL,
                file_path TEXT NULL,
                hash TEXT NOT NULL UNIQUE,
                created_at TEXT NOT NULL,
                favorite INTEGER NOT NULL DEFAULT 0,
                note TEXT NOT NULL DEFAULT ''
            );
            CREATE INDEX IF NOT EXISTS idx_clipboard_items_created ON clipboard_items(created_at DESC);
            CREATE INDEX IF NOT EXISTS idx_clipboard_items_kind ON clipboard_items(kind);
            """;
        command.ExecuteNonQuery();
    }

    private static string BuildWhere(SqliteCommand command, string? keyword, ClipboardCaptureKind? kind)
    {
        var clauses = new List<string>();
        if (kind is not null)
        {
            clauses.Add("kind = $kind");
            command.Parameters.AddWithValue("$kind", (int)kind.Value);
        }
        if (!string.IsNullOrWhiteSpace(keyword))
        {
            clauses.Add("(text LIKE $pattern ESCAPE '\\' OR IFNULL(file_path, '') LIKE $pattern ESCAPE '\\' OR note LIKE $pattern ESCAPE '\\')");
            command.Parameters.AddWithValue("$pattern", BuildLikePattern(keyword.Trim()));
        }
        return clauses.Count == 0 ? string.Empty : " WHERE " + string.Join(" AND ", clauses);
    }

    private static string BuildLikePattern(string keyword)
    {
        var escaped = keyword
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
        return $"%{escaped}%";
    }

    private static ClipboardItem ReadItem(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(0),
        Kind = (ClipboardCaptureKind)reader.GetInt32(1),
        Text = reader.GetString(2),
        FilePath = reader.IsDBNull(3) ? null : reader.GetString(3),
        Hash = reader.GetString(4),
        CreatedAt = DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        Favorite = reader.GetInt32(6) != 0,
        Note = reader.GetString(7)
    };

    private ClipboardItem? GetByIdLocked(string id)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = SelectColumns + " WHERE id = $id LIMIT 1";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadItem(reader) : null;
    }

    private ClipboardItem? GetByHashLocked(string hash)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = SelectColumns + " WHERE hash = $hash LIMIT 1";
        command.Parameters.AddWithValue("$hash", hash);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadItem(reader) : null;
    }

    private int ExecuteUpdateLocked(string sql, params (string Name, object Value)[] parameters)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return command.ExecuteNonQuery();
    }

    private static string ComputeCaptureHash(Platform.ClipboardCapture capture)
    {
        if (capture.Kind == ClipboardCaptureKind.Image && !string.IsNullOrWhiteSpace(capture.FilePath) && File.Exists(capture.FilePath))
            return ClipboardHash.Compute(capture.Kind, ClipboardHash.FileDigest(capture.FilePath));
        var payload = capture.Kind == ClipboardCaptureKind.Files && string.IsNullOrEmpty(capture.Text)
            ? capture.FilePath ?? string.Empty
            : capture.Text;
        return ClipboardHash.Compute(capture.Kind, payload);
    }

    private void DeleteOrphanImage(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return;
        if (!IsInsideImagesDirectory(filePath)) return;
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM clipboard_items WHERE file_path = $file";
        command.Parameters.AddWithValue("$file", filePath);
        if (Convert.ToInt32(command.ExecuteScalar()) > 0) return;
        try
        {
            File.Delete(filePath);
        }
        catch (IOException)
        {
            // 图片文件可能正被其他进程占用，下次清空时再处理。
        }
        catch (UnauthorizedAccessException)
        {
            // 清理失败不影响删除记录本身。
        }
    }

    private void CleanupOrphanImages()
    {
        if (!Directory.Exists(ImagesDirectory)) return;
        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var command = _connection.CreateCommand())
        {
            command.CommandText = "SELECT file_path FROM clipboard_items WHERE file_path IS NOT NULL";
            using var reader = command.ExecuteReader();
            while (reader.Read()) referenced.Add(reader.GetString(0));
        }

        var cutoff = DateTime.UtcNow.AddMinutes(-10);
        foreach (var path in Directory.EnumerateFiles(ImagesDirectory, "*.png"))
        {
            if (referenced.Contains(path)) continue;
            try
            {
                if (File.GetLastWriteTimeUtc(path) < cutoff) File.Delete(path);
            }
            catch (IOException)
            {
                // 正在写入的图片下一轮再清理。
            }
            catch (UnauthorizedAccessException)
            {
                // 清理失败不影响记录裁剪。
            }
        }
    }

    private bool IsInsideImagesDirectory(string path)
    {
        var root = Path.GetFullPath(ImagesDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(path);
        return candidate.StartsWith(root, AppDataPaths.PathComparison);
    }

    private void TryDeleteImageFiles()
    {
        if (!Directory.Exists(ImagesDirectory)) return;
        foreach (var path in Directory.EnumerateFiles(ImagesDirectory, "*.png"))
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // 清空历史时忽略仍被占用的图片文件。
            }
            catch (UnauthorizedAccessException)
            {
                // 清空历史时忽略无权限的图片文件。
            }
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
