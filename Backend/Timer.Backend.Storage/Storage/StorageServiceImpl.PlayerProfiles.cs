using System;
using System.Threading.Tasks;
using Sharp.Shared.Units;

namespace Timer.Backend.Storage;

internal sealed partial class StorageServiceImpl
{
    internal async Task<TimerBackendPlayerProfileResult> EnsureBackendPlayerProfileAsync(
        TimerBackendPlayerProfileCommand command)
    {
        if (command is null)
        {
            throw new TimerBackendPlayerProfileValidationException("Player profile request must not be null.");
        }

        if (command.SteamId <= 0)
        {
            throw new TimerBackendPlayerProfileValidationException("SteamId must be positive.");
        }

        var name = NormalizePlayerName(command.Name);
        var profile = await GetPlayerProfile(new SteamID(checked((ulong)command.SteamId)), name);

        return new TimerBackendPlayerProfileResult
        {
            PlayerId = profile.Id,
            SteamId = command.SteamId,
            Name = profile.Name,
            Points = profile.Points,
            JoinDateUtc = DateTime.SpecifyKind(profile.JoinDate, DateTimeKind.Utc),
            LastSeenDateUtc = DateTime.SpecifyKind(profile.LastSeenDate, DateTimeKind.Utc),
        };
    }

    private static string NormalizePlayerName(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 192 || value != value.Trim())
        {
            throw new TimerBackendPlayerProfileValidationException(
                "Name must contain 1 to 192 characters without surrounding whitespace.");
        }

        foreach (var character in value)
        {
            if (char.IsControl(character))
            {
                throw new TimerBackendPlayerProfileValidationException("Name must not contain control characters.");
            }
        }

        return value;
    }
}
