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
        // The model's producer-side default is "now"; an absent external timestamp is not evidence.
        if (status is not null && !document.RootElement.EnumerateObject()
            .Any(p => p.Name.Equals(nameof(StatusSnapshot.UpdatedAt), StringComparison.OrdinalIgnoreCase)))
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
