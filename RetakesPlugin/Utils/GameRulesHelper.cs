using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Entities;
using CounterStrikeSharp.API.Modules.Entities.Constants;
using CounterStrikeSharp.API.Modules.Utils;

namespace RetakesPlugin.Utils;

public static class GameRulesHelper
{
    private static uint? _proxyHandle;

    public static void Reset() => _proxyHandle = null;

    public static CCSGameRules? GetGameRulesOrNull()
    {
        if (_proxyHandle is { } handle && EntitySystem.GetEntityByHandle(handle) is { } pointer &&
            pointer != IntPtr.Zero)
        {
            return new CCSGameRulesProxy(pointer).GameRules;
        }

        var gameRulesEntities = Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules");
        var gameRulesProxy = gameRulesEntities.FirstOrDefault();

        if (gameRulesProxy == null)
        {
            Reset();
            return null;
        }

        _proxyHandle = gameRulesProxy.EntityHandle.Raw;
        return gameRulesProxy.GameRules;
    }

    public static CCSGameRules GetGameRules()
    {
        var gameRules = GetGameRulesOrNull();

        if (gameRules == null)
        {
            throw new InvalidOperationException("Game rules are not available yet. The server may still be initializing.");
        }

        return gameRules;
    }

    public static void RestartGame()
    {
        var gameRules = GetGameRulesOrNull();
        if (gameRules == null)
        {
            Logger.LogWarning("GameRules", "Game rules not available, skipping game restart");
            return;
        }

        if (!gameRules.WarmupPeriod)
        {
            CheckRoundDone();
        }

        Server.ExecuteCommand("mp_restartgame 1");
    }

    public static void CheckRoundDone()
    {
        var (tHumanCount, ctHumanCount) = PlayerHelper.CountTeams(Utilities.GetPlayers());

        if (tHumanCount == 0 || ctHumanCount == 0)
        {
            TerminateRound(RoundEndReason.TerroristsWin);
        }
    }

    public static void TerminateRound(RoundEndReason roundEndReason)
    {
        try
        {
            GetGameRules().TerminateRound(0.1f, roundEndReason);
        }
        catch
        {
            Logger.LogWarning("GameRules",
                "Incorrect signature detected (Can't use TerminateRound), killing all alive players instead.");

            var alivePlayers = Utilities.GetPlayers()
                .Where(PlayerHelper.IsValid)
                .Where(player => player.PawnIsAlive)
                .ToList();

            foreach (var player in alivePlayers)
            {
                player.CommitSuicide(false, true);
            }
        }
    }

    public static double GetDistanceBetweenVectors(Vector v1, Vector v2)
    {
        var dx = v1.X - v2.X;
        var dy = v1.Y - v2.Y;

        return Math.Sqrt(Math.Pow(dx, 2) + Math.Pow(dy, 2));
    }
}