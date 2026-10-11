using System.Text.Json;

namespace AutoNet.Core;

/// <summary>Validates external status data before any display or timer callback consumes it.</summary>
public static class StatusSnapshotReader
{
    public static StatusSnapshot? Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind == JsonValueKind.Null) return null;
        var status = document.RootElement.Deserialize<StatusSnapshot>(Storage.Json);
        // Producer-side defaults are not evidence: external timestamps and recovery settings must be explicit.
        if (status is not null && (!document.RootElement.EnumerateObject()
            .Any(p => p.Name.Equals(nameof(StatusSnapshot.UpdatedAt), StringComparison.OrdinalIgnoreCase))
            || !document.RootElement.EnumerateObject()
            .Any(p => p.Name.Equals(nameof(StatusSnapshot.Enabled), StringComparison.OrdinalIgnoreCase))))
            status = status with { UpdatedAt = default };
        return IsValid(status) ? status : new StatusSnapshot { UpdatedAt = default, State = "状态不可确认" };
    }

    public static bool IsValid(StatusSnapshot? status) => status is { } s && s.UpdatedAt != default
        && !string.IsNullOrWhiteSpace(s.State) && !string.IsNullOrWhiteSpace(s.Wifi)
        && !string.IsNullOrWhiteSpace(s.Direct) && !string.IsNullOrWhiteSpace(s.Portal)
        && !string.IsNullOrWhiteSpace(s.Tunnel) && !string.IsNullOrWhiteSpace(s.Uu)
        && !string.IsNullOrWhiteSpace(s.AuthenticationStage) && !string.IsNullOrWhiteSpace(s.AuthenticationFailure)
        && !string.IsNullOrWhiteSpace(s.AuthenticationPersistentState);
}
