using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;

using RetakesPlugin.Utils;
using RetakesPlugin.Models;
using RetakesPlugin.Services;
using RetakesPlugin.Managers;
using RetakesPluginShared.Enums;

namespace RetakesPlugin.Commands.SpawnEditor;

public class AddSpawnCommand
{
    private readonly RetakesPlugin _plugin;
    private readonly ShowSpawnsCommand _showSpawnsCommand;

    public AddSpawnCommand(RetakesPlugin plugin, ShowSpawnsCommand showSpawnsCommand)
    {
        _plugin = plugin;
        _showSpawnsCommand = showSpawnsCommand;
    }

    public void OnCommand(CCSPlayerController? player, CommandInfo commandInfo)
    {
        if (!PlayerHelper.IsValid(player))
        {
            return;
        }

        var commandName = commandInfo.GetArg(0);
        var requiredPermission = PlayerHelper.GetCommandPermission(_plugin.Config, commandName, "SpawnEditor");
        if (!AdminManager.PlayerHasPermissions(player, requiredPermission))
        {
            commandInfo.ReplyToCommand($"{_plugin.Localizer["retakes.prefix"]} {_plugin.Localizer["retakes.no_permissions"]}");
            return;
        }

        if (_showSpawnsCommand.ShowingSpawnsForBombsite == null)
        {
            commandInfo.ReplyToCommand($"{_plugin.Localizer["retakes.prefix"]} {_plugin.Localizer["retakes.spawn.add.show_first"]}");
            return;
        }

        if (!PlayerHelper.HasAlivePawn(player))
        {
            commandInfo.ReplyToCommand($"{_plugin.Localizer["retakes.prefix"]} {_plugin.Localizer["retakes.spawn.alive_required"]}");
            return;
        }

        if (commandInfo.ArgCount < 2)
        {
            commandInfo.ReplyToCommand($"{_plugin.Localizer["retakes.prefix"]} {_plugin.Localizer["retakes.spawn.add.usage"]}");
            return;
        }

        var team = commandInfo.GetArg(1).ToUpper();
        if (team != "T" && team != "CT")
        {
            commandInfo.ReplyToCommand($"{_plugin.Localizer["retakes.prefix"]} {_plugin.Localizer["retakes.spawn.team_required"]}");
            return;
        }

        var canBePlanterInput = commandInfo.GetArg(2).ToUpper();
        if (!string.IsNullOrWhiteSpace(canBePlanterInput) && canBePlanterInput != "Y" && canBePlanterInput != "N")
        {
            commandInfo.ReplyToCommand($"{_plugin.Localizer["retakes.prefix"]} {_plugin.Localizer["retakes.spawn.planter_invalid"]}");
            return;
        }

        if (_plugin.SpawnManager == null || _plugin.MapConfigService == null)
        {
            commandInfo.ReplyToCommand($"{_plugin.Localizer["retakes.prefix"]} {_plugin.Localizer["retakes.not_ready"]}");
            return;
        }

        var spawns = _plugin.SpawnManager.GetSpawns((Bombsite)_showSpawnsCommand.ShowingSpawnsForBombsite);
        var closestDistance = 9999.9;

        foreach (var spawn in spawns)
        {
            var distance = GameRulesHelper.GetDistanceBetweenVectors(spawn.Vector, player!.PlayerPawn.Value!.AbsOrigin!);

            if (distance > 128.0 || distance > closestDistance)
            {
                continue;
            }

            closestDistance = distance;
        }

        if (closestDistance <= 72)
        {
            commandInfo.ReplyToCommand($"{_plugin.Localizer["retakes.prefix"]} {_plugin.Localizer["retakes.spawn.too_close"]}");
            return;
        }

        string? plantLocation = null;
        if (commandInfo.ArgCount > 3)
        {
            var plantLocationParts = new List<string>();
            for (var argIndex = 3; argIndex < commandInfo.ArgCount; argIndex++)
            {
                plantLocationParts.Add(commandInfo.GetArg(argIndex));
            }

            plantLocation = string.Join(" ", plantLocationParts).Trim();
            if (string.IsNullOrWhiteSpace(plantLocation))
            {
                plantLocation = null;
            }
        }

        var newSpawn = new Spawn(
            vector: player!.PlayerPawn.Value!.AbsOrigin!,
            qAngle: player!.PlayerPawn.Value!.AbsRotation!
        )
        {
            Team = team == "T" ? CsTeam.Terrorist : CsTeam.CounterTerrorist,
            CanBePlanter = team == "T" && (string.IsNullOrWhiteSpace(canBePlanterInput) ? player.PlayerPawn.Value.InBombZoneTrigger : canBePlanterInput == "Y"),
            Bombsite = (Bombsite)_showSpawnsCommand.ShowingSpawnsForBombsite
        };

        if (newSpawn.CanBePlanter)
        {
            newSpawn.PlantLocation = plantLocation;
        }
        else if (plantLocation != null)
        {
            commandInfo.ReplyToCommand($"{_plugin.Localizer["retakes.prefix"]} {_plugin.Localizer["retakes.spawn.plant_location_ignored"]}");
        }

        var didAddSpawn = _plugin.MapConfigService.AddSpawn(newSpawn);
        if (didAddSpawn)
        {
            SpawnService.ShowSpawn(_plugin, newSpawn);
            _plugin.SpawnManager.CalculateMapSpawns();
        }

        commandInfo.ReplyToCommand($"{_plugin.Localizer["retakes.prefix"]} {_plugin.Localizer[didAddSpawn ? "retakes.spawn.added" : "retakes.spawn.add_failed"]}");

        if (didAddSpawn)
        {
            Logger.LogInfo("Commands", $"{player.PlayerName} added spawn at bombsite {_showSpawnsCommand.ShowingSpawnsForBombsite}");
        }
    }
}