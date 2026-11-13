namespace OpenTribes.Server;

using OpenTribes.Game;
using OpenTribes.Script;

/// <summary>The query reply source of src/server/main.cpp, shared by the dedicated server and a hosted game (docs/net-protocol.md 6.1). The reply reads the live script state.</summary>
public static class GameInfoProvider
{
    /// <summary>Creates the provider.</summary>
    /// <param name="console">The script interpreter that holds the server variables.</param>
    /// <param name="game">The game state that holds the teams and players.</param>
    /// <returns>A function that builds the current server info.</returns>
    public static Func<GameInfo> Create(Interpreter console, GameState game) => () =>
    {
        var g = new GameInfo
        {
            Hostname = console.PeekVariable("$Server::HostName"),
            Password = console.PeekVariable("$Server::Password"),
            MaxPlayers = console.PeekVariable("$Server::MaxPlayers"),
            Info = console.PeekVariable("$Server::Info"),
            ModList = console.PeekVariable("$modList"),
            MissionType = console.PeekVariable("$Game::MissionType"),
            MissionName = console.PeekVariable("$MissionName"),
            Dedicated = Numbers.IsTrueString(console.PeekVariable("$Dedicated")),
            CpuText = console.PeekVariable("$CPU::estimatedSpeed"),
        };
        g.CpuSpeed = Numbers.BorlandAtoi(g.CpuText);
        g.TeamCount = game.TeamCount;
        g.TeamHeading = game.TeamScoreHeading;
        g.ClientHeading = game.ClientScoreHeading;
        for (var t = 0; t < game.TeamCount && t + 1 < game.Teams.Count; ++t)
            g.Teams.Add(new GameInfo.TeamRow(game.Teams[t + 1].Name, game.Teams[t + 1].Score, game.Teams[t + 1].SortValue));
        foreach (var id in game.Players.Humans())
            if (game.Players.FindHuman(id) is ClientRecord r)
                g.Players.Add(new GameInfo.PlayerRow { Id = (uint)id, Team = r.Team, Score = r.ScoreText, Sort = r.ScoreSort, Ping = r.Ping, Loss = r.Loss });
        return g;
    };
}
