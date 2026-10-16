using System.Buffers;
using System.Text;
using System.Text.Json;

namespace AutoNet.Core;

/// <summary>
/// 一次受限尾部读取的完整结果：可见记录（最新在前、按条数截断）之外，还披露
/// 读取窗口内无法解析而跳过的记录数与列表是否覆盖当日全部记录，
/// 供界面显式标注读取范围与缺口（迁移工单 #7 / 故事 45、49）。
/// </summary>
public sealed record LogTailRead(IReadOnlyList<ActivityLog> Records, int Skipped, bool Complete);

/// <summary>一次解析的结果：可渲染记录与被跳过的损坏行数。</summary>
public sealed record LogParseResult(IReadOnlyList<ActivityLog> Records, int Skipped);

/// <summary>
/// Reads only the tail of a daily JSONL log. The window is bounded regardless of file size, so a large
/// log can never be pulled into memory in full or block the UI thread for longer than the tail itself.
/// Deliberately free of WinForms types so every boundary case is directly testable.
/// </summary>
public static class LogTailReader
{
    /// <summary>Hard cap on records handed to the UI. Matches the previous display limit.</summary>
    public const int DefaultMaxRecords = 300;

    /// <summary>Bytes read from the end on the first attempt; grows only if too few records come back.</summary>
    private const int InitialWindow = 128 * 1024;
    private const int MaxWindow = 4 * 1024 * 1024;

    /// <summary>Newest first.</summary>
    public static IReadOnlyList<ActivityLog> Read(string path, int maxRecords = DefaultMaxRecords, CancellationToken cancellationToken = default) =>
        ReadDetailed(path, maxRecords, cancellationToken).Records;

    /// <summary>
    /// 与 <see cref="Read"/> 同口径，但额外披露缺口：读取窗口内跳过的损坏记录数，
    /// 以及列表是否覆盖当日全部记录（窗口起点不在文件头、或记录触达上限都视为不完整）。
    /// </summary>
    public static LogTailRead ReadDetailed(string path, int maxRecords = DefaultMaxRecords, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var length = file.Length;
        var window = Math.Min(InitialWindow, length);
        IReadOnlyList<ActivityLog> records = [];
        var skipped = 0;
        var coversFileStart = false;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // 窗口翻倍后可能越过文件头：起点钳到 0，否则 Seek 负偏移会抛“参数错误”。
            var start = Math.Max(0, length - window);
            file.Seek(start, SeekOrigin.Begin);
            var buffer = new byte[length - start];
            var read = 0;
            while (read < buffer.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var chunk = file.Read(buffer, read, buffer.Length - read);
                if (chunk == 0) break;
                read += chunk;
            }
            var parsed = ParseDetailed(Split(buffer.AsSpan(0, read), start == 0, endsWithNewline: read > 0 && buffer[read - 1] == (byte)'\n'), cancellationToken);
            records = parsed.Records;
            skipped = parsed.Skipped;
            coversFileStart = start == 0;
            if (records.Count >= maxRecords || window >= Math.Min(MaxWindow, length)) break;
            window = Math.Min(MaxWindow, window * 2);
        }

        return new LogTailRead(records.TakeLast(maxRecords).Reverse().ToArray(), skipped, coversFileStart && records.Count < maxRecords);
    }

    /// <summary>
    /// Splits a byte window into complete lines. Handles a UTF-8 BOM, CRLF, and — critically — drops the
    /// partial first and last lines that a tail read necessarily produces, so a multi-byte character or a
    /// half-written final record can never become a malformed entry.
    /// </summary>
    public static IReadOnlyList<string> Split(ReadOnlySpan<byte> bytes, bool startsAtFileStart, bool endsWithNewline)
    {
        if (bytes.IsEmpty) return [];
        var body = bytes;
        // Discard cut records before decoding, so an incomplete leading UTF-8 character
        // cannot hide every valid record following it.
        if (!startsAtFileStart)
        {
            var firstNewline = body.IndexOf((byte)'\n');
            if (firstNewline < 0) return [];
            body = body[(firstNewline + 1)..];
        }
        if (!endsWithNewline)
        {
            var lastNewline = body.LastIndexOf((byte)'\n');
            if (lastNewline < 0) return [];
            body = body[..(lastNewline + 1)];
        }
        // A BOM only counts as a BOM at the very start of the file; mid-window it is part of a character.
        if (startsAtFileStart && body.Length >= 3 && body[0] == 0xEF && body[1] == 0xBB && body[2] == 0xBF)
            body = body[3..];

        var text = Encoding.UTF8.GetString(body);
        text = text.Replace("\r\n", "\n");

        var lines = text.Split('\n').ToList();
        // Window did not start at byte 0, so the first line is a fragment of a record we did not fully read.
        // A file still being appended to can end mid-record; only keep whole lines.
        lines.RemoveAll(line => string.IsNullOrWhiteSpace(line) || line.Contains('�'));
        return lines;
    }

    /// <summary>Deserialises whole lines, skipping any record that cannot be read rather than failing the batch.</summary>
    public static IReadOnlyList<ActivityLog> Parse(IEnumerable<string> lines, CancellationToken cancellationToken = default) =>
        ParseDetailed(lines, cancellationToken).Records;

    /// <summary>与 <see cref="Parse"/> 同口径，但统计被跳过的损坏行数（缺口披露依据）。</summary>
    public static LogParseResult ParseDetailed(IEnumerable<string> lines, CancellationToken cancellationToken = default)
    {
        var records = new List<ActivityLog>();
        var skipped = 0;
        foreach (var line in lines)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[]? buffer = null;
            try
            {
                buffer = ArrayPool<byte>.Shared.Rent(Encoding.UTF8.GetMaxByteCount(line.Length));
                var count = Encoding.UTF8.GetBytes(line, buffer);
                var bytes = buffer.AsSpan(0, count);
                var reader = new Utf8JsonReader(bytes);
                if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) { skipped++; continue; }
                // Defaults are for producers; missing external fields are not evidence of a complete record.
                var timeCount = 0;
                var actionCount = 0;
                var detailCount = 0;
                while (reader.Read())
                {
                    if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != 1) continue;
                    // Compare ordinary names without allocations; escaped names retain JSON's normal semantics.
                    var name = reader.ValueIsEscaped ? reader.GetString() : null;
                    if (name is null ? Ascii.EqualsIgnoreCase(reader.ValueSpan, "time"u8) : name.Equals("time", StringComparison.OrdinalIgnoreCase)) timeCount++;
                    else if (name is null ? Ascii.EqualsIgnoreCase(reader.ValueSpan, "action"u8) : name.Equals("action", StringComparison.OrdinalIgnoreCase)) actionCount++;
                    else if (name is null ? Ascii.EqualsIgnoreCase(reader.ValueSpan, "detail"u8) : name.Equals("detail", StringComparison.OrdinalIgnoreCase)) detailCount++;
                    reader.Read();
                    reader.Skip();
                }
                if (timeCount != 1 || actionCount != 1 || detailCount != 1) { skipped++; continue; }
                var entry = JsonSerializer.Deserialize<ActivityLog>(bytes, ActivityLog.Json);
                if (IsRenderable(entry)) records.Add(entry!);
                else skipped++;
            }
            catch (JsonException) { skipped++; }
            finally
            {
                if (buffer is not null) ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        return new LogParseResult(records, skipped);
    }

    private static bool IsRenderable(ActivityLog? entry)
    {
        if (entry is null || entry.Time == default || string.IsNullOrWhiteSpace(entry.Action) || entry.Detail is null) return false;
        if (entry.Diagnostics is not { } data) return true;
        return ValidError(data.Error) && ValidError(data.Authentication?.Error)
            && (data.Probes is null || data.Probes.All(probe => probe is not null
                && !string.IsNullOrWhiteSpace(probe.Target) && !string.IsNullOrWhiteSpace(probe.Failure) && ValidError(probe.Error)));
    }

    private static bool ValidError(DiagnosticError? error) => error is null
        || !string.IsNullOrWhiteSpace(error.Type) && error.CallSites is not null
            && error.CallSites.All(site => !string.IsNullOrWhiteSpace(site));
}
