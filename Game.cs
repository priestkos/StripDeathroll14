using System.Text.RegularExpressions;

namespace StripDeathroll14;

/// <summary>Where the game is played. Saved as a number: never renumber, only append.</summary>
public enum Venue
{
    Private = 0, // say
    Public = 1,  // party, or alliance when in one
}

/// <summary>Where the rules and ad texts are posted. Saved as a number: never renumber, only append.</summary>
public enum TextChannel
{
    Say = 0,
    Yell = 1,
    Party = 2,
}

public enum Phase { Setup, Opening, Turns, Pick, GameOver }

public enum RollOutcome
{
    Ignored,          // not a player, out, or nothing expected
    OpeningRecorded,
    OpeningWrongMax,  // not recorded: the player rerolls a bare /random
    AlreadyRolled,
    OrderSet,         // last opening roll in: turns start
    OutOfTurn,
    WrongMax,         // held as Pending until the host accepts it or the player rerolls
    Next,
    Won,
}

public sealed class Player
{
    public string Name { get; set; } = "";
    public int Lives { get; set; } = DeathrollGame.StartLives;
    public int Wins { get; set; }
    public int? Opening { get; set; }
    public bool Out => Lives <= 0;
}

public sealed record RoundRecord(int Round, string Winner, int WinningRoll, string Target, int TargetLivesLeft);

public readonly record struct PendingRoll(Player Player, int Value, int? Max);

/// <summary>Pure Strip Deathroll state: no Dalamud types, so the test project compiles it as is.</summary>
public sealed class DeathrollGame
{
    public const int StartLives = 3;
    public const int OpeningMax = 999; // what a bare /random rolls up to

    public List<Player> Players { get; } = [];
    public List<RoundRecord> History { get; } = [];
    public Phase Phase { get; private set; } = Phase.Setup;
    public int Round { get; private set; }
    public Player? Current { get; private set; }
    /// <summary>The number the current player rolls to; null = a fresh /random (the player starting the turns).</summary>
    public int? Limit { get; private set; }
    public Player? Winner { get; private set; }
    public int WinningRoll { get; private set; }
    public PendingRoll? Pending { get; private set; }
    public bool OpeningTie { get; private set; }

    public IEnumerable<Player> Active => Players.Where(p => !p.Out);

    public static bool IsEnding(int roll) => roll is 1 or 69 or 666;

    public Player? Find(string rawName) => Players.FirstOrDefault(p => Names.Matches(p.Name, rawName));

    public bool Add(string name)
    {
        name = name.Trim();
        if (!Names.IsValid(name) || Players.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))) return false;
        Players.Add(new Player { Name = name });
        return true;
    }

    public void Remove(Player p)
    {
        if (Current == p) Current = Players.Count > 1 ? NextActive(p) : null;
        if (Winner == p) { Winner = null; Phase = Phase.Setup; }
        if (Pending?.Player == p) Pending = null;
        Players.Remove(p);
        if (Phase == Phase.Opening && Active.Any() && Active.All(x => x.Opening is not null)) SetOrder();
        if (Phase is Phase.Opening or Phase.Turns && Active.Count() < 2) Phase = Phase.Setup;
    }

    public void Move(Player p, int delta)
    {
        var i = Players.IndexOf(p);
        var j = i + delta;
        if (i < 0 || j < 0 || j >= Players.Count) return;
        (Players[i], Players[j]) = (Players[j], Players[i]);
    }

    public bool CanStartRound => Phase is Phase.Setup or Phase.Opening or Phase.Turns && Active.Count() >= 2;

    public void StartRound()
    {
        if (!CanStartRound) return;
        foreach (var p in Players) p.Opening = null;
        Round++;
        Phase = Phase.Opening;
        Current = Winner = null;
        Limit = null;
        Pending = null;
        OpeningTie = false;
    }

    public void NewGame()
    {
        foreach (var p in Players) { p.Lives = StartLives; p.Wins = 0; p.Opening = null; }
        History.Clear();
        Round = 0;
        Phase = Phase.Setup;
        Current = Winner = null;
        Pending = null;
        OpeningTie = false;
    }

    /// <summary>A roll read from chat. <paramref name="max"/> is null for a bare /random.</summary>
    public RollOutcome OnRoll(Player? p, int value, int? max)
    {
        if (p is null || p.Out) return RollOutcome.Ignored;

        if (Phase == Phase.Opening)
        {
            if (p.Opening is not null) return RollOutcome.AlreadyRolled;
            if (max is not (null or OpeningMax)) return RollOutcome.OpeningWrongMax; // not recorded: reroll a bare /random
            p.Opening = value;
            if (Active.All(x => x.Opening is not null)) { SetOrder(); return RollOutcome.OrderSet; }
            return RollOutcome.OpeningRecorded;
        }

        if (Phase != Phase.Turns) return RollOutcome.Ignored;
        if (p != Current) return RollOutcome.OutOfTurn;
        if (Limit is null ? max is not (null or OpeningMax) : max != Limit)
        {
            Pending = new PendingRoll(p, value, max);
            return RollOutcome.WrongMax;
        }

        return Apply(value);
    }

    public RollOutcome AcceptPending()
    {
        if (Pending is not { } pending || Phase != Phase.Turns || pending.Player != Current) return RollOutcome.Ignored;
        return Apply(pending.Value);
    }

    /// <summary>The winner names who strips. Returns false when the pick is not allowed.</summary>
    public bool Pick(Player target)
    {
        if (Phase != Phase.Pick || Winner is null || target == Winner || target.Out || !Players.Contains(target)) return false;
        Winner.Wins++;
        target.Lives--;
        History.Add(new RoundRecord(Round, Winner.Name, WinningRoll, target.Name, target.Lives));
        if (Active.Count() <= 1) { Phase = Phase.GameOver; return true; }
        Phase = Phase.Setup;
        StartRound();
        return true;
    }

    private RollOutcome Apply(int value)
    {
        Pending = null;
        if (IsEnding(value))
        {
            Winner = Current;
            WinningRoll = value;
            Phase = Phase.Pick;
            return RollOutcome.Won;
        }

        Limit = Math.Max(value, 2);
        Current = NextActive(Current!);
        return RollOutcome.Next;
    }

    // Opening rolls high to low decide the order; the list itself is the turn order, so the host can still rearrange it.
    private void SetOrder()
    {
        var active = Active.OrderByDescending(p => p.Opening).ToList(); // stable: ties keep list order
        var outs = Players.Where(p => p.Out).ToList();
        Players.Clear();
        Players.AddRange(active);
        Players.AddRange(outs);
        OpeningTie = active.Count > 1 && active[0].Opening == active[1].Opening;
        Current = active[0];
        Limit = null; // the starting player rolls a fresh /random
        Phase = Phase.Turns;
    }

    private Player? NextActive(Player from)
    {
        var i = Players.IndexOf(from);
        for (var step = 1; step <= Players.Count; step++)
        {
            var p = Players[(i + step) % Players.Count];
            if (!p.Out && p != from) return p;
        }

        return null;
    }
}

/// <summary>Character names: validation (a trust boundary, names end up inside chat lines) and matching chat text.</summary>
public static partial class Names
{
    // "Forename Surname": letters with optional ' or -, 2-15 chars each.
    [GeneratedRegex(@"^[A-Za-z][A-Za-z'\-]{1,14} [A-Za-z][A-Za-z'\-]{1,14}$")]
    private static partial Regex NameRegex();

    public static bool IsValid(string? name) => name is not null && NameRegex().IsMatch(name);

    /// <summary>
    /// True when <paramref name="raw"/> contains the name as a whole: a party-slot glyph may precede it and a
    /// cross-world glyph plus world name may follow it, but "Ann Lee" never matches "Ann Leez".
    /// </summary>
    public static bool Matches(string name, string raw)
    {
        if (name.Length == 0) return false;
        for (var i = raw.IndexOf(name, StringComparison.OrdinalIgnoreCase); i >= 0; i = raw.IndexOf(name, i + 1, StringComparison.OrdinalIgnoreCase))
        {
            var end = i + name.Length;
            var before = i == 0 || !char.IsLetter(raw[i - 1]) && raw[i - 1] is not ('\'' or '-');
            var after = end == raw.Length || !char.IsLower(raw[end]) && raw[end] is not ('\'' or '-' or ' ');
            if (before && after) return true;
        }

        return false;
    }
}

public readonly record struct ParsedRoll(string? Name, int Value, int? Max);

/// <summary>Roll text parsing (English client). Formats agreed by ChatDeathRoll2 and FFXIVGamblePyon.</summary>
public static partial class Rolls
{
    // "Random! You roll a 7 (out of 10)." / "Random! Ann Lee rolls a 7." (bare /random has no "out of")
    [GeneratedRegex(@"^Random! (?:You roll|(.+?) rolls) a (\d+)(?: \(out of (\d+)\))?\.$")]
    private static partial Regex RandomRegex();

    // "/dice" in a chat mode: "Random! (1-10) 7", the roller is the message sender; a bare /dice has no range.
    [GeneratedRegex(@"^Random! (?:\(1-(\d+)\) )?(\d+)$")]
    private static partial Regex DiceRegex();

    /// <summary>A /random result; Name is null for your own roll.</summary>
    public static ParsedRoll? ParseRandom(string text)
    {
        var m = RandomRegex().Match(text.Trim());
        if (!m.Success) return null;
        return new ParsedRoll(m.Groups[1].Success ? m.Groups[1].Value : null, int.Parse(m.Groups[2].Value),
            m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : null);
    }

    /// <summary>A /dice result; the caller supplies the name from the sender.</summary>
    public static ParsedRoll? ParseDice(string text)
    {
        var m = DiceRegex().Match(text.Trim());
        if (!m.Success) return null;
        return new ParsedRoll(null, int.Parse(m.Groups[2].Value), m.Groups[1].Success ? int.Parse(m.Groups[1].Value) : null);
    }
}

/// <summary>Every line the plugin posts, and the command prefixes it posts them with.</summary>
public static class Lines
{
    /// <summary>The roll command players are asked to use; n null = the bare opening roll.</summary>
    public static string RollCommand(Venue venue, bool alliance, int? n)
    {
        var cmd = venue == Venue.Private ? "/random" : alliance ? "/dice alliance" : "/dice party";
        return n is null ? cmd : $"{cmd} {n}";
    }

    public static string AnnouncePrefix(Venue venue, bool alliance) => venue == Venue.Private ? "/s" : alliance ? "/a" : "/p";

    public static string TextPrefix(TextChannel c) => c switch
    {
        TextChannel.Yell => "/y",
        TextChannel.Party => "/p",
        _ => "/s",
    };

    public static string Opening(int round, string cmd) => $"Strip Deathroll round {round}! Everyone roll {cmd} - highest goes first.";

    public static string Call(string name, string cmd) => $"{name}, roll {cmd}";

    public static string Won(string winner, int roll, int round) => $"{winner} rolled {roll} and wins round {round}! {winner}, who strips?";

    public static string Stripped(string target, int livesLeft) => livesLeft > 0
        ? $"{target} strips! {livesLeft} {(livesLeft == 1 ? "piece" : "pieces")} left."
        : $"{target} strips the last piece and is out!";

    public static string GameOver(string name) => $"{name} is the last one standing and wins Strip Deathroll!";

    /// <summary>A line is safe to send only as one short line that is not a command of its own.</summary>
    public static bool IsSendable(string line) =>
        line.Length > 0 && System.Text.Encoding.UTF8.GetByteCount(line) <= 400 && !line.StartsWith('/') && !line.Any(char.IsControl);

    /// <summary>Rules/ad text: one chat line per non-empty text line, capped so a paste cannot flood chat.</summary>
    public const int MaxTextLines = 10;

    public static List<string> TextLines(string text) =>
        text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Take(MaxTextLines).ToList();
}
