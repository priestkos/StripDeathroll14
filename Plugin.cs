using Dalamud.Game.Chat;
using Dalamud.Game.Command;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Info;

namespace StripDeathroll14;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/sdr";
    private const int MinSpacingMs = 2000; // safety floor between any two sends
    private const int JitterMs = 1500;

    // What the game's own sanitiser may keep; a free-text line it would change is dropped, never sent altered.
    private const AllowedEntities TextEntities = AllowedEntities.UppercaseLetters | AllowedEntities.LowercaseLetters |
        AllowedEntities.Numbers | AllowedEntities.SpecialCharacters | AllowedEntities.CharacterList |
        AllowedEntities.OtherCharacters | AllowedEntities.Payloads | AllowedEntities.Unknown9 | AllowedEntities.CJK;

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly ICommandManager commandManager;
    private readonly IChatGui chatGui;
    private readonly IClientState clientState;
    private readonly IPlayerState playerState;
    private readonly IFramework framework;
    private readonly IPartyList partyList;
    private readonly WindowSystem windowSystem = new("StripDeathroll14");
    private readonly MainWindow window;
    private readonly Queue<(string? Prefix, string Text, bool FreeText)> outbox = new();
    private long nextSendAt;

    public Configuration Config { get; }
    public DeathrollGame Game { get; } = new();

    /// <summary>Announcements waiting for the host's Post press (when auto-post is off).</summary>
    public List<string> Proposed { get; } = [];

    public string? LastAlert { get; private set; }
    public int Queued => outbox.Count;

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        ICommandManager commandManager,
        IChatGui chatGui,
        IClientState clientState,
        IPlayerState playerState,
        IFramework framework,
        IPartyList partyList)
    {
        this.pluginInterface = pluginInterface;
        this.commandManager = commandManager;
        this.chatGui = chatGui;
        this.clientState = clientState;
        this.playerState = playerState;
        this.framework = framework;
        this.partyList = partyList;

        Config = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Config.Normalize();

        window = new MainWindow(this);
        windowSystem.AddWindow(window);

        pluginInterface.UiBuilder.Draw += windowSystem.Draw;
        pluginInterface.UiBuilder.OpenMainUi += window.Toggle;
        pluginInterface.UiBuilder.OpenConfigUi += window.Toggle;
        chatGui.ChatMessage += OnChatMessage;
        clientState.Logout += OnLogout;
        framework.Update += OnUpdate;

        commandManager.AddHandler(CommandName, new CommandInfo((_, _) => window.Toggle())
        {
            HelpMessage = "Open the Strip Deathroll host window.",
        });
    }

    public void Dispose()
    {
        commandManager.RemoveHandler(CommandName);
        framework.Update -= OnUpdate;
        clientState.Logout -= OnLogout;
        chatGui.ChatMessage -= OnChatMessage;
        pluginInterface.UiBuilder.OpenConfigUi -= window.Toggle;
        pluginInterface.UiBuilder.OpenMainUi -= window.Toggle;
        pluginInterface.UiBuilder.Draw -= windowSystem.Draw;
        windowSystem.RemoveAllWindows();
    }

    public void SaveConfig() => pluginInterface.SavePluginConfig(Config);

    public string MyName => playerState.CharacterName;
    public bool InAlliance => partyList.IsAlliance;
    public string RollCommand(int? n) => Lines.RollCommand(Config.Venue, InAlliance, n);

    // ---- host actions (all from button presses) ----

    public void StartRound()
    {
        Game.StartRound();
        if (Game.Phase == Phase.Opening) Propose(Lines.Opening(Game.Round, RollCommand(null)));
    }

    public void Pick(Player target)
    {
        if (!Game.Pick(target)) return;
        Propose(Lines.Stripped(target.Name, target.Lives));
        if (Game.Phase == Phase.GameOver) Propose(Lines.GameOver(Game.Active.First().Name));
        else if (Game.Phase == Phase.Opening) Propose(Lines.Opening(Game.Round, RollCommand(null)));
    }

    public void AcceptPending() => Handle(Game.AcceptPending(), Game.Current);

    public void CallCurrent()
    {
        if (Game.Current is { } p) Propose(Lines.Call(p.Name, RollCommand(Game.Limit)));
    }

    /// <summary>Your own roll: the opening roll or your turn. Sends the roll command only.</summary>
    public void RollForMe()
    {
        int? n = Game.Phase == Phase.Opening ? null : Game.Limit;
        outbox.Enqueue((null, RollCommand(n), false));
    }

    public void PostProposed()
    {
        foreach (var line in Proposed) Enqueue(Lines.AnnouncePrefix(Config.Venue, InAlliance), line, false);
        Proposed.Clear();
    }

    public void PostText(string text)
    {
        foreach (var line in Lines.TextLines(text)) Enqueue(Lines.TextPrefix(Config.TextChannel), line, true);
    }

    public int AddGroup()
    {
        var names = new List<string>();
        foreach (var m in partyList) names.Add(m.Name.TextValue);
        for (var i = 0; i < 20; i++)
        {
            var addr = partyList.GetAllianceMemberAddress(i);
            if (addr != 0 && partyList.CreateAllianceMemberReference(addr) is { } m) names.Add(m.Name.TextValue);
        }

        AddCrossWorldParty(names);
        if (names.Count == 0 && MyName.Length > 0) names.Add(MyName);
        return names.Count(Game.Add); // Add validates each name and skips duplicates
    }

    // A cross-world party is not in IPartyList; read the local group from the cross-realm proxy (read-only).
    private static unsafe void AddCrossWorldParty(List<string> names)
    {
        if (!InfoProxyCrossRealm.IsCrossRealmParty()) return;
        var count = InfoProxyCrossRealm.GetPartyMemberCount();
        for (uint i = 0; i < count; i++)
        {
            var m = InfoProxyCrossRealm.GetGroupMember(i);
            if (m != null) names.Add(m->NameString);
        }
    }

    // ---- chat and tick ----

    private void Propose(string line)
    {
        if (Config.AutoPost) Enqueue(Lines.AnnouncePrefix(Config.Venue, InAlliance), line, false);
        else Proposed.Add(line);
    }

    private void Enqueue(string prefix, string line, bool freeText)
    {
        if (Lines.IsSendable(line)) outbox.Enqueue((prefix, line, freeText));
        else Alert($"Not sent (too long, starts with /, or has a line break): {line}");
    }

    private void Alert(string text)
    {
        LastAlert = text;
        chatGui.Print(new XivChatEntry { Type = XivChatType.Echo, Message = $"[Deathroll] {text}" });
    }

    private void OnChatMessage(IHandleableChatMessage message)
    {
        ParsedRoll? roll;
        string? name;
        if (message.LogKind == XivChatType.RandomNumber)
        {
            roll = Rolls.ParseRandom(message.Message.TextValue);
            name = roll is null ? null : roll.Value.Name is null ? MyName : PlayerName(message.Message) ?? roll.Value.Name;
        }
        else if (message.LogKind == XivChatType.Party || message.LogKind == XivChatType.CrossParty || message.LogKind == XivChatType.Alliance)
        {
            roll = Rolls.ParseDice(message.Message.TextValue);
            name = roll is null ? null : PlayerName(message.Sender) ?? message.Sender.TextValue;
        }
        else
        {
            return;
        }

        if (roll is not { } r || name is null) return;
        var player = Game.Find(name);
        var outcome = Game.OnRoll(player, r.Value, r.Max);
        var shownMax = r.Max?.ToString() ?? "999";
        switch (outcome)
        {
            case RollOutcome.OpeningWrongMax:
                Alert($"{player!.Name} rolled out of {shownMax}: the opening roll is a plain {RollCommand(null)}. Not counted.");
                break;
            case RollOutcome.AlreadyRolled:
                Alert($"{player!.Name} rolled again during the opening. Ignored.");
                break;
            case RollOutcome.OutOfTurn:
                Alert($"{player!.Name} rolled out of turn ({Game.Current?.Name} is up). Ignored.");
                break;
            case RollOutcome.WrongMax:
                Alert($"{player!.Name} rolled {r.Value} out of {shownMax}, expected {Game.Limit}. Accept it or ask for a reroll.");
                break;
            default:
                Handle(outcome, player);
                break;
        }
    }

    private void Handle(RollOutcome outcome, Player? player)
    {
        switch (outcome)
        {
            case RollOutcome.OrderSet:
                if (Game.OpeningTie) Alert("Tie for the highest opening roll: rearrange the list if needed, then Call.");
                CallCurrent();
                break;
            case RollOutcome.Next:
                CallCurrent();
                break;
            case RollOutcome.Won:
                Propose(Lines.Won(Game.Winner!.Name, Game.WinningRoll, Game.Round));
                break;
        }
    }

    private static string? PlayerName(SeString s) => s.Payloads.OfType<PlayerPayload>().FirstOrDefault()?.PlayerName;

    private void OnLogout(int type, int code)
    {
        outbox.Clear();
        Proposed.Clear();
    }

    private void OnUpdate(IFramework _)
    {
        if (!clientState.IsLoggedIn || outbox.Count == 0) return;
        var now = Environment.TickCount64;
        if (now < nextSendAt) return;

        var (prefix, text, freeText) = outbox.Dequeue();
        if (freeText && !IsClean(text))
        {
            Alert($"Not sent (the game would change some characters): {text}");
            return;
        }

        if (Send(prefix is null ? text : $"{prefix} {text}"))
            nextSendAt = now + MinSpacingMs + Random.Shared.Next(JitterMs);
    }

    // Only plugin-composed text reaches here: a roll command, or a line checked by Lines.IsSendable behind a fixed prefix.
    private static unsafe bool Send(string text)
    {
        var ui = UIModule.Instance();
        if (ui == null) return false;
        var utf8 = Utf8String.FromString(text);
        ui->ProcessChatBoxEntry(utf8);
        utf8->Dtor(true);
        return true;
    }

    private static unsafe bool IsClean(string text)
    {
        var utf8 = Utf8String.FromString(text);
        utf8->SanitizeString(TextEntities);
        var clean = utf8->ToString() == text;
        utf8->Dtor(true);
        return clean;
    }
}
