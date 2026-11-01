using System.Diagnostics;
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Principal;

namespace AutoNet.Tray.Avalonia;

/// <summary>Rejected/TimedOut/Cancelled 均为提交确认前拒绝；确认可能已发出后只能返回 OutcomeUnknown。</summary>
internal enum HandoffOutcome { Completed, Rejected, TimedOut, Cancelled, OutcomeUnknown }
internal sealed record HandoffResult(HandoffOutcome Outcome, SettingsSaveOutcome? SaveOutcome = null);
internal sealed record HandoffRequest(Guid Id, int SenderProcessId, long SenderStartedAt);

/// <summary>一次设置保存的凭据交接。双方身份通过后传载荷，接收完整提交确认且复核通过后才调用保存。</summary>
internal sealed class SettingsHandoff(TimeSpan timeout)
{
    private int used;
    private bool commitMayHaveBeenSent;
    private readonly long createdAt = Stopwatch.GetTimestamp();
    private static readonly ConcurrentDictionary<Guid, byte> Received = new();
    private static readonly JsonSerializerOptions PayloadJson = new()
    {
        MaxDepth = 8,
        AllowDuplicateProperties = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true
    };
    public HandoffRequest Request { get; } = new(Guid.NewGuid(), Environment.ProcessId, HandoffPeer.Capture(Environment.ProcessId).StartedAt);

    public async Task<HandoffResult> SendAsync(SettingsDraft draft, HandoffPeer receiver, CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref used, 1) != 0) return new(HandoffOutcome.Rejected);
        try { return await SendCoreAsync(draft, receiver, cancellationToken); }
        catch (OperationCanceledException) { return Abort(cancellationToken.IsCancellationRequested ? HandoffOutcome.Cancelled : HandoffOutcome.TimedOut); }
        catch (IOException) { return Abort(HandoffOutcome.Rejected); }
        catch (InvalidDataException) { return Abort(HandoffOutcome.Rejected); }
        catch (JsonException) { return Abort(HandoffOutcome.Rejected); }
        catch (UnauthorizedAccessException) { return Abort(HandoffOutcome.Rejected); }
        catch (System.ComponentModel.Win32Exception) { return Abort(HandoffOutcome.Rejected); }
    }

    private async Task<HandoffResult> SendCoreAsync(SettingsDraft draft, HandoffPeer receiver, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var remaining = timeout - Stopwatch.GetElapsedTime(createdAt);
        if (remaining <= TimeSpan.Zero) throw new OperationCanceledException();
        deadline.CancelAfter(remaining);
        var ct = deadline.Token;
        using var pipe = new NamedPipeClientStream(".", PipeName(Request),
            PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize, PipeOptions.Asynchronous,
            TokenImpersonationLevel.Identification, HandleInheritability.None);
        await pipe.ConnectAsync(ct);
        if (!receiver.Matches(pipe, server: true)) return new(HandoffOutcome.Rejected);
        await pipe.WriteAsync(Request.Id.ToByteArray(), ct);
        var challenge = await ReadGuidAsync(pipe, ct);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(draft);
        try
        {
            if (bytes.Length > MaximumPayload) return new(HandoffOutcome.Rejected);
            await pipe.WriteAsync(BitConverter.GetBytes(bytes.Length), ct);
            await pipe.WriteAsync(bytes, ct);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
        await ReadIdAsync(pipe, Request.Id, ct);
        ct.ThrowIfCancellationRequested();
        if (!receiver.Matches(pipe, server: true)) return new(HandoffOutcome.Rejected);
        commitMayHaveBeenSent = true;
        await pipe.WriteAsync(challenge.ToByteArray(), ct);
        var result = new byte[1];
        await pipe.ReadExactlyAsync(result, ct);
        return Enum.IsDefined((SettingsSaveOutcome)result[0])
            ? new(HandoffOutcome.Completed, (SettingsSaveOutcome)result[0])
            : new(HandoffOutcome.OutcomeUnknown);
    }

    public static async Task<HandoffResult> ReceiveAsync(HandoffRequest request, HandoffPeer sender,
        Func<SettingsDraft, SettingsSaveResult> save, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (!Received.TryAdd(request.Id, 0)) return new(HandoffOutcome.Rejected);
        try { return await ReceiveCoreAsync(request, sender, save, timeout, cancellationToken); }
        catch (OperationCanceledException) { return new(cancellationToken.IsCancellationRequested ? HandoffOutcome.Cancelled : HandoffOutcome.TimedOut); }
        catch (IOException) { return new(HandoffOutcome.Rejected); }
        catch (InvalidDataException) { return new(HandoffOutcome.Rejected); }
        catch (JsonException) { return new(HandoffOutcome.Rejected); }
        catch (UnauthorizedAccessException) { return new(HandoffOutcome.Rejected); }
        catch (System.ComponentModel.Win32Exception) { return new(HandoffOutcome.Rejected); }
    }

    private static async Task<HandoffResult> ReceiveCoreAsync(HandoffRequest request, HandoffPeer sender,
        Func<SettingsDraft, SettingsSaveResult> save, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var ct = deadline.Token;
        using var pipe = HandoffPipe.Create(PipeName(request), sender.UserSid);
        await pipe.WaitForConnectionAsync(ct);
        if (!sender.Matches(pipe, server: false) || sender.ProcessId != request.SenderProcessId || sender.StartedAt != request.SenderStartedAt)
            return new(HandoffOutcome.Rejected);
        await ReadIdAsync(pipe, request.Id, ct);
        // 新助手即使收到同一公开关联，也只接受本实例的新鲜确认，旧确认帧不能跨进程重放。
        var challenge = Guid.NewGuid();
        await pipe.WriteAsync(challenge.ToByteArray(), ct);
        var length = new byte[4];
        await pipe.ReadExactlyAsync(length, ct);
        var size = BitConverter.ToInt32(length);
        if (size is <= 0 or > MaximumPayload) return new(HandoffOutcome.Rejected);
        var bytes = new byte[size];
        SettingsDraft? draft;
        try
        {
            await pipe.ReadExactlyAsync(bytes, ct);
            draft = JsonSerializer.Deserialize<SettingsDraft>(bytes, PayloadJson);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
        if (draft is null) return new(HandoffOutcome.Rejected);
        await pipe.WriteAsync(request.Id.ToByteArray(), ct);
        await ReadIdAsync(pipe, challenge, ct);
        if (!sender.Matches(pipe, server: false)) return new(HandoffOutcome.Rejected);
        ct.ThrowIfCancellationRequested();
        SettingsSaveOutcome? saved;
        try { saved = save(draft).Outcome; }
        catch (Exception) { saved = null; } // 保存适配器可能已部分写入；异常文本不能进入结果或日志。
        // 保存已开始后不再承诺零写入；回执发送失败不改变接收端已知的保存结果。
        try { await pipe.WriteAsync(new[] { saved is { } outcome ? (byte)outcome : byte.MaxValue }, ct); }
        catch (IOException) { }
        catch (OperationCanceledException) { }
        return saved is { } completed ? new(HandoffOutcome.Completed, completed) : new(HandoffOutcome.OutcomeUnknown);
    }

    internal static string PipeName(HandoffRequest request) => "AutoNet.Settings." + request.Id.ToString("N");
    private const int MaximumPayload = 64 * 1024;

    private HandoffResult Abort(HandoffOutcome outcome) => new(commitMayHaveBeenSent ? HandoffOutcome.OutcomeUnknown : outcome);

    private static async Task ReadIdAsync(PipeStream pipe, Guid expected, CancellationToken ct)
    {
        if (await ReadGuidAsync(pipe, ct) != expected) throw new InvalidDataException();
    }

    private static async Task<Guid> ReadGuidAsync(PipeStream pipe, CancellationToken ct)
    {
        var bytes = new byte[16];
        await pipe.ReadExactlyAsync(bytes, ct);
        var id = new Guid(bytes);
        return id != Guid.Empty ? id : throw new InvalidDataException();
    }
}
