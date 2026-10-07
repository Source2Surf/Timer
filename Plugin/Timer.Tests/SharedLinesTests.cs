using Sharp.Shared.Types;
using Sharp.Shared.Units;
using Source2Surf.Timer.Modules.Zone;
using Source2Surf.Timer.Types;
using Xunit;

namespace Timer.Tests;

// Zone lines shared by everyone who has them: created once for a mask of players, each look sent once to all who share
// it, a look change only to whoever changed, and nothing to a player who left.
public sealed class SharedLinesTests
{
    private const uint Zone = 7;

    private static readonly Edge Bottom = new (new Vector(0, 0, 0), new Vector(10, 0, 0));
    private static readonly Edge Top    = new (new Vector(0, 0, 50), new Vector(10, 0, 50));

    private static readonly LineLook Green = new (new Vector(0, 255, 0), 1);
    private static readonly LineLook Red   = new (new Vector(255, 0, 0), 1);
    private static readonly LineLook Thick = Green with { Radius = 4 };

    private static readonly PlayerSlot A = (PlayerSlot) 0;
    private static readonly PlayerSlot B = (PlayerSlot) 1;
    private static readonly PlayerSlot C = (PlayerSlot) 5;

    private sealed record Send(Edge Edge, int Index, ulong Gone, ulong Added, string Styles);

    private static void Draw(SharedLines lines, PlayerSlot slot, LineLook look, params Edge[] edges)
    {
        var mark = lines.Begin();

        if (edges.Length > 0)
        {
            lines.Want(slot, mark, Zone, look, [.. edges]);
        }

        lines.End(slot, mark);
    }

    private static List<Send> Sent(SharedLines lines)
    {
        var sent = new List<Send>();

        while (lines.TryNext(out var line, out var gone, out var added, out var styles))
        {
            var looks = string.Join(" ", styles.OrderBy(s => s.Value).Select(s => $"{s.Key.Color.X}/{s.Key.Radius}:{s.Value}"));
            sent.Add(new Send(line.Key.Edge, line.Index, gone, added, looks));
        }

        return sent;
    }

    [Fact]
    public void PlayersWhoShareALookGetTheLineAndTheLookInOneSendEach()
    {
        var lines = new SharedLines();

        Draw(lines, A, Green, Bottom);
        Draw(lines, B, Green, Bottom);
        Draw(lines, C, Green, Bottom);

        Assert.Equal([new Send(Bottom, 0, 0, 0b100011, "0/1:35")], Sent(lines));
        Assert.Empty(Sent(lines));
    }

    [Fact]
    public void OneLineIsCreatedForAllAndEachLookGoesToItsOwn()
    {
        var lines = new SharedLines();

        Draw(lines, A, Green, Bottom);
        Draw(lines, B, Red, Bottom);

        Assert.Equal([new Send(Bottom, 0, 0, 0b11, "0/1:1 255/1:2")], Sent(lines));
    }

    [Fact]
    public void ALookChangeOnlyRestylesThatPlayersCopies()
    {
        var lines = new SharedLines();

        Draw(lines, A, Green, Bottom, Top);
        Draw(lines, B, Green, Bottom, Top);
        Sent(lines);

        Draw(lines, B, Thick, Bottom, Top);

        Assert.Equal([new Send(Bottom, 0, 0, 0, "0/4:2"), new Send(Top, 1, 0, 0, "0/4:2")], Sent(lines));
    }

    [Fact]
    public void GoingFlatDropsOnlyTheEdgesNotAtTheBottom()
    {
        var lines = new SharedLines();

        Draw(lines, A, Green, Bottom, Top);
        Sent(lines);

        Draw(lines, A, Green, Bottom);

        Assert.Equal([new Send(Top, 1, 0b1, 0, "")], Sent(lines));
        Assert.Equal(1, lines.Count);
    }

    [Fact]
    public void ALateJoinerGetsTheLineAlone()
    {
        var lines = new SharedLines();

        Draw(lines, A, Green, Bottom);
        Sent(lines);
        Draw(lines, B, Red, Bottom);

        Assert.Equal([new Send(Bottom, 0, 0, 0b10, "255/1:2")], Sent(lines));
    }

    [Fact]
    public void ALineNobodyWantsIsDestroyedAndItsIndexReused()
    {
        var lines = new SharedLines();

        Draw(lines, A, Green, Bottom);
        Sent(lines);
        Draw(lines, A, Green); // hidden

        Assert.Equal([new Send(Bottom, 0, 0b1, 0, "")], Sent(lines));
        Assert.Equal(0, lines.Count);

        Draw(lines, A, Red, Top);
        Assert.Equal([new Send(Top, 0, 0, 0b1, "255/1:1")], Sent(lines));
    }

    [Fact]
    public void NothingIsSentForAPlayerWhoLeft()
    {
        var lines = new SharedLines();

        Draw(lines, A, Green, Bottom);
        Draw(lines, B, Green, Bottom);
        Sent(lines);

        lines.Drop(A);
        Assert.Equal([new Send(Bottom, 0, 0, 0, "")], Sent(lines)); // B keeps it

        lines.Drop(B);
        Assert.Equal([new Send(Bottom, 0, 0, 0, "")], Sent(lines));
        Assert.Equal(0, lines.Count);
    }

    [Fact]
    public void HiddenThenShownAgainBeforeTheRemovalWentOutOnlyRestyles()
    {
        var lines = new SharedLines();

        Draw(lines, A, Green, Bottom);
        Sent(lines);
        Draw(lines, A, Green);
        Draw(lines, A, Red, Bottom);

        Assert.Equal([new Send(Bottom, 0, 0, 0, "255/1:1")], Sent(lines));
    }

    [Fact]
    public void RedrawingTheSameLinesQueuesNothing()
    {
        var lines = new SharedLines();

        Draw(lines, A, Green, Bottom, Top);
        Sent(lines);
        Draw(lines, A, Green, Bottom, Top);

        Assert.Empty(Sent(lines));
    }
}
