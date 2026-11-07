# StripDeathroll14

A host helper for Strip Deathroll at FFXIV venues: it keeps the turn order, calls each player to roll, catches wrong rolls, and tracks pieces and round history.

`/sdr` · Dalamud API 15 · .NET 10

## What it does

- **Private or Public** — a dropdown. Private room: calls go to `/s` and players roll `/random N`. Public: calls go to `/p` (or `/a` when you are in an alliance) and players roll `/dice party N` / `/dice alliance N`, so the rolls stay in the group. Rolls are read in every form either way.
- **Player list** — add everyone in your party or alliance (cross-world parties included) with one button, or type names in as `Forename Surname`. Remove (Ctrl+click) and move players up or down; the list *is* the turn order.
- **Opening roll** — *Start round* proposes "everyone roll /random". When every active player has rolled, the list sorts high to low, the highest goes first, and their roll is the first limit. A tie for first is flagged so you can reorder.
- **Turn calls** — after each roll it proposes `Name, roll /random N`, where N is the previous player's roll, looping the list.
- **Alerts** — a roll with the wrong number (`out of 999` when 532 was expected), a roll out of turn, a second opening roll, or a non-bare opening roll prints a `[Deathroll]` line in Echo and shows in red in the window. A wrong-number roll waits for you: *Accept* it or *Ask for reroll*.
- **Round end** — a 1, 69 or 666 wins the round. The window asks *Who strips?* from the active players (not the winner); that player loses one of 3 pieces, at 0 they are out, and the next round's opening call follows. One player left is game over.
- **History** — every round: winner, winning roll, who stripped, pieces left. Wins per player are in the list.
- **Rules & Ad** — two saved texts, posted one chat line per text line (up to 10) to Say, Yell or Party.
- **Roll for me** — when it is your opening roll or your turn, one press sends the right roll command for you.

### What it is not meant to do

- **It does not roll for anyone but you,** and it rolls for you only when you press *Roll for me*.
- **It does not post on its own unless you turn on Auto-post.** By default every call and result waits in the window for *Post*.
- **It does not choose who strips.** That is the winner's call; you pick it in the window.
- **It does not handle bets or gil.**

## How it works

Rolls are read from chat (`IChatGui.ChatMessage`, nothing is hidden): `RandomNumber` lines in the form `Random! Name rolls a N (out of M).` and `/dice` lines `Random! (1-M) N` in Party, Cross-world Party and Alliance, with the roller taken from the player link in the message. The formats are the ones ChatDeathRoll2 and FFXIVGamblePyon both parse. A roll only counts for someone on the list, so spectators rolling nearby change nothing.

Everything posted goes through one queue, sent with `UIModule.ProcessChatBoxEntry` (no hooks, no signatures of its own), 2 to 3.5 s apart, only while logged in. Lines the plugin writes are checked to be one line, under 400 bytes and not a command; rules and ad lines are also run through the game's own text sanitiser and dropped, never altered, if it would change them. Player names are validated before they are used in a line.

## Commands

| Command | Effect |
| --- | --- |
| `/sdr` | Open or close the window. |

## Settings

- **Venue** — Private room (say) or Public (party/alliance).
- **Auto-post** — off by default; on posts calls and results as they happen.
- **Rules & Ad** — the two texts and the channel they go to (Say, Yell, Party).

## Build

Requires the .NET 10 SDK and the Dalamud dev distribution
(`%APPDATA%\XIVLauncher\addon\Hooks\dev`, or `DALAMUD_HOME`).

```
dotnet build -c Debug      # load bin/Debug/StripDeathroll14.dll as a dev plugin
dotnet build -c Release    # zip in bin/Release/StripDeathroll14/latest.zip
```

## Install (dev plugin)

1. `/xlsettings` → Experimental → Dev Plugin Locations → add `bin\Debug\StripDeathroll14.dll`.
2. `/xlplugins` → Dev Tools → enable **StripDeathroll14**.
3. `/sdr` opens the window.

## Tests

`StripDeathroll14.Tests` (xUnit) compiles `Game.cs` with no Dalamud and covers the roll parsers, name matching (party-slot and cross-world glyphs), name validation, opening order and ties, wrong-number and out-of-turn rolls, the 1/69/666 endings, three pieces to out and game over, removing and moving players mid-round, the posted lines and commands, pinned enum numbers, and a 2,000-game random-play sweep that asserts every game finishes.

```
dotnet test StripDeathroll14.Tests    # 15 tests
```

## Limits

- Not verified in game: behaviour is taken from the code and the offline tests, not from a live client.
- English client only: the roll text is localised, so JP/DE/FR rolls are not read.
- Each round starts with a fresh opening roll; the rules leave open whether the order carries over.
- `/dice alliance` and alliance detection are unverified in a live alliance.
- Players and history live for the session only; a plugin reload clears them. Settings and texts are saved.

## Layout

```
Game.cs            pure rules: players, opening order, turns, endings, picks, history, parsers, lines
Plugin.cs          chat reading, the send queue, party import, alerts
MainWindow.cs      Game / History / Rules & Ad tabs
Configuration.cs   saved settings and texts
StripDeathroll14.Tests/   xUnit tests over Game.cs
```
