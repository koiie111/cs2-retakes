using System.Text.Json;
using CounterStrikeSharp.API.Modules.Utils;

using RetakesPlugin.Models;
using RetakesPlugin.Utils;

namespace RetakesPlugin.Services;

public class MapConfigService
{
    private string _mapName;
    private readonly string _mapConfigDirectory;
    private string MapConfigPath => Path.Combine(_mapConfigDirectory, $"{_mapName}.json");
    private MapConfigData? _mapConfigData;
    private readonly JsonSerializerOptions _jsonOptions;

    public MapConfigService(string moduleDirectory, string mapName, JsonSerializerOptions jsonOptions)
    {
        _mapName = mapName;
        _mapConfigDirectory = Path.Combine(moduleDirectory, "map_config");
        _mapConfigData = null;
        _jsonOptions = jsonOptions;
    }

    public void Load(bool isViaCommand = false)
    {
        if (!isViaCommand && !File.Exists(MapConfigPath))
        {
            _mapConfigData = new MapConfigData();
            Save();
            return;
        }

        TryLoad(_mapName);
    }

    public bool TryLoad(string mapName)
    {
        try
        {
            var path = Path.Combine(_mapConfigDirectory, $"{mapName}.json");
            var data = JsonSerializer.Deserialize<MapConfigData>(File.ReadAllText(path), _jsonOptions);
            if (data?.Spawns is null || data.Spawns.Any(spawn => spawn?.Vector is null || spawn.QAngle is null ||
                !Enum.IsDefined(spawn.Bombsite) || spawn.Team is not (CsTeam.Terrorist or CsTeam.CounterTerrorist)))
                throw new JsonException("Invalid map spawn data");

            _mapName = mapName;
            _mapConfigData = data;
            Logger.LogInfo("MapConfig", $"Map config loaded for {_mapName}");
            return true;
        }
        catch (Exception ex)
        {
            Logger.LogException("MapConfig", ex);
            return false;
        }
    }

    public List<Spawn> GetSpawnsClone()
    {
        if (_mapConfigData == null)
        {
            throw new Exception("Map config data is null");
        }

        return _mapConfigData.Spawns.ToList();
    }

    public bool AddSpawn(Spawn spawn)
    {
        _mapConfigData ??= new MapConfigData();

        if (_mapConfigData.Spawns.Any(existingSpawn =>
                existingSpawn.Vector == spawn.Vector && existingSpawn.Bombsite == spawn.Bombsite))
        {
            Logger.LogWarning("MapConfig", "Spawn already exists, avoiding duplication");
            return false;
        }

        _mapConfigData.Spawns.Add(spawn);
        Save();
        Load();

        Logger.LogInfo("MapConfig", "Spawn added successfully");
        return true;
    }

    public bool RemoveSpawn(Spawn spawn)
    {
        _mapConfigData ??= new MapConfigData();

        if (!_mapConfigData.Spawns.Any(existingSpawn =>
                existingSpawn.Vector == spawn.Vector && existingSpawn.Bombsite == spawn.Bombsite))
        {
            Logger.LogWarning("MapConfig", "Spawn doesn't exist, nothing to remove");
            return false;
        }

        _mapConfigData.Spawns.Remove(spawn);
        Save();
        Load();

        Logger.LogInfo("MapConfig", "Spawn removed successfully");
        return true;
    }

    private MapConfigData GetSanitizedMapConfigData()
    {
        if (_mapConfigData == null)
        {
            throw new Exception("Map config data is null");
        }

        _mapConfigData.Spawns = _mapConfigData.Spawns
            .GroupBy(spawn => new { spawn.Vector, spawn.Bombsite })
            .Select(group => group.First())
            .ToList();

        return _mapConfigData;
    }

    private void Save()
    {
        var jsonString = JsonSerializer.Serialize(GetSanitizedMapConfigData(), _jsonOptions);

        try
        {
            if (!Directory.Exists(_mapConfigDirectory))
            {
                Directory.CreateDirectory(_mapConfigDirectory);
            }

            File.WriteAllText(MapConfigPath, jsonString);
            Logger.LogDebug("MapConfig", $"Data written to {MapConfigPath}");
        }
        catch (IOException e)
        {
            Logger.LogError("MapConfig", $"Error writing to file: {e.Message}");
        }
    }

    public static bool IsLoaded(MapConfigService? mapConfig, string currentMap)
    {
        if (mapConfig == null || mapConfig._mapName != currentMap)
        {
            return false;
        }

        return true;
    }
}