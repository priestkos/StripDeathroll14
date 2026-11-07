using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace StripDeathroll14;

public sealed class MainWindow : Window
{
    private readonly Plugin plugin;
    private string newName = "";
    private string addNote = "";
    private Player? pickTarget;

    public MainWindow(Plugin plugin) : base("Strip Deathroll###StripDeathroll14")
    {
        this.plugin = plugin;
        Size = new Vector2(520, 560);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(420, 360), MaximumSize = new Vector2(1200, 1400) };
    }

    public override void Draw()
    {
        using var bar = ImRaii.TabBar("##tabs");
        if (!bar) return;
        using (var t = ImRaii.TabItem("Game")) if (t) DrawGame();
        using (var t = ImRaii.TabItem("History")) if (t) DrawHistory();
        using (var t = ImRaii.TabItem("Rules & Ad")) if (t) DrawTexts();
    }

    private void DrawGame()
    {
        var config = plugin.Config;
        var game = plugin.Game;

        ImGui.SetNextItemWidth(220);
        using (var combo = ImRaii.Combo("Venue", VenueLabel(config.Venue)))
        {
            if (combo)
            {
                foreach (var v in Enum.GetValues<Venue>())
                {
                    if (!ImGui.Selectable(VenueLabel(v), v == config.Venue)) continue;
                    config.Venue = v;
                    plugin.SaveConfig();
                }
            }
        }

        ImGui.SameLine();
        var auto = config.AutoPost;
        if (ImGui.Checkbox("Auto-post", ref auto)) { config.AutoPost = auto; plugin.SaveConfig(); }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Post calls and results as they happen (2-3.5 s apart). Off: they wait below for Post.");
        ImGui.TextDisabled($"Calls go to {Lines.AnnouncePrefix(config.Venue, plugin.InAlliance)}; players roll {plugin.RollCommand(null)} [N].");

        ImGui.Separator();
        ImGui.TextUnformatted(Status(game));
        if (plugin.LastAlert is { } alert) ImGui.TextColored(ImGuiColors.DalamudRed, alert);

        if (game.Pending is { } pending && pending.Player == game.Current)
        {
            if (ImGui.Button($"Accept {pending.Value}")) plugin.AcceptPending();
            ImGui.SameLine();
            if (ImGui.Button("Ask for reroll")) plugin.CallCurrent();
        }

        if (game.Phase == Phase.Pick) DrawPick(game);

        using (ImRaii.Disabled(!game.CanStartRound)) { if (ImGui.Button(game.Phase == Phase.Setup ? "Start round" : "Restart round")) plugin.StartRound(); }
        ImGui.SameLine();
        var myTurn = game.Find(plugin.MyName) is { } me &&
            (game.Phase == Phase.Opening && me.Opening is null && !me.Out || game.Phase == Phase.Turns && game.Current == me);
        using (ImRaii.Disabled(!myTurn)) { if (ImGui.Button("Roll for me")) plugin.RollForMe(); }
        ImGui.SameLine();
        using (ImRaii.Disabled(game.Current is null)) { if (ImGui.Button("Call again")) plugin.CallCurrent(); }
        ImGui.SameLine();
        if (ImGui.Button("New game") && ImGui.GetIO().KeyCtrl) game.NewGame();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Ctrl+click: everyone back to 3 pieces, history cleared.");

        DrawProposed();
        ImGui.Separator();
        DrawPlayers(game);
    }

    private void DrawPick(DeathrollGame game)
    {
        var choices = game.Active.Where(p => p != game.Winner).ToList();
        if (pickTarget is not null && !choices.Contains(pickTarget)) pickTarget = null;
        ImGui.SetNextItemWidth(220);
        using (var combo = ImRaii.Combo("Who strips?", pickTarget?.Name ?? "Choose..."))
        {
            if (combo)
            {
                foreach (var p in choices)
                    if (ImGui.Selectable($"{p.Name} ({p.Lives} left)", p == pickTarget)) pickTarget = p;
            }
        }

        ImGui.SameLine();
        using (ImRaii.Disabled(pickTarget is null))
        {
            if (ImGui.Button("Confirm") && pickTarget is not null)
            {
                plugin.Pick(pickTarget);
                pickTarget = null;
            }
        }
    }

    private void DrawProposed()
    {
        if (plugin.Proposed.Count == 0)
        {
            if (plugin.Queued > 0) ImGui.TextDisabled($"Sending {plugin.Queued} line(s)...");
            return;
        }

        ImGui.Separator();
        ImGui.TextUnformatted($"To post in {Lines.AnnouncePrefix(plugin.Config.Venue, plugin.InAlliance)}:");
        foreach (var line in plugin.Proposed) ImGui.BulletText(line);
        if (ImGui.Button("Post")) plugin.PostProposed();
        ImGui.SameLine();
        if (ImGui.Button("Discard")) plugin.Proposed.Clear();
    }

    private void DrawPlayers(DeathrollGame game)
    {
        ImGui.SetNextItemWidth(200);
        var enter = ImGui.InputTextWithHint("##name", "Forename Surname", ref newName, 32, ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.SameLine();
        if (ImGui.Button("Add") || enter)
        {
            addNote = game.Add(newName) ? "" : "Needs Forename Surname (letters, ' or -), not already listed.";
            if (addNote.Length == 0) newName = "";
        }

        ImGui.SameLine();
        if (ImGui.Button("Add party/alliance")) addNote = $"Added {plugin.AddGroup()} player(s).";
        if (addNote.Length > 0) ImGui.TextDisabled(addNote);

        using var table = ImRaii.Table("##players", 6, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.ScrollY);
        if (!table) return;
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("#", ImGuiTableColumnFlags.WidthFixed, 22);
        ImGui.TableSetupColumn("Player", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Opening", ImGuiTableColumnFlags.WidthFixed, 58);
        ImGui.TableSetupColumn("Pieces", ImGuiTableColumnFlags.WidthFixed, 48);
        ImGui.TableSetupColumn("Wins", ImGuiTableColumnFlags.WidthFixed, 38);
        ImGui.TableSetupColumn("##edit", ImGuiTableColumnFlags.WidthFixed, 88);
        ImGui.TableHeadersRow();

        Action? edit = null; // applied after the loop so the list is not changed while it is drawn
        for (var i = 0; i < game.Players.Count; i++)
        {
            var p = game.Players[i];
            using var id = ImRaii.PushId(i);
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(p == game.Current ? ">" : (i + 1).ToString());
            ImGui.TableNextColumn();
            if (p.Out) ImGui.TextDisabled($"{p.Name} (out)");
            else if (p == game.Current) ImGui.TextColored(ImGuiColors.HealerGreen, p.Name);
            else ImGui.TextUnformatted(p.Name);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(p.Opening?.ToString() ?? "-");
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(p.Lives.ToString());
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(p.Wins.ToString());
            ImGui.TableNextColumn();
            if (ImGui.SmallButton("Up")) edit = () => game.Move(p, -1);
            ImGui.SameLine();
            if (ImGui.SmallButton("Dn")) edit = () => game.Move(p, 1);
            ImGui.SameLine();
            if (ImGui.SmallButton("X") && ImGui.GetIO().KeyCtrl) edit = () => game.Remove(p);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Ctrl+click to remove.");
        }

        edit?.Invoke();
    }

    private void DrawHistory()
    {
        var history = plugin.Game.History;
        if (history.Count == 0) { ImGui.TextDisabled("No rounds finished yet."); return; }

        using var table = ImRaii.Table("##history", 5, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.ScrollY);
        if (!table) return;
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Round", ImGuiTableColumnFlags.WidthFixed, 44);
        ImGui.TableSetupColumn("Winner", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Roll", ImGuiTableColumnFlags.WidthFixed, 40);
        ImGui.TableSetupColumn("Stripped", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Pieces left", ImGuiTableColumnFlags.WidthFixed, 70);
        ImGui.TableHeadersRow();
        for (var i = history.Count - 1; i >= 0; i--) // newest first
        {
            var r = history[i];
            ImGui.TableNextRow();
            ImGui.TableNextColumn(); ImGui.TextUnformatted(r.Round.ToString());
            ImGui.TableNextColumn(); ImGui.TextUnformatted(r.Winner);
            ImGui.TableNextColumn(); ImGui.TextUnformatted(r.WinningRoll.ToString());
            ImGui.TableNextColumn(); ImGui.TextUnformatted(r.Target);
            ImGui.TableNextColumn(); ImGui.TextUnformatted(r.TargetLivesLeft > 0 ? r.TargetLivesLeft.ToString() : "out");
        }
    }

    private void DrawTexts()
    {
        var config = plugin.Config;
        ImGui.SetNextItemWidth(160);
        using (var combo = ImRaii.Combo("Post to", config.TextChannel.ToString()))
        {
            if (combo)
            {
                foreach (var c in Enum.GetValues<TextChannel>())
                {
                    if (!ImGui.Selectable(c.ToString(), c == config.TextChannel)) continue;
                    config.TextChannel = c;
                    plugin.SaveConfig();
                }
            }
        }

        ImGui.TextDisabled($"One chat line per text line, up to {Lines.MaxTextLines}, 2-3.5 s apart.");
        DrawText("Rules", config.RulesText, v => config.RulesText = v);
        DrawText("Ad", config.AdText, v => config.AdText = v);
    }

    private void DrawText(string label, string value, Action<string> set)
    {
        ImGui.Separator();
        ImGui.TextUnformatted(label);
        if (ImGui.InputTextMultiline($"##{label}", ref value, 4000, new Vector2(-1, 110))) set(value);
        if (ImGui.IsItemDeactivatedAfterEdit()) plugin.SaveConfig();
        if (ImGui.Button($"Post {label.ToLowerInvariant()}")) plugin.PostText(value);
    }

    private static string VenueLabel(Venue v) => v == Venue.Private ? "Private room (say)" : "Public (party/alliance)";

    private string Status(DeathrollGame g) => g.Phase switch
    {
        Phase.Setup when g.Round == 0 => "Add at least 2 players, then Start round.",
        Phase.Setup => $"Round {g.Round} stopped. Start a round when ready.",
        Phase.Opening => $"Round {g.Round}: opening rolls {g.Active.Count(p => p.Opening is not null)}/{g.Active.Count()}.",
        Phase.Turns => $"Round {g.Round}: {g.Current?.Name} to roll {plugin.RollCommand(g.Limit)}.",
        Phase.Pick => $"Round {g.Round}: {g.Winner?.Name} rolled {g.WinningRoll} and wins.",
        Phase.GameOver => $"Game over: {g.Active.FirstOrDefault()?.Name ?? "nobody"} is the last one standing.",
        _ => "",
    };
}
