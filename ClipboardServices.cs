using Microsoft.Data.Sqlite;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Clipboard = System.Windows.Clipboard;
using DataFormats = System.Windows.DataFormats;
using DataObject = System.Windows.DataObject;

namespace SnapTranslate;

public sealed record ClipboardSearchResult(IReadOnlyList<ClipboardItem> Items, int TotalCount);

public sealed class ClipboardRepository : IDisposable
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _mutationGate = new(1, 1);
    private readonly SqliteConnection _connection;

    public ClipboardRepository(string? dataDirectory = null)
    {
        DataDirectory = dataDirectory ?? Path.Combine(AppDataPaths.ApplicationDataDirectory, "Data");
        if (dataDirectory is null) MigrateLegacyData(DataDirectory);
        ImagesDirectory = Path.Combine(DataDirectory, "clipboard-images");
        Directory.CreateDirectory(ImagesDirectory);
        var databasePath = Path.Combine(DataDirectory, "clipboard.db");
        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private
        }.ToString());
        _connection.Open();
        Initialize();
    }

    private static void MigrateLegacyData(string destinationDirectory)
    {
        var legacyDirectory = Path.Combine(AppContext.BaseDirectory, "Data");
        var legacyDatabase = Path.Combine(legacyDirectory, "clipboard.db");
        var destinationDatabase = Path.Combine(destinationDirectory, "clipboard.db");
        if (!File.Exists(legacyDatabase) || File.Exists(destinationDatabase)) return;

        try
        {
            Directory.CreateDirectory(destinationDirectory);
            using (var source = new SqliteConnection(new SqliteConnectionStringBuilder
                   {
                       DataSource = legacyDatabase,
                       Mode = SqliteOpenMode.ReadOnly
                   }.ToString()))
            using (var destination = new SqliteConnection(new SqliteConnectionStringBuilder
                   {
                       DataSource = destinationDatabase,
                       Mode = SqliteOpenMode.ReadWriteCreate
                   }.ToString()))
            {
                source.Open();
                destination.Open();
                source.BackupDatabase(destination);
            }

            var legacyImages = Path.Combine(legacyDirectory, "clipboard-images");
            var destinationImages = Path.Combine(destinationDirectory, "clipboard-images");
            if (Directory.Exists(legacyImages))
            {
                Directory.CreateDirectory(destinationImages);
                foreach (var path in Directory.EnumerateFiles(legacyImages))
                {
                    var destination = Path.Combine(destinationImages, Path.GetFileName(path));
                    if (!File.Exists(destination)) File.Copy(path, destination);
                }
            }

            using var migrated = new SqliteConnection($"Data Source={destinationDatabase}");
            migrated.Open();
            using var update = migrated.CreateCommand();
            update.CommandText = """
                UPDATE clipboard_items
                SET value = $newRoot || substr(value, length($oldRoot) + 1)
                WHERE kind = 'Image' AND instr(value, $oldRoot) = 1
                """;
            update.Parameters.AddWithValue("$oldRoot", legacyImages);
            update.Parameters.AddWithValue("$newRoot", destinationImages);
            update.ExecuteNonQuery();
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("无法迁移旧剪贴板数据：{0}", ex.Message);
            try
            {
                if (File.Exists(destinationDatabase)) File.Delete(destinationDatabase);
            }
            catch (Exception cleanupError) when (cleanupError is IOException or UnauthorizedAccessException)
            {
                Trace.TraceWarning("无法清理未完成的迁移数据库：{0}", cleanupError.Message);
            }
        }
    }

    public string DataDirectory { get; }
    public string ImagesDirectory { get; }

    private void Initialize()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;
            CREATE TABLE IF NOT EXISTS clipboard_items (
                id TEXT PRIMARY KEY,
                kind TEXT NOT NULL,
                subtype TEXT,
                value TEXT NOT NULL,
                search_text TEXT,
                note TEXT,
                size INTEGER NOT NULL DEFAULT 0,
                width INTEGER NOT NULL DEFAULT 0,
                height INTEGER NOT NULL DEFAULT 0,
                use_count INTEGER NOT NULL DEFAULT 1,
                is_favorite INTEGER NOT NULL DEFAULT 0,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                content_hash TEXT NOT NULL UNIQUE
            );
            CREATE INDEX IF NOT EXISTS idx_clipboard_items_updated ON clipboard_items(updated_at DESC);
            CREATE INDEX IF NOT EXISTS idx_clipboard_items_kind ON clipboard_items(kind);
            CREATE INDEX IF NOT EXISTS idx_clipboard_items_favorite ON clipboard_items(is_favorite);
            CREATE INDEX IF NOT EXISTS idx_clipboard_items_favorite_updated
                ON clipboard_items(is_favorite DESC, updated_at DESC);
            CREATE INDEX IF NOT EXISTS idx_clipboard_items_kind_favorite_updated
                ON clipboard_items(kind, is_favorite DESC, updated_at DESC);
            """;
        command.ExecuteNonQuery();
    }

    public Task<ClipboardSearchResult> SearchWithCountAsync(
        string? keyword,
        ClipboardFilter filter,
        int limit = 300,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            lock (_gate)
            {
                // Recheck after entering the database gate so canceled searches do not
                // execute later when rapid filter or keyword changes have queued them.
                cancellationToken.ThrowIfCancellationRequested();
                var items = Search(keyword, filter, limit);
                cancellationToken.ThrowIfCancellationRequested();
                return new ClipboardSearchResult(items, Count());
            }
        }, cancellationToken);
    }

    public IReadOnlyList<ClipboardItem> Search(string? keyword, ClipboardFilter filter, int limit = 300)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            var clauses = new List<string>();
            switch (filter)
            {
                case ClipboardFilter.Text:
                    clauses.Add("kind IN ('Text', 'Html', 'Rtf')");
                    break;
                case ClipboardFilter.Image:
                    clauses.Add("kind = 'Image'");
                    break;
                case ClipboardFilter.Files:
                    clauses.Add("kind = 'Files'");
                    break;
                case ClipboardFilter.Favorite:
                    clauses.Add("is_favorite = 1");
                    break;
            }
            if (!string.IsNullOrWhiteSpace(keyword))
            {
                clauses.Add("(search_text LIKE $keyword OR note LIKE $keyword)");
                command.Parameters.AddWithValue("$keyword", $"%{keyword.Trim()}%");
            }
            var where = clauses.Count == 0 ? string.Empty : " WHERE " + string.Join(" AND ", clauses);
            command.CommandText = SelectColumns + where
                + " ORDER BY is_favorite DESC, updated_at DESC LIMIT $limit";
            command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 1000));
            using var reader = command.ExecuteReader();
            var result = new List<ClipboardItem>();
            while (reader.Read()) result.Add(ReadItem(reader));
            return result;
        }
    }

    public int Count()
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM clipboard_items";
            return Convert.ToInt32(command.ExecuteScalar());
        }
    }

    public ClipboardItem Upsert(ClipboardCapture capture, int maxItems, int retentionDays)
    {
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow.ToString("O");
            using var command = _connection.CreateCommand();
            command.CommandText = """
                INSERT INTO clipboard_items
                    (id, kind, subtype, value, search_text, note, size, width, height,
                     use_count, is_favorite, created_at, updated_at, content_hash)
                VALUES
                    ($id, $kind, $subtype, $value, $search, '', $size, $width, $height,
                     1, 0, $now, $now, $hash)
                ON CONFLICT(content_hash) DO UPDATE SET
                    subtype = excluded.subtype,
                    search_text = CASE WHEN excluded.search_text = '' THEN clipboard_items.search_text ELSE excluded.search_text END,
                    size = excluded.size,
                    width = excluded.width,
                    height = excluded.height,
                    use_count = clipboard_items.use_count + 1,
                    updated_at = excluded.updated_at;
                """;
            command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
            command.Parameters.AddWithValue("$kind", capture.Kind.ToString());
            command.Parameters.AddWithValue("$subtype", capture.Subtype);
            command.Parameters.AddWithValue("$value", capture.Value);
            command.Parameters.AddWithValue("$search", capture.SearchText);
            command.Parameters.AddWithValue("$size", capture.Size);
            command.Parameters.AddWithValue("$width", capture.Width);
            command.Parameters.AddWithValue("$height", capture.Height);
            command.Parameters.AddWithValue("$now", now);
            command.Parameters.AddWithValue("$hash", capture.ContentHash);
            command.ExecuteNonQuery();
            ApplyRetention(maxItems, retentionDays);
            return GetByHash(capture.ContentHash) ?? throw new InvalidOperationException("剪贴板记录写入失败。");
        }
    }

    public void Touch(string id)
    {
        ExecuteUpdate("UPDATE clipboard_items SET use_count = use_count + 1, updated_at = $now WHERE id = $id",
            ("$id", id), ("$now", DateTimeOffset.UtcNow.ToString("O")));
    }

    public Task TouchAsync(string id, CancellationToken cancellationToken = default)
        => Task.Run(() => Touch(id), cancellationToken);

    public void ToggleFavorite(ClipboardItem item)
    {
        var isFavorite = !item.IsFavorite;
        ExecuteUpdate("UPDATE clipboard_items SET is_favorite = $favorite WHERE id = $id",
            ("$id", item.Id), ("$favorite", isFavorite ? 1 : 0));
        item.IsFavorite = isFavorite;
    }

    public async Task SetFavoriteAsync(string id, bool isFavorite)
    {
        await _mutationGate.WaitAsync();
        try
        {
            await Task.Run(() => ExecuteUpdate(
                "UPDATE clipboard_items SET is_favorite = $favorite WHERE id = $id",
                ("$id", id), ("$favorite", isFavorite ? 1 : 0)));
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    public void UpdateNote(ClipboardItem item, string note)
    {
        var normalized = note.Trim();
        ExecuteUpdate("UPDATE clipboard_items SET note = $note WHERE id = $id",
            ("$id", item.Id), ("$note", normalized));
        item.Note = normalized;
    }

    public async Task SetNoteAsync(string id, string note)
    {
        var normalized = note.Trim();
        await _mutationGate.WaitAsync();
        try
        {
            await Task.Run(() => ExecuteUpdate(
                "UPDATE clipboard_items SET note = $note WHERE id = $id",
                ("$id", id), ("$note", normalized)));
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    public void UpdateOcr(ClipboardItem item, string text)
    {
        item.SearchText = text.Trim();
        ExecuteUpdate("UPDATE clipboard_items SET search_text = $search WHERE id = $id",
            ("$id", item.Id), ("$search", item.SearchText));
    }

    public void Delete(ClipboardItem item)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "DELETE FROM clipboard_items WHERE id = $id";
            command.Parameters.AddWithValue("$id", item.Id);
            command.ExecuteNonQuery();
            DeleteOwnedImage(item.Kind, item.Value);
        }
    }

    private void ApplyRetention(int maxItems, int retentionDays)
    {
        var doomed = new List<(ClipboardItemKind Kind, string Value)>();
        using (var select = _connection.CreateCommand())
        {
            var conditions = new List<string>();
            if (retentionDays > 0)
            {
                conditions.Add("(is_favorite = 0 AND updated_at < $cutoff)");
                select.Parameters.AddWithValue("$cutoff", DateTimeOffset.UtcNow.AddDays(-retentionDays).ToString("O"));
            }
            if (maxItems > 0)
            {
                conditions.Add("id IN (SELECT id FROM clipboard_items WHERE is_favorite = 0 ORDER BY updated_at DESC LIMIT -1 OFFSET $max)");
                select.Parameters.AddWithValue("$max", maxItems);
            }
            if (conditions.Count == 0) return;
            select.CommandText = "SELECT kind, value FROM clipboard_items WHERE " + string.Join(" OR ", conditions);
            using var reader = select.ExecuteReader();
            while (reader.Read())
                doomed.Add((Enum.Parse<ClipboardItemKind>(reader.GetString(0)), reader.GetString(1)));
        }
        using (var delete = _connection.CreateCommand())
        {
            var conditions = new List<string>();
            if (retentionDays > 0)
            {
                conditions.Add("(is_favorite = 0 AND updated_at < $cutoff)");
                delete.Parameters.AddWithValue("$cutoff", DateTimeOffset.UtcNow.AddDays(-retentionDays).ToString("O"));
            }
            if (maxItems > 0)
            {
                conditions.Add("id IN (SELECT id FROM clipboard_items WHERE is_favorite = 0 ORDER BY updated_at DESC LIMIT -1 OFFSET $max)");
                delete.Parameters.AddWithValue("$max", maxItems);
            }
            delete.CommandText = "DELETE FROM clipboard_items WHERE " + string.Join(" OR ", conditions);
            delete.ExecuteNonQuery();
        }
        foreach (var item in doomed) DeleteOwnedImage(item.Kind, item.Value);
    }

    private ClipboardItem? GetByHash(string hash)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = SelectColumns + " WHERE content_hash = $hash LIMIT 1";
        command.Parameters.AddWithValue("$hash", hash);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadItem(reader) : null;
    }

    private void ExecuteUpdate(string sql, params (string Name, object Value)[] parameters)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = sql;
            foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
            command.ExecuteNonQuery();
        }
    }

    private void DeleteOwnedImage(ClipboardItemKind kind, string path)
    {
        if (kind != ClipboardItemKind.Image || !File.Exists(path)) return;
        var root = Path.GetFullPath(ImagesDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(path);
        if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return;
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM clipboard_items WHERE kind = 'Image' AND value = $value";
        command.Parameters.AddWithValue("$value", path);
        if (Convert.ToInt32(command.ExecuteScalar()) == 0) File.Delete(path);
    }

    public static string HashContent(ClipboardItemKind kind, string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(kind + "\0" + value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static ClipboardItem ReadItem(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(0),
        Kind = Enum.Parse<ClipboardItemKind>(reader.GetString(1)),
        Subtype = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
        Value = reader.GetString(3),
        SearchText = reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
        Note = reader.IsDBNull(5) ? string.Empty : reader.GetString(5),
        Size = reader.GetInt64(6),
        Width = reader.GetInt32(7),
        Height = reader.GetInt32(8),
        UseCount = reader.GetInt32(9),
        IsFavorite = reader.GetInt32(10) != 0,
        CreatedAt = DateTimeOffset.Parse(reader.GetString(11)),
        UpdatedAt = DateTimeOffset.Parse(reader.GetString(12)),
        ContentHash = reader.GetString(13)
    };

    private const string SelectColumns = """
        SELECT id, kind, subtype, value, search_text, note, size, width, height,
               use_count, is_favorite, created_at, updated_at, content_hash
        FROM clipboard_items
        """;

    public void Dispose()
    {
        lock (_gate) _connection.Dispose();
    }
}

public static class ClipboardCaptureService
{
    public static async Task<ClipboardCapture?> ReadAsync(
        string imagesDirectory,
        bool recognizeImage,
        OcrService ocr)
    {
        ExternalException? lastError = null;
        for (var attempt = 0; attempt < 6; attempt++)
        {
            try
            {
                return await ReadOnceAsync(imagesDirectory, recognizeImage, ocr);
            }
            catch (ExternalException ex)
            {
                lastError = ex;
                if (attempt < 5) await Task.Delay(25 + attempt * 35);
            }
        }

        throw new InvalidOperationException("剪贴板正被其他程序占用，请稍后重试。", lastError);
    }

    private static async Task<ClipboardCapture?> ReadOnceAsync(
        string imagesDirectory,
        bool recognizeImage,
        OcrService ocr)
    {
        var data = Clipboard.GetDataObject();
        if (data is null) return null;

        if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
        {
            var normalized = files.Select(Path.GetFullPath).ToArray();
            var value = JsonSerializer.Serialize(normalized);
            var search = string.Join(" ", normalized.Select(Path.GetFileName));
            var size = normalized.Sum(path => File.Exists(path) ? new FileInfo(path).Length : 0L);
            return Create(ClipboardItemKind.Files, value, search, string.Empty, size, 0, 0);
        }

        var hasText = data.GetDataPresent(DataFormats.UnicodeText) || data.GetDataPresent(DataFormats.Text);
        if (!hasText && data.GetDataPresent(DataFormats.Bitmap) && data.GetData(DataFormats.Bitmap) is BitmapSource image)
        {
            image.Freeze();
            var encoded = await Task.Run(() =>
            {
                var bytes = ScreenshotService.ToPng(image);
                var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                return (Bytes: bytes, Hash: hash);
            });
            Directory.CreateDirectory(imagesDirectory);
            var path = Path.Combine(imagesDirectory, encoded.Hash + ".png");
            if (!File.Exists(path)) await File.WriteAllBytesAsync(path, encoded.Bytes);
            var search = string.Empty;
            if (recognizeImage)
            {
                try { search = await ocr.RecognizeAsync(image); }
                catch (Exception ex) { Trace.TraceWarning("剪贴板图片 OCR 失败：{0}", ex.Message); }
            }
            return new ClipboardCapture(ClipboardItemKind.Image, path, search, string.Empty,
                encoded.Bytes.LongLength, image.PixelWidth, image.PixelHeight, "image:" + encoded.Hash);
        }

        var plain = ReadString(data, DataFormats.UnicodeText);
        if (data.GetDataPresent(DataFormats.Html))
        {
            var html = ReadString(data, DataFormats.Html);
            if (!string.IsNullOrEmpty(html)) return Create(ClipboardItemKind.Html, html, plain, string.Empty, html.Length, 0, 0);
        }
        if (data.GetDataPresent(DataFormats.Rtf))
        {
            var rtf = ReadString(data, DataFormats.Rtf);
            if (!string.IsNullOrEmpty(rtf)) return Create(ClipboardItemKind.Rtf, rtf, plain, string.Empty, rtf.Length, 0, 0);
        }
        if (!string.IsNullOrEmpty(plain))
            return Create(ClipboardItemKind.Text, plain, plain, DetectSubtype(plain), plain.Length, 0, 0);
        return null;
    }

    private static string ReadString(System.Windows.IDataObject data, string format)
        => data.GetDataPresent(format) ? data.GetData(format)?.ToString() ?? string.Empty : string.Empty;

    private static ClipboardCapture Create(
        ClipboardItemKind kind, string value, string search, string subtype, long size, int width, int height)
        => new(kind, value, search, subtype, size, width, height, ClipboardRepository.HashContent(kind, value));

    private static string DetectSubtype(string text)
    {
        var value = text.Trim();
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https") return "url";
        if (Regex.IsMatch(value, @"^[^\s@]+@[^\s@]+\.[^\s@]+$")) return "email";
        if (Regex.IsMatch(value, @"^(#(?:[0-9a-fA-F]{3}|[0-9a-fA-F]{6}|[0-9a-fA-F]{8})|rgba?\(.+\))$")) return "color";
        if ((File.Exists(value) || Directory.Exists(value)) && value.IndexOfAny(Path.GetInvalidPathChars()) < 0) return "path";
        return string.Empty;
    }
}

public static class ClipboardWriteService
{
    public static Task WriteAsync(ClipboardItem item, bool plain)
        => ClipboardService.ExecuteWriteAsync(() => WriteCoreAsync(item, plain));

    private static async Task WriteCoreAsync(ClipboardItem item, bool plain)
    {
        if (plain || item.Kind == ClipboardItemKind.Text)
        {
            var text = GetPlainText(item);
            if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("该条目没有可粘贴的文本。");
            await NativeTextClipboard.WriteAsync(text);
            return;
        }

        if (item.Kind == ClipboardItemKind.Image)
        {
            var image = await Task.Run(() => LoadImage(item.Value));
            await NativeImageClipboard.WriteAsync(image);
            return;
        }

        var data = new DataObject();
        switch (item.Kind)
        {
            case ClipboardItemKind.Html:
                data.SetData(DataFormats.UnicodeText, item.SearchText);
                data.SetData(DataFormats.Html, item.Value);
                break;
            case ClipboardItemKind.Rtf:
                data.SetData(DataFormats.UnicodeText, item.SearchText);
                data.SetData(DataFormats.Rtf, item.Value);
                break;
            case ClipboardItemKind.Files:
                var paths = new StringCollection();
                paths.AddRange(JsonSerializer.Deserialize<string[]>(item.Value) ?? Array.Empty<string>());
                data.SetFileDropList(paths);
                break;
            default:
                data.SetData(DataFormats.UnicodeText, item.Value);
                break;
        }

        COMException? lastError = null;
        for (var attempt = 0; attempt < 6; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(data, false);
                try
                {
                    Clipboard.Flush();
                }
                catch (COMException)
                {
                    // The data object is already available. A listener can briefly block Flush, so
                    // do not turn a persistence failure into an immediate paste failure.
                }
                return;
            }
            catch (COMException ex)
            {
                lastError = ex;
                if (attempt < 5) await Task.Delay(40 + attempt * 50);
            }
        }
        throw new InvalidOperationException("剪贴板正被其他程序占用，请稍后重试。", lastError);
    }

    private static string GetPlainText(ClipboardItem item) => item.Kind switch
    {
        ClipboardItemKind.Files => string.Join(Environment.NewLine,
            JsonSerializer.Deserialize<string[]>(item.Value) ?? Array.Empty<string>()),
        ClipboardItemKind.Text => item.Value,
        _ => item.SearchText
    };

    private static BitmapSource LoadImage(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("图片文件已不存在。", path);
        using var stream = File.OpenRead(path);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }
}

public static class NativeTextClipboard
{
    private const uint CfUnicodeText = 13;
    private const uint GmemMoveable = 0x0002;
    private const int MaxAttempts = 8;

    public static async Task WriteAsync(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            if (TryWrite(text)) return;
            if (attempt + 1 < MaxAttempts) await Task.Delay(12 + attempt * 15);
        }
        throw new InvalidOperationException("剪贴板正被其他程序占用，请稍后重试。");
    }

    private static bool TryWrite(string text)
    {
        if (!OpenClipboard(IntPtr.Zero)) return false;
        IntPtr memory = IntPtr.Zero;
        try
        {
            if (!EmptyClipboard()) return false;
            var bytes = Encoding.Unicode.GetBytes(text + '\0');
            memory = GlobalAlloc(GmemMoveable, (UIntPtr)bytes.Length);
            if (memory == IntPtr.Zero) throw new OutOfMemoryException("无法分配剪贴板内存。");
            var pointer = GlobalLock(memory);
            if (pointer == IntPtr.Zero) throw new InvalidOperationException("无法锁定剪贴板内存。");
            try { Marshal.Copy(bytes, 0, pointer, bytes.Length); }
            finally { GlobalUnlock(memory); }
            if (SetClipboardData(CfUnicodeText, memory) == IntPtr.Zero) return false;
            memory = IntPtr.Zero;
            return true;
        }
        finally
        {
            CloseClipboard();
            if (memory != IntPtr.Zero) GlobalFree(memory);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr owner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint format, IntPtr memory);

    [DllImport("user32.dll")]
    private static extern bool CloseClipboard();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr memory);

    [DllImport("kernel32.dll")]
    private static extern bool GlobalUnlock(IntPtr memory);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalFree(IntPtr memory);
}

public static class NativeImageClipboard
{
    private const uint CfDib = 8;
    private const uint GmemMoveable = 0x0002;
    private const int BitmapInfoHeaderSize = 40;
    private const int MaxAttempts = 8;

    // 只写 CF_DIB 时，微信 / QQ / 浏览器这类应用有时挑不到自己认的图片格式，
    // 会把剪贴板内容当成一段未知数据（表现成"粘贴出来是个文件"）。
    // 这里同时提供 24 位 DIB（兼容老程序）和 PNG（浏览器、IM、Office 都优先读它）。
    private static readonly uint PngClipboardFormat = RegisterClipboardFormat("PNG");
    private static readonly uint PngMimeClipboardFormat = RegisterClipboardFormat("image/png");

    public static async Task WriteAsync(BitmapSource image)
    {
        ArgumentNullException.ThrowIfNull(image);
        var payloads = CreatePayloads(image);
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            if (TryWrite(payloads)) return;
            if (attempt + 1 < MaxAttempts) await Task.Delay(12 + attempt * 15);
        }
        throw new InvalidOperationException("剪贴板正被其他程序占用，请稍后重试。");
    }

    private static List<(uint Format, byte[] Bytes)> CreatePayloads(BitmapSource image)
    {
        var png = ScreenshotService.ToPng(image);
        var payloads = new List<(uint Format, byte[] Bytes)> { (CfDib, CreateDib(image)) };
        if (PngClipboardFormat != 0) payloads.Add((PngClipboardFormat, png));
        if (PngMimeClipboardFormat != 0) payloads.Add((PngMimeClipboardFormat, png));
        return payloads;
    }

    /// <summary>24 位 BGR 自下而上的 DIB。截图没有透明通道，去掉 alpha 更不容易被别的程序画成黑底。</summary>
    private static byte[] CreateDib(BitmapSource image)
    {
        var formatted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        var width = formatted.PixelWidth;
        var height = formatted.PixelHeight;
        var sourceStride = checked(width * 4);
        var source = new byte[checked(sourceStride * height)];
        formatted.CopyPixels(source, sourceStride, 0);

        var stride = checked(((width * 3) + 3) / 4 * 4);
        var pixels = new byte[checked(stride * height)];
        var dib = new byte[checked(BitmapInfoHeaderSize + pixels.Length)];
        WriteInt32(dib, 0, BitmapInfoHeaderSize);
        WriteInt32(dib, 4, width);
        WriteInt32(dib, 8, height);
        WriteInt16(dib, 12, 1);
        WriteInt16(dib, 14, 24);
        WriteInt32(dib, 16, 0);
        WriteInt32(dib, 20, pixels.Length);
        WriteInt32(dib, 24, (int)Math.Round(Math.Max(1, image.DpiX) * 39.3701));
        WriteInt32(dib, 28, (int)Math.Round(Math.Max(1, image.DpiY) * 39.3701));

        for (var y = 0; y < height; y++)
        {
            var sourceOffset = y * sourceStride;
            var destinationOffset = (height - 1 - y) * stride;
            for (var x = 0; x < width; x++)
            {
                var from = sourceOffset + x * 4;
                var to = destinationOffset + x * 3;
                pixels[to] = source[from];
                pixels[to + 1] = source[from + 1];
                pixels[to + 2] = source[from + 2];
            }
        }
        Buffer.BlockCopy(pixels, 0, dib, BitmapInfoHeaderSize, pixels.Length);
        return dib;
    }

    private static bool TryWrite(IReadOnlyList<(uint Format, byte[] Bytes)> payloads)
    {
        if (!OpenClipboard(IntPtr.Zero)) return false;
        var owned = new List<IntPtr>();
        try
        {
            if (!EmptyClipboard()) return false;
            foreach (var (format, bytes) in payloads)
            {
                var memory = GlobalAlloc(GmemMoveable, (UIntPtr)bytes.Length);
                if (memory == IntPtr.Zero) throw new OutOfMemoryException("无法分配剪贴板图像内存。");
                owned.Add(memory);
                var pointer = GlobalLock(memory);
                if (pointer == IntPtr.Zero) throw new InvalidOperationException("无法锁定剪贴板图像内存。");
                try { Marshal.Copy(bytes, 0, pointer, bytes.Length); }
                finally { GlobalUnlock(memory); }

                if (SetClipboardData(format, memory) == IntPtr.Zero)
                {
                    if (format != CfDib) continue; // PNG 这类补充格式失败不影响 DIB 已经写入的图片
                    return false;
                }
                owned.Remove(memory); // 交给系统后不能再释放
            }
            return true;
        }
        finally
        {
            CloseClipboard();
            foreach (var memory in owned) GlobalFree(memory);
        }
    }

    private static void WriteInt16(byte[] buffer, int offset, short value)
    {
        buffer[offset] = (byte)value;
        buffer[offset + 1] = (byte)(value >> 8);
    }

    private static void WriteInt32(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)value;
        buffer[offset + 1] = (byte)(value >> 8);
        buffer[offset + 2] = (byte)(value >> 16);
        buffer[offset + 3] = (byte)(value >> 24);
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint RegisterClipboardFormat(string format);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr owner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint format, IntPtr memory);

    [DllImport("user32.dll")]
    private static extern bool CloseClipboard();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr memory);

    [DllImport("kernel32.dll")]
    private static extern bool GlobalUnlock(IntPtr memory);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalFree(IntPtr memory);
}

public static class PasteService
{
    internal static int NativeInputSize => Marshal.SizeOf<Input>();

    public static async Task PasteAsync(IntPtr targetWindow)
    {
        if (targetWindow == IntPtr.Zero) return;
        if (!SetForegroundWindow(targetWindow)) return;
        await Task.Delay(90);
        if (GetForegroundWindow() != targetWindow) return;
        var inputs = new[]
        {
            KeyInput(0x11, 0),
            KeyInput(0x56, 0),
            KeyInput(0x56, KeyeventfKeyup),
            KeyInput(0x11, KeyeventfKeyup)
        };
        var inserted = SendInput((uint)inputs.Length, inputs, NativeInputSize);
        if (inserted == (uint)inputs.Length) return;

        var error = Marshal.GetLastWin32Error();
        var releases = new[]
        {
            KeyInput(0x56, KeyeventfKeyup),
            KeyInput(0x11, KeyeventfKeyup)
        };
        _ = SendInput((uint)releases.Length, releases, NativeInputSize);
        throw new InvalidOperationException($"自动粘贴失败（Win32 {error}），内容已保留在剪贴板中。");
    }

    private static Input KeyInput(ushort key, uint flags) => new()
    {
        Type = InputKeyboard,
        Union = new InputUnion
        {
            Keyboard = new KeyboardInput
            {
                VirtualKey = key,
                Flags = flags,
                ExtraInfo = SyntheticKeyboardInputMarker.Value
            }
        }
    };

    private const uint InputKeyboard = 1;
    private const uint KeyeventfKeyup = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Union;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint numberOfInputs, Input[] inputs, int sizeOfInput);
}
