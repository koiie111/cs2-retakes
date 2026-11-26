using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;

using RetakesPlugin.Models;
using RetakesPlugin.Services;
using RetakesPlugin.Utils;
using RetakesPluginShared.Enums;

namespace RetakesPlugin.Managers;

public class SpawnManager
{
    private readonly MapConfigService _mapConfigService;
    private readonly Dictionary<Bombsite, Dictionary<CsTeam, List<Spawn>>> _spawns = new();
    private readonly Dictionary<Bombsite, List<Spawn>> _planterSpawns = new();
    private readonly Random _random = new();

    public Spawn? CurrentPlanterSpawn { get; private set; }

    public SpawnManager(MapConfigService mapConfigService)
    {
        _mapConfigService = mapConfigService;
        CalculateMapSpawns();
    }

    public void CalculateMapSpawns()
    {
        _spawns.Clear();

        _spawns.Add(Bombsite.A, new Dictionary<CsTeam, List<Spawn>>()
        {
            { CsTeam.Terrorist, [] },
            { CsTeam.CounterTerrorist, [] }
        });
        _spawns.Add(Bombsite.B, new Dictionary<CsTeam, List<Spawn>>()
        {
            { CsTeam.Terrorist, [] },
            { CsTeam.CounterTerrorist, [] }
        });

        foreach (var spawn in _mapConfigService.GetSpawnsClone())
        {
            _spawns[spawn.Bombsite][spawn.Team].Add(spawn);
        }

        foreach (var (site, teams) in _spawns)
            _planterSpawns[site] = teams[CsTeam.Terrorist].FindAll(spawn => spawn.CanBePlanter);

        Logger.LogInfo("SpawnManager", "Map spawns calculated successfully");
    }

    public List<Spawn> GetSpawns(Bombsite bombsite, CsTeam? team = null)
    {
        if (_spawns[bombsite][CsTeam.Terrorist].Count == 0 &&
            _spawns[bombsite][CsTeam.CounterTerrorist].Count == 0)
        {
            Logger.LogWarning("SpawnManager", $"No spawns found for bombsite {bombsite}");
            return [];
        }

        if (team == null)
        {
            return _spawns[bombsite].SelectMany(entry => entry.Value).ToList();
        }

        return _spawns[bombsite][(CsTeam)team];
    }

    public CCSPlayerController? HandleRoundSpawns(Bombsite bombsite, HashSet<CCSPlayerController> players)
    {
        Logger.LogDebug("SpawnManager", $"Handling round spawns for bombsite {bombsite}");

        var spawns = _spawns[bombsite].ToDictionary(
            entry => entry.Key,
            entry => entry.Value.ToList()
        );

        var (tCount, ctCount) = PlayerHelper.CountTeams(players);

        if (ctCount > spawns[CsTeam.CounterTerrorist].Count ||
            tCount > spawns[CsTeam.Terrorist].Count)
        {
            Logger.LogError("SpawnManager",
                $"Not enough spawns for bombsite {bombsite}! CT: {ctCount}/{spawns[CsTeam.CounterTerrorist].Count}, T: {tCount}/{spawns[CsTeam.Terrorist].Count}");
            throw new Exception($"Not enough spawns in map config for Bombsite {bombsite}!");
        }

        var planterSpawns = _planterSpawns[bombsite];

        if (planterSpawns.Count == 0)
        {
            Logger.LogError("SpawnManager", $"No planter spawns found for bombsite {bombsite}");
            throw new Exception($"No planter spawns for Bombsite {bombsite}!");
        }

        var randomPlanterSpawn = planterSpawns[_random.Next(planterSpawns.Count)];
        CurrentPlanterSpawn = randomPlanterSpawn;
        spawns[CsTeam.Terrorist].Remove(randomPlanterSpawn);

        CCSPlayerController? planter = null;

        foreach (var player in PlayerHelper.Shuffle(players, _random))
        {
            if (!PlayerHelper.HasAlivePawn(player))
            {
                continue;
            }

            var team = player.Team;
            if (team != CsTeam.Terrorist && team != CsTeam.CounterTerrorist)
            {
                continue;
            }

            if (planter == null && team == CsTeam.Terrorist)
            {
                planter = player;
            }

            var available = spawns[team];
            if (player != planter && available.Count == 0)
            {
                continue;
            }

            var spawn = randomPlanterSpawn;
            if (player != planter)
            {
                var index = _random.Next(available.Count);
                spawn = available[index];
                available.RemoveAt(index);
            }

            player.Pawn.Value!.Teleport(spawn.Vector, spawn.QAngle, new Vector());
        }

        Logger.LogInfo("SpawnManager", $"Players moved to spawns. Planter: {planter?.PlayerName ?? "None"}");

        return planter;
    }
}