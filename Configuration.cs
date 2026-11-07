using Dalamud.Configuration;

namespace StripDeathroll14;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;
    public Venue Venue { get; set; } = Venue.Private;

    /// <summary>Off by default: calls and results wait in the window for a Post press.</summary>
    public bool AutoPost { get; set; }

    public TextChannel TextChannel { get; set; } = TextChannel.Say;

    public string RulesText { get; set; } =
        "Strip Deathroll! Everyone rolls /random to start; highest goes first, then down the list.\n" +
        "On your turn, roll /random up to the number the player before you rolled.\n" +
        "Roll a 1, 69 or 666 to win the round - then you pick who strips.\n" +
        "Everyone has 3 pieces. Lose all 3 and you are out!";

    public string AdText { get; set; } = "Strip Deathroll starting soon! Ask to join the game.";

    /// <summary>Pulls hand-edited values back inside what the UI offers.</summary>
    public void Normalize()
    {
        if (!Enum.IsDefined(Venue)) Venue = Venue.Private;
        if (!Enum.IsDefined(TextChannel)) TextChannel = TextChannel.Say;
        RulesText ??= "";
        AdText ??= "";
    }
}
