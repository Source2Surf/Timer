using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Source2Surf.Timer.Common.Entities;
using SqlSugar;

namespace Timer.Backend.Storage;

internal sealed partial class StorageServiceImpl
{
    // Reads the points index from the top; ties share the rank of the first player with those points.
    public async Task<IReadOnlyList<TimerBackendRankedPlayer>> GetTopPlayersAsync(int limit)
    {
        var rows = await _db.Queryable<PlayerEntity>()
                            .Where(x => x.Points > 0)
                            .OrderBy(x => x.Points, OrderByType.Desc)
                            .OrderBy(x => x.SteamId)
                            .Take(limit)
                            .Select(x => new TopPlayerRow { SteamId = x.SteamId, Name = x.Name, Points = x.Points })
                            .ToListAsync(OperationCancellation);

        var result = new List<TimerBackendRankedPlayer>(rows.Count);

        for (var i = 0; i < rows.Count; i++)
        {
            var rank = i > 0 && rows[i].Points == rows[i - 1].Points ? result[i - 1].Rank : i + 1;
            result.Add(new TimerBackendRankedPlayer(unchecked((ulong) rows[i].SteamId), rows[i].Name, rows[i].Points, rank));
        }

        return result;
    }

    // Several players' points ranks from one statement (each counts the players ahead on the points index).
    // A player without points, or without a row, is left out: unranked.
    public async Task<(IReadOnlyDictionary<ulong, int> ranks, int total)> GetPlayersPointsRankAsync(IReadOnlyList<ulong> steamIds)
    {
        var ids = steamIds.Select(x => unchecked((long) x)).Distinct().ToList();

        if (ids.Count == 0)
        {
            return (new Dictionary<ulong, int>(), 0);
        }

        var rows = await _db.Queryable<PlayerEntity>()
                            .Where(me => ids.Contains(me.SteamId) && me.Points > 0)
                            .Select(me => new PlayerRanksRow
                            {
                                SteamId = me.SteamId,
                                Ahead   = SqlFunc.Subqueryable<PlayerEntity>().Where(other => other.Points > me.Points).Count(),
                            })
                            .ToListAsync(OperationCancellation);

        var ranks = rows.ToDictionary(x => unchecked((ulong) x.SteamId), x => x.Ahead + 1);
        var total = await GetRankedPlayerCountAsync();

        return (ranks, (int) System.Math.Max(total, ranks.Count == 0 ? 0 : ranks.Values.Max()));
    }

    // Projections the queries are generated from.
    private sealed class TopPlayerRow
    {
        public long   SteamId { get; set; }
        public string Name    { get; set; } = string.Empty;
        public uint   Points  { get; set; }
    }

    private sealed class PlayerRanksRow
    {
        public long SteamId { get; set; }
        public int  Ahead   { get; set; }
    }
}
