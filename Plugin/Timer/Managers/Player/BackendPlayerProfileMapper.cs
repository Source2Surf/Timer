using System;
using Sharp.Shared.Units;
using Source2Surf.Timer.Backend.Rpc.Contracts;
using Source2Surf.Timer.Shared.Models;

namespace Source2Surf.Timer.Managers.Player;

/// <summary>
/// Converts the scalar backend profile projection into the plugin's game-facing model.
/// Responses are validated at this boundary so a malformed or mismatched backend response
/// cannot attach a profile to the wrong player session.
/// </summary>
internal static class BackendPlayerProfileMapper
{
    private const int MaximumNameLength = 192;

    public static PlayerProfile ToProfile(EnsurePlayerProfileResponse response,
                                           SteamID                       requestedSteamId)
    {
        ArgumentNullException.ThrowIfNull(response);

        var requestedValue = requestedSteamId.AsPrimitive();
        if (requestedValue == 0 || requestedValue > long.MaxValue)
        {
            throw new InvalidOperationException("The requested SteamID cannot be represented by the profile contract.");
        }

        if (response.PlayerId <= 0)
        {
            throw new InvalidOperationException("The backend returned an invalid player id.");
        }

        if (response.SteamId <= 0 || response.SteamId != (long) requestedValue)
        {
            throw new InvalidOperationException("The backend profile SteamID does not match the requested player.");
        }

        ValidateName(response.Name);

        var joinDate     = FromUnixMilliseconds(response.JoinDateUnixTimeMilliseconds, nameof(response.JoinDateUnixTimeMilliseconds));
        var lastSeenDate = FromUnixMilliseconds(response.LastSeenDateUnixTimeMilliseconds, nameof(response.LastSeenDateUnixTimeMilliseconds));

        var profile = new PlayerProfile
        {
            Id           = response.PlayerId,
            SteamId      = requestedSteamId,
            Points       = response.Points,
            JoinDate     = joinDate,
            LastSeenDate = lastSeenDate,
        };
        profile.UpdateName(response.Name);

        return profile;
    }

    private static void ValidateName(string? name)
    {
        if (string.IsNullOrEmpty(name)
            || name.Length > MaximumNameLength
            || name != name.Trim())
        {
            throw new InvalidOperationException("The backend returned an invalid player name.");
        }

        foreach (var character in name)
        {
            if (char.IsControl(character))
            {
                throw new InvalidOperationException("The backend returned a player name containing control characters.");
            }
        }
    }

    private static DateTime FromUnixMilliseconds(long value, string fieldName)
    {
        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(value).UtcDateTime;
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new InvalidOperationException($"The backend returned an invalid {fieldName}.", exception);
        }
    }
}
