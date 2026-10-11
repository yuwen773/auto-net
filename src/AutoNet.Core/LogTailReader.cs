using System.Text;
using System.Text.Json;

namespace AutoNet.Core;

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
    public static IReadOnlyList<ActivityLog> Read(string path, int maxRecords = DefaultMaxRecords, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var length = file.Length;
        var window = Math.Min(InitialWindow, length);
        IReadOnlyList<ActivityLog> records = [];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var start = length - window;
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
            records = Parse(Split(buffer.AsSpan(0, read), start == 0, endsWithNewline: read > 0 && buffer[read - 1] == (byte)'\n'), cancellationToken);
            if (records.Count >= maxRecords || window >= Math.Min(MaxWindow, length)) break;
            window = Math.Min(MaxWindow, window * 2);
        }
        return records.TakeLast(maxRecords).Reverse().ToArray();
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
    public static IReadOnlyList<ActivityLog> Parse(IEnumerable<string> lines, CancellationToken cancellationToken = default)
    {
        var records = new List<ActivityLog>();
        foreach (var line in lines)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var entry = JsonSerializer.Deserialize<ActivityLog>(line, ActivityLog.Json);
                if (entry is not null) records.Add(entry);
            }
            catch (JsonException) { }
        }
        return records;
    }
}
