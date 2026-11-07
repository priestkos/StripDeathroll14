using Xunit;

namespace StripDeathroll14.Tests;

public class GameTests
{
    private static DeathrollGame Game(params string[] names)
    {
        var g = new DeathrollGame();
        foreach (var n in names) Assert.True(g.Add(n));
        return g;
    }

    private static Player P(DeathrollGame g, string name) => g.Find(name)!;

    [Fact]
    public void ParsesRandomAndDice()
    {
        Assert.Equal(new ParsedRoll(null, 7, 10), Rolls.ParseRandom("Random! You roll a 7 (out of 10)."));
        Assert.Equal(new ParsedRoll("Ann Lee", 532, null), Rolls.ParseRandom("Random! Ann Lee rolls a 532."));
        Assert.Equal(new ParsedRoll("Ann Lee", 5, 532), Rolls.ParseRandom("Random! Ann Lee rolls a 5 (out of 532)."));
        Assert.Equal(new ParsedRoll(null, 7, 10), Rolls.ParseDice("Random! (1-10) 7"));
        Assert.Equal(new ParsedRoll(null, 812, null), Rolls.ParseDice("Random! 812"));
        Assert.Null(Rolls.ParseRandom("Ann Lee: Random! You roll a 7 (out of 10)."));
        Assert.Null(Rolls.ParseDice("Random! (1-10) 7 extra"));
    }

    [Fact]
    public void NameMatchingHandlesGlyphsAndWorlds()
    {
        Assert.True(Names.Matches("Ann Lee", "Ann Lee"));
        Assert.True(Names.Matches("Ann Lee", "\uE090Ann Lee"));          // party-slot glyph before
        Assert.True(Names.Matches("Ann Lee", "Ann Lee\uE05DCactuar"));    // cross-world glyph + world after
        Assert.True(Names.Matches("Ann Lee", "Ann LeeCactuar"));
        Assert.False(Names.Matches("Ann Lee", "Ann Leez"));
        Assert.False(Names.Matches("Ann Lee", "Joann Lee"));
        Assert.False(Names.Matches("Ann Lee", "Ann Lee-Smith"));
        Assert.False(Names.Matches("", "Ann Lee"));
    }

    [Fact]
    public void NameValidationIsATrustBoundary()
    {
        Assert.True(Names.IsValid("Ann Lee"));
        Assert.True(Names.IsValid("Y'shtola Rhul"));
        Assert.False(Names.IsValid("Ann"));
        Assert.False(Names.IsValid("Ann Lee /shout hi"));
        Assert.False(Names.IsValid("Ann Lee\n/shout"));
        var g = Game("Ann Lee");
        Assert.False(g.Add("ann lee")); // duplicate
    }

    [Fact]
    public void OpeningSetsOrderHighToLowAndFirstLimit()
    {
        var g = Game("Ann Lee", "Bo Ray", "Cy Dee");
        g.StartRound();
        Assert.Equal(Phase.Opening, g.Phase);
        Assert.Equal(RollOutcome.OpeningRecorded, g.OnRoll(P(g, "Ann Lee"), 100, null));
        Assert.Equal(RollOutcome.AlreadyRolled, g.OnRoll(P(g, "Ann Lee"), 900, null));
        Assert.Equal(RollOutcome.OpeningWrongMax, g.OnRoll(P(g, "Bo Ray"), 5, 10));
        Assert.Null(P(g, "Bo Ray").Opening);
        Assert.Equal(RollOutcome.OpeningRecorded, g.OnRoll(P(g, "Bo Ray"), 812, 999));
        Assert.Equal(RollOutcome.OrderSet, g.OnRoll(P(g, "Cy Dee"), 400, null));
        Assert.Equal(["Bo Ray", "Cy Dee", "Ann Lee"], g.Players.Select(p => p.Name));
        Assert.Equal("Bo Ray", g.Current!.Name);
        Assert.Equal(812, g.Limit);
        Assert.False(g.OpeningTie);
    }

    [Fact]
    public void TurnsLoopAndAlertOnWrongMaxAndOutOfTurn()
    {
        var g = Opened(out _);
        // order Ann (900), Bo (500); limit 900
        Assert.Equal(RollOutcome.OutOfTurn, g.OnRoll(P(g, "Bo Ray"), 3, 900));
        Assert.Equal(RollOutcome.WrongMax, g.OnRoll(P(g, "Ann Lee"), 300, 999));
        Assert.Equal(300, g.Pending!.Value.Value);
        Assert.Equal("Ann Lee", g.Current!.Name); // not advanced
        Assert.Equal(RollOutcome.Next, g.AcceptPending());
        Assert.Null(g.Pending);
        Assert.Equal(300, g.Limit);
        Assert.Equal("Bo Ray", g.Current!.Name);
        Assert.Equal(RollOutcome.Next, g.OnRoll(P(g, "Bo Ray"), 120, 300));
        Assert.Equal("Ann Lee", g.Current!.Name); // looped
        Assert.Equal(RollOutcome.Ignored, g.OnRoll(null, 5, 120)); // spectator
    }

    [Theory]
    [InlineData(1)]
    [InlineData(69)]
    [InlineData(666)]
    public void EndingRollsWinAndPickCostsALife(int ending)
    {
        var g = Opened(out _);
        Assert.Equal(RollOutcome.Won, g.OnRoll(P(g, "Ann Lee"), ending, 900));
        Assert.Equal(Phase.Pick, g.Phase);
        Assert.False(g.Pick(P(g, "Ann Lee"))); // winner cannot pick themselves
        Assert.True(g.Pick(P(g, "Bo Ray")));
        Assert.Equal(2, P(g, "Bo Ray").Lives);
        Assert.Equal(1, P(g, "Ann Lee").Wins);
        Assert.Equal(new RoundRecord(1, "Ann Lee", ending, "Bo Ray", 2), g.History.Single());
        Assert.Equal(Phase.Opening, g.Phase); // next round starts with the opening roll
        Assert.Equal(2, g.Round);
    }

    [Fact]
    public void ThreeLivesThenOutThenGameOver()
    {
        var g = Game("Ann Lee", "Bo Ray", "Cy Dee");
        for (var i = 0; i < 3; i++) WinRound(g, "Ann Lee", "Bo Ray");
        Assert.True(P(g, "Bo Ray").Out);
        Assert.Equal(Phase.Opening, g.Phase);
        Assert.Equal(2, g.Active.Count());
        Assert.Equal(RollOutcome.Ignored, g.OnRoll(P(g, "Bo Ray"), 5, null)); // out players are ignored
        for (var i = 0; i < 3; i++) WinRound(g, "Ann Lee", "Cy Dee");
        Assert.Equal(Phase.GameOver, g.Phase);
        Assert.Equal("Ann Lee", g.Active.Single().Name);
        Assert.Equal(6, g.History.Count);
        Assert.Equal(6, P(g, "Ann Lee").Wins);
        g.NewGame();
        Assert.All(g.Players, p => Assert.Equal(3, p.Lives));
        Assert.Empty(g.History);
    }

    [Fact]
    public void OutPlayersAreSkippedInTurns()
    {
        var g = Game("Ann Lee", "Bo Ray", "Cy Dee");
        for (var i = 0; i < 3; i++) WinRound(g, "Ann Lee", "Bo Ray");
        g.OnRoll(P(g, "Ann Lee"), 900, null);
        g.OnRoll(P(g, "Cy Dee"), 100, null);
        Assert.Equal("Ann Lee", g.Current!.Name);
        g.OnRoll(P(g, "Ann Lee"), 50, 900);
        Assert.Equal("Cy Dee", g.Current!.Name);
        g.OnRoll(P(g, "Cy Dee"), 20, 50);
        Assert.Equal("Ann Lee", g.Current!.Name);
    }

    [Fact]
    public void RemoveAndMoveKeepTheTurnSane()
    {
        var g = Game("Ann Lee", "Bo Ray", "Cy Dee");
        g.StartRound();
        g.OnRoll(P(g, "Ann Lee"), 900, null);
        g.OnRoll(P(g, "Bo Ray"), 500, null);
        Assert.Equal(Phase.Opening, g.Phase);
        g.Remove(P(g, "Cy Dee")); // the last missing opening roll leaves: turns start
        Assert.Equal(Phase.Turns, g.Phase);
        Assert.Equal("Ann Lee", g.Current!.Name);

        g.Add("Cy Dee");
        g.Move(P(g, "Cy Dee"), -1); // Ann, Cy, Bo
        Assert.Equal(["Ann Lee", "Cy Dee", "Bo Ray"], g.Players.Select(p => p.Name));
        g.Remove(P(g, "Ann Lee")); // current leaves: the next in the list is up, same limit
        Assert.Equal("Cy Dee", g.Current!.Name);
        Assert.Equal(900, g.Limit);
        g.Remove(P(g, "Cy Dee"));
        Assert.Equal(Phase.Setup, g.Phase); // one player left cannot play
        g.Move(P(g, "Bo Ray"), -1); // out of range: no-op
    }

    [Fact]
    public void OpeningTieIsFlagged()
    {
        var g = Game("Ann Lee", "Bo Ray");
        g.StartRound();
        g.OnRoll(P(g, "Ann Lee"), 500, null);
        g.OnRoll(P(g, "Bo Ray"), 500, null);
        Assert.True(g.OpeningTie);
        Assert.Equal("Ann Lee", g.Current!.Name); // list order breaks the tie
    }

    [Fact]
    public void LinesAndCommands()
    {
        Assert.Equal("/random", Lines.RollCommand(Venue.Private, false, null));
        Assert.Equal("/random 532", Lines.RollCommand(Venue.Private, true, 532));
        Assert.Equal("/dice party 532", Lines.RollCommand(Venue.Public, false, 532));
        Assert.Equal("/dice alliance", Lines.RollCommand(Venue.Public, true, null));
        Assert.Equal("/s", Lines.AnnouncePrefix(Venue.Private, true));
        Assert.Equal("/p", Lines.AnnouncePrefix(Venue.Public, false));
        Assert.Equal("/a", Lines.AnnouncePrefix(Venue.Public, true));
        Assert.Equal("/y", Lines.TextPrefix(TextChannel.Yell));
        Assert.Equal("Ann Lee, roll /random 532", Lines.Call("Ann Lee", "/random 532"));
        Assert.Equal("Bo Ray strips! 1 piece left.", Lines.Stripped("Bo Ray", 1));
        Assert.Equal("Bo Ray strips the last piece and is out!", Lines.Stripped("Bo Ray", 0));
        Assert.False(Lines.IsSendable("/shout hi"));
        Assert.False(Lines.IsSendable("a\tb"));
        Assert.False(Lines.IsSendable(new string('x', 401)));
        Assert.True(Lines.IsSendable("ストリップ"));
        Assert.Equal(["a", "b"], Lines.TextLines("a\r\n\r\n b \n"));
        Assert.Equal(Lines.MaxTextLines, Lines.TextLines(string.Join('\n', Enumerable.Range(0, 50))).Count);
    }

    [Fact]
    public void EnumNumbersArePinned()
    {
        Assert.Equal(0, (int)Venue.Private);
        Assert.Equal(1, (int)Venue.Public);
        Assert.Equal([0, 1, 2], new[] { TextChannel.Say, TextChannel.Yell, TextChannel.Party }.Select(c => (int)c));
    }

    // A round can never ask for rolls forever: each accepted roll is <= the limit, a 1 always ends it.
    [Fact]
    public void RandomPlaySweepAlwaysFinishes()
    {
        var rng = new Random(1234);
        for (var game = 0; game < 2000; game++)
        {
            var g = Game("Ann Lee", "Bo Ray", "Cy Dee", "Di Fay");
            var rolls = 0;
            while (g.Phase != Phase.GameOver)
            {
                if (g.Phase == Phase.Setup) g.StartRound();
                if (g.Phase == Phase.Opening)
                    foreach (var p in g.Active.Where(p => p.Opening is null).ToList()) g.OnRoll(p, rng.Next(1, 1000), null);
                else if (g.Phase == Phase.Turns)
                {
                    var out1 = g.OnRoll(g.Current, rng.Next(1, g.Limit + 1), g.Limit);
                    Assert.True(out1 is RollOutcome.Next or RollOutcome.Won);
                }
                else if (g.Phase == Phase.Pick)
                {
                    var choices = g.Active.Where(p => p != g.Winner).ToList();
                    Assert.True(g.Pick(choices[rng.Next(choices.Count)]));
                }

                Assert.True(++rolls < 20_000, "game did not finish");
            }

            var survivor = Assert.Single(g.Active);
            Assert.Equal(9 + (DeathrollGame.StartLives - survivor.Lives), g.History.Count); // 3 losers x 3 pieces + the survivor's
        }
    }

    private static DeathrollGame Opened(out DeathrollGame g)
    {
        g = Game("Ann Lee", "Bo Ray");
        g.StartRound();
        g.OnRoll(P(g, "Bo Ray"), 500, null);
        g.OnRoll(P(g, "Ann Lee"), 900, null);
        return g;
    }

    private static void WinRound(DeathrollGame g, string winner, string target)
    {
        if (g.Phase == Phase.Setup) g.StartRound();
        foreach (var p in g.Active.ToList()) g.OnRoll(p, p.Name == winner ? 999 : 10, null);
        Assert.Equal(winner, g.Current!.Name);
        Assert.Equal(RollOutcome.Won, g.OnRoll(g.Current, 1, g.Limit));
        Assert.True(g.Pick(P(g, target)));
    }
}
