using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;

using RetakesPlugin.Utils;

namespace RetakesPlugin.Commands.MapConfig;

public class MapConfigCommand
{
    private readonly RetakesPlugin _plugin;
    private readonly string _moduleDirectory;
    private readonly Func<string, bool> _onMapConfigLoad;

    public MapConfigCommand(RetakesPlugin plugin, string moduleDirectory, Func<string, bool> onMapConfigLoad)
    {
        _plugin = plugin;
        _moduleDirectory = moduleDirectory;
        _onMapConfigLoad = onMapConfigLoad;
    }

    public void OnCommand(CCSPlayerController? player, CommandInfo commandInfo)
    {
        if (player != null && !PlayerHelper.IsValid(player))
        {
            return;
        }

        var commandName = commandInfo.GetArg(0);
        var requiredPermission = PlayerHelper.GetCommandPermission(_plugin.Config, commandName, "MapConfig");
        if (!AdminManager.PlayerHasPermissions(player, requiredPermission))
        {
            commandInfo.ReplyToCommand($"{_plugin.Localizer["retakes.prefix"]} {_plugin.Localizer["retakes.no_permissions"]}");
            return;
        }

        if (commandInfo.ArgCount < 2)
        {
            commandInfo.ReplyToCommand($"{_plugin.Localizer["retakes.prefix"]} {_plugin.Localizer["retakes.map_config.usage"]}");
            return;
        }

        var mapConfigDirectory = Path.Combine(_moduleDirectory, "map_config");

        if (!Directory.Exists(mapConfigDirectory))
        {
            commandInfo.ReplyToCommand($"{_plugin.Localizer["retakes.prefix"]} {_plugin.Localizer["retakes.map_config.none"]}");
            return;
        }

        var mapConfigFileName = commandInfo.GetArg(1).Trim();
        if (mapConfigFileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            mapConfigFileName = mapConfigFileName[..^5];
        }

        if (string.IsNullOrWhiteSpace(mapConfigFileName) || mapConfigFileName.IndexOfAny(['/', '\\', ':']) >= 0 ||
            mapConfigFileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            commandInfo.ReplyToCommand($"{_plugin.Localizer["retakes.prefix"]} {_plugin.Localizer["retakes.map_config.file_not_found"]}");
            return;
        }
        var mapConfigFilePath = Path.Combine(mapConfigDirectory, $"{mapConfigFileName}.json");

        if (!File.Exists(mapConfigFilePath))
        {
            commandInfo.ReplyToCommand($"{_plugin.Localizer["retakes.prefix"]} {_plugin.Localizer["retakes.map_config.file_not_found"]}");
            return;
        }

        if (!_onMapConfigLoad(mapConfigFileName))
        {
            commandInfo.ReplyToCommand($"{_plugin.Localizer["retakes.prefix"]} {_plugin.Localizer["retakes.map_config.invalid"]}");
            return;
        }

        commandInfo.ReplyToCommand($"{_plugin.Localizer["retakes.prefix"]} {_plugin.Localizer["retakes.map_config.loaded"]}");
        Logger.LogInfo("Commands", $"Map config '{mapConfigFileName}' loaded by {player?.PlayerName ?? "Console"}");
    }
}