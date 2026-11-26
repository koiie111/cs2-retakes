using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Timers;
using RetakesPluginShared;
using System.Text.Json;
using System.Runtime.CompilerServices;

using RetakesPlugin.Configs;
using RetakesPlugin.Configs.JsonConverters;
using RetakesPlugin.Events;
using RetakesPlugin.Managers;
using RetakesPlugin.Modules;
using RetakesPlugin.Services;
using RetakesPlugin.Utils;

using RetakesPlugin.Commands.Admin;
using RetakesPlugin.Commands.MapConfig;
using RetakesPlugin.Commands.Player;
using RetakesPlugin.Commands.SpawnEditor;

namespace RetakesPlugin;

[MinimumApiVersion(369)]
public class RetakesPlugin : BasePlugin, IPluginConfig<BaseConfigs>
{
    public const string Version = "3.1.2";

    #region Plugin Info
    public override string ModuleName => "Retakes Plugin";
    public override string ModuleVersion => Version;
    public override string ModuleAuthor => "B3none";
    public override string ModuleDescription => "https://github.com/b3none/cs2-retakes";
    #endregion

    #region Configuration
    public required BaseConfigs Config { get; set; }

    public void OnConfigParsed(BaseConfigs config)
    {
        Config = config;
        Utils.Logger.Initialize(Config.Debug.IsDebugMode);
        Utils.Logger.LogInfo("Main", "Configuration parsed successfully");
    }
    #endregion

    #region Services & Managers
    private readonly Random _random = new();
    private readonly JsonSerializerOptions _jsonOptions;
    private GameManager? _gameManager;
    private SpawnManager? _spawnManager;
    private BreakerManager? _breakerManager;
    private MapConfigService? _mapConfigService;
    private AllocationService? _allocationService;
    private AnnouncementService? _announcementService;
    private RoundEventHandlers? _roundEventHandlers;
    private PlayerEventHandlers? _playerEventHandlers;

    public MapConfigService? MapConfigService => _mapConfigService;
    public SpawnManager? SpawnManager => _spawnManager;
    #endregion

    #region Commands
    // Admin Commands
    private ForceBombsiteCommand? _forceBombsiteCommand;
    private ForceBombsiteStopCommand? _forceBombsiteStopCommand;
    private ScrambleCommand? _scrambleCommand;
    private DebugQueuesCommand? _debugQueuesCommand;

    // Map Config Commands
    private MapConfigCommand? _mapConfigCommand;
    private MapConfigsCommand? _mapConfigsCommand;

    // Player Commands
    private VoicesCommand? _voicesCommand;

    // Spawn Editor Commands
    private ShowSpawnsCommand? _showSpawnsCommand;
    private AddSpawnCommand? _addSpawnCommand;
    private RemoveSpawnCommand? _removeSpawnCommand;
    private NearestSpawnCommand? _nearestSpawnCommand;
    private HideSpawnsCommand? _hideSpawnsCommand;
    #endregion

    #region Capabilities
    public static PluginCapability<IRetakesPluginEventSender> RetakesPluginEventSenderCapability { get; } = new("retakes_plugin:event_sender");
    #endregion

    private static readonly PluginCapability<Func<CCSPlayerController, (int Position, int Playing, int Waiting)>> HudQueueCapability
        = new("retakes_plugin:hud_queue");
    private static readonly PluginCapability<Func<(int Playing, int Waiting, IReadOnlyDictionary<int, int> Positions)>> HudQueueSnapshotCapability
        = new("retakes_plugin:hud_queue_snapshot");
    private static readonly PluginCapability<Func<CCSPlayerController, string, bool>> HudCommandCapability
        = new("retakes_plugin:hud_command_allowed");
    private readonly List<Action> _clearHudProviders = [];
    private static readonly PluginCapability<Func<bool>> HudEnabledCapability = new("retakes_plugin:enabled");

    private bool CanRunHudCommand(CCSPlayerController player, string command)
    {
        if (!IsPluginEnabled || _gameManager is null || !PlayerHelper.IsValid(player))
        {
            return false;
        }

        var section = command switch
        {
            "css_forcebombsite" or "css_forcebombsitestop" or "css_scramble" or "css_debugqueues" => "Admin",
            "css_mapconfig" or "css_mapconfigs" => "MapConfig",
            "css_showspawns" or "css_addspawn" or "css_nearestspawn" or "css_removespawn" or "css_hidespawns" => "SpawnEditor",
            _ => null
        };
        return section is not null && AdminManager.PlayerHasPermissions(player,
            PlayerHelper.GetCommandPermission(Config, command, section, command == "css_scramble" ? "@css/admin" : "@css/root"));
    }

    #region State
    private readonly HashSet<CCSPlayerController> _hasMutedVoices = [];
    #endregion

    #region ConVars
    public FakeConVar<bool> RetakesEnabledConVar = new("retakes_enabled", "Включает режим ретейка.", true);
    private bool _lastEnabledState = true;
    #endregion

    public bool IsPluginEnabled => RetakesEnabledConVar.Value;

    public RetakesPlugin()
    {
        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            Converters =
            {
                new VectorJsonConverter(),
                new QAngleJsonConverter()
            }
        };
    }

    public override void Load(bool hotReload)
    {
        Utils.Logger.LogInfo("Main", "Plugin loading...");

        RegisterListener<Listeners.OnMapStart>(OnMapStart);
        RegisterListener<Listeners.OnMapEnd>(() =>
        {
            GameRulesHelper.Reset();
            _breakerManager = null;
        });
        RegisterListener<Listeners.OnEntitySpawned>(entity => _breakerManager?.OnSpawn(entity));
        RegisterListener<Listeners.OnEntityDeleted>(entity => _breakerManager?.OnDelete(entity));
        AddCommandListener("jointeam", OnCommandJoinTeam);
        RegisterCommands();

        RetakesEnabledConVar.ValueChanged += OnRetakesEnabledChanged;

        // Consumers stay subscribed when the core reloads. CSS# keeps the first provider for each name.
        IRetakesPluginEventSender? sender = null;
        try { sender = RetakesPluginEventSenderCapability.Get(); }
        catch (KeyNotFoundException) { }
        if (sender is null)
        {
            var eventSender = new RetakesPluginEventSender();
            Capabilities.RegisterPluginCapability(RetakesPluginEventSenderCapability, () => eventSender);
        }

        PublishHudProvider(HudQueueCapability,
            player => _gameManager?.QueueManager.GetHudQueueState(player) ?? default);
        PublishHudProvider(HudQueueSnapshotCapability,
            () => _gameManager?.QueueManager.GetHudQueueSnapshot() ?? (0, 0, new Dictionary<int, int>()));
        PublishHudProvider(HudCommandCapability, CanRunHudCommand);
        PublishHudProvider(HudEnabledCapability, () => IsPluginEnabled);

        // Register event handlers
        RegisterEventHandler<EventPlayerConnectFull>(OnPlayerConnectFull);
        RegisterEventHandler<EventRoundPrestart>(OnRoundPreStart);
        RegisterEventHandler<EventRoundStart>(OnRoundStart);
        RegisterEventHandler<EventRoundPoststart>(OnRoundPostStart);
        RegisterEventHandler<EventRoundFreezeEnd>(OnRoundFreezeEnd);
        RegisterEventHandler<EventRoundEnd>(OnRoundEnd);
        RegisterEventHandler<EventPlayerSpawn>(OnPlayerSpawn);
        RegisterEventHandler<EventPlayerDeath>(OnPlayerDeath);
        RegisterEventHandler<EventBombPlanted>(OnBombPlanted, HookMode.Pre);
        RegisterEventHandler<EventBombDefused>(OnBombDefused);
        RegisterEventHandler<EventPlayerDisconnect>(OnPlayerDisconnect, HookMode.Pre);
        RegisterEventHandler<EventPlayerTeam>(OnPlayerTeam, HookMode.Pre);

        if (hotReload)
        {
            Utils.Logger.LogServer(Localizer["retakes.plugin.map_restarting"]);
            Server.ExecuteCommand($"map {Server.MapName}");
        }

        Utils.Logger.LogInfo("Main", "Plugin loaded successfully");
    }

    private void PublishHudProvider<T>(PluginCapability<T> capability, T implementation) where T : class
    {
        // A process-wide slot lets the original CSS# supplier follow the live plugin instance.
        var key = "cs2-retakes/" + capability.Name;
        if (AppContext.GetData(key) is not StrongBox<T?> slot)
        {
            slot = new();
            AppContext.SetData(key, slot);
            Capabilities.RegisterPluginCapability(capability, () => slot.Value!);
        }

        slot.Value = implementation;
        _clearHudProviders.Add(() => slot.Value = null);
    }

    #region Map Initialization
    private void OnMapStart(string mapName)
    {
        Utils.Logger.LogInfo("MapStart", $"Map started: {mapName}");

        GameRulesHelper.Reset();
        SpawnService.Reset();

        AddTimer(1.0f, () =>
        {
            if (IsPluginEnabled)
            {
                ServerHelper.ExecuteRetakesConfiguration();
            }
        }, TimerFlags.STOP_ON_MAPCHANGE);

        InitializeServices(mapName);
    }

    private void OnRetakesEnabledChanged(object? sender, bool isEnabled)
    {
        if (isEnabled == _lastEnabledState)
        {
            return;
        }

        _lastEnabledState = isEnabled;

        if (isEnabled)
        {
            Utils.Logger.LogInfo("Main", "Retakes enabled via retakes_enabled convar");
            Server.PrintToChatAll($"{Localizer["retakes.prefix"]} {Localizer["retakes.plugin.enabled"]}");

            ServerHelper.ExecuteRetakesConfiguration();
            _gameManager?.QueueManager.SyncActivePlayersFromTeams();
            GameRulesHelper.RestartGame();
        }
        else
        {
            Utils.Logger.LogInfo("Main", "Retakes disabled via retakes_enabled convar");
            Server.PrintToChatAll($"{Localizer["retakes.prefix"]} {Localizer["retakes.plugin.disabled"]}");

            // Make sure we don't leave the server stuck in a paused warmup
            _gameManager?.CancelWaitingForPlayers();
            Server.ExecuteCommand("mp_warmup_pausetimer 0");
            ServerHelper.ExecuteRetakesUnloadConfiguration();
            _gameManager?.QueueManager.ClearAllQueues();
        }
    }

    private void InitializeServices(string mapName)
    {
        try
        {
            // A previous game manager may be holding the server in a paused warmup
            _gameManager?.CancelWaitingForPlayers();

            // Initialize MapConfigService
            _mapConfigService = new MapConfigService(ModuleDirectory, mapName, _jsonOptions);
            _mapConfigService.Load();

            // Initialize Managers
            _spawnManager = new SpawnManager(_mapConfigService);
            _allocationService = new AllocationService(_random);

            _gameManager = new GameManager(
                this,
                new QueueManager(
                    this,
                    Config.Game.MaxPlayers,
                    Config.Team.TerroristRatio,
                    Config.Queue.GetPriorityFlags(),
                    Config.Queue.GetImmunityFlags(),
                    Config.Team.ShouldForceEvenTeamsWhenPlayerCountIsMultipleOf10,
                    Config.Team.ShouldPreventTeamChangesMidRound
                ),
                Config.Team.RoundsToScramble,
                Config.Team.IsScrambleEnabled,
                Config.Queue.ShouldRemoveSpectators,
                Config.Team.IsBalanceEnabled,
                Config.Game.MinimumPlayers
            );

            _breakerManager = new BreakerManager(
                Config.Game.ShouldBreakBreakables,
                Config.Game.ShouldOpenDoors
            );

            _announcementService = new AnnouncementService(
                this,
                _random,
                _hasMutedVoices,
                Config.MapConfig.EnableBombsiteAnnouncementVoices,
                Config.MapConfig.EnableBombsiteAnnouncementCenter,
                Config.MapConfig.EnablePlantLocationAnnouncement
            );

            // Initialize Event Handlers
            _roundEventHandlers = new RoundEventHandlers(
                this,
                _gameManager,
                _spawnManager,
                _breakerManager,
                _allocationService,
                _announcementService,
                Config.Bomb.IsAutoPlantEnabled,
                Config.Game.EnableFallbackAllocation,
                Config.MapConfig.EnableFallbackBombsiteAnnouncement,
                _random
            );

            _playerEventHandlers = new PlayerEventHandlers(this, _gameManager, _hasMutedVoices);

            // Initialize Commands
            _forceBombsiteCommand = new ForceBombsiteCommand(this, _roundEventHandlers);
            _forceBombsiteStopCommand = new ForceBombsiteStopCommand(this, _roundEventHandlers);
            _scrambleCommand = new ScrambleCommand(this, _gameManager);
            _debugQueuesCommand = new DebugQueuesCommand(this, _gameManager);

            _mapConfigCommand = new MapConfigCommand(this, ModuleDirectory, (configName) =>
            {
                if (!_mapConfigService.TryLoad(configName))
                    return false;

                _spawnManager.CalculateMapSpawns();
                if (_showSpawnsCommand?.ShowingSpawnsForBombsite is { } site)
                    SpawnService.ShowSpawns(this, _mapConfigService.GetSpawnsClone(), site);
                return true;
            });
            _mapConfigsCommand = new MapConfigsCommand(this, ModuleDirectory);

            _voicesCommand = new VoicesCommand(this, Config, _hasMutedVoices);

            _showSpawnsCommand = new ShowSpawnsCommand(this);
            _addSpawnCommand = new AddSpawnCommand(this, _showSpawnsCommand);
            _removeSpawnCommand = new RemoveSpawnCommand(this, _showSpawnsCommand);
            _nearestSpawnCommand = new NearestSpawnCommand(this, _showSpawnsCommand);
            _hideSpawnsCommand = new HideSpawnsCommand(this, _showSpawnsCommand);

            // Set command references in event handlers
            _roundEventHandlers?.SetCommandReferences(_showSpawnsCommand);

            Utils.Logger.LogInfo("Services", "All services initialized successfully");
        }
        catch (Exception ex)
        {
            Utils.Logger.LogException("Services", ex);
        }
    }

    private void RegisterCommands()
    {
        // Admin Commands
        AddCommand("css_forcebombsite", "Выбрать одну точку для следующих раундов.", (player, info) => _forceBombsiteCommand?.OnCommand(player, info));
        AddCommand("css_forcebombsitestop", "Вернуть автоматический выбор точки.", (player, info) => _forceBombsiteStopCommand?.OnCommand(player, info));
        AddCommand("css_scramble", "Перемешать команды перед следующим раундом.", (player, info) => _scrambleCommand?.OnCommand(player, info));
        AddCommand("css_scrambleteams", "Перемешать команды перед следующим раундом.", (player, info) => _scrambleCommand?.OnCommand(player, info));
        AddCommand("css_debugqueues", "Вывести состояние очереди в консоль сервера.", (player, info) => _debugQueuesCommand?.OnCommand(player, info));

        // Map Config Commands
        AddCommand("css_mapconfig", "Загрузить конфигурацию карты из файла.", (player, info) => _mapConfigCommand?.OnCommand(player, info));
        AddCommand("css_setmapconfig", "Загрузить конфигурацию карты из файла.", (player, info) => _mapConfigCommand?.OnCommand(player, info));
        AddCommand("css_loadmapconfig", "Загрузить конфигурацию карты из файла.", (player, info) => _mapConfigCommand?.OnCommand(player, info));
        AddCommand("css_mapconfigs", "Показать доступные конфигурации карт.", (player, info) => _mapConfigsCommand?.OnCommand(player, info));
        AddCommand("css_viewmapconfigs", "Показать доступные конфигурации карт.", (player, info) => _mapConfigsCommand?.OnCommand(player, info));
        AddCommand("css_listmapconfigs", "Показать доступные конфигурации карт.", (player, info) => _mapConfigsCommand?.OnCommand(player, info));

        // Spawn Editor Commands
        AddCommand("css_showspawns", "Показать спавны на выбранной точке.", (player, info) => _showSpawnsCommand?.OnCommand(player, info));
        AddCommand("css_spawns", "Показать спавны на выбранной точке.", (player, info) => _showSpawnsCommand?.OnCommand(player, info));
        AddCommand("css_edit", "Показать спавны на выбранной точке.", (player, info) => _showSpawnsCommand?.OnCommand(player, info));
        AddCommand("css_add", "Добавить спавн на выбранной точке.", (player, info) => _addSpawnCommand?.OnCommand(player, info));
        AddCommand("css_addspawn", "Добавить спавн на выбранной точке.", (player, info) => _addSpawnCommand?.OnCommand(player, info));
        AddCommand("css_new", "Добавить спавн на выбранной точке.", (player, info) => _addSpawnCommand?.OnCommand(player, info));
        AddCommand("css_newspawn", "Добавить спавн на выбранной точке.", (player, info) => _addSpawnCommand?.OnCommand(player, info));
        AddCommand("css_remove", "Удалить ближайшую точку спавна.", (player, info) => _removeSpawnCommand?.OnCommand(player, info));
        AddCommand("css_removespawn", "Удалить ближайшую точку спавна.", (player, info) => _removeSpawnCommand?.OnCommand(player, info));
        AddCommand("css_delete", "Удалить ближайшую точку спавна.", (player, info) => _removeSpawnCommand?.OnCommand(player, info));
        AddCommand("css_deletespawn", "Удалить ближайшую точку спавна.", (player, info) => _removeSpawnCommand?.OnCommand(player, info));
        AddCommand("css_nearestspawn", "Переместиться к ближайшей точке спавна.", (player, info) => _nearestSpawnCommand?.OnCommand(player, info));
        AddCommand("css_nearest", "Переместиться к ближайшей точке спавна.", (player, info) => _nearestSpawnCommand?.OnCommand(player, info));
        AddCommand("css_hidespawns", "Выйти из редактора спавнов.", (player, info) => _hideSpawnsCommand?.OnCommand(player, info));
        AddCommand("css_done", "Выйти из редактора спавнов.", (player, info) => _hideSpawnsCommand?.OnCommand(player, info));
        AddCommand("css_exitedit", "Выйти из редактора спавнов.", (player, info) => _hideSpawnsCommand?.OnCommand(player, info));

        // Player Commands
        AddCommand("css_voices", "Включить или отключить голосовые объявления.", (player, info) => _voicesCommand?.OnCommand(player, info));

        Utils.Logger.LogInfo("Commands", "All commands registered successfully");
    }
    #endregion

    #region Event Handlers
    private HookResult OnPlayerConnectFull(EventPlayerConnectFull @event, GameEventInfo info)
    {
        if (!IsPluginEnabled)
        {
            return HookResult.Continue;
        }

        return _playerEventHandlers?.OnPlayerConnectFull(@event, info) ?? HookResult.Continue;
    }

    private HookResult OnRoundPreStart(EventRoundPrestart @event, GameEventInfo info)
    {
        if (!IsPluginEnabled)
        {
            return HookResult.Continue;
        }

        return _roundEventHandlers?.OnRoundPreStart(@event, info) ?? HookResult.Continue;
    }

    private HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        if (!IsPluginEnabled)
        {
            return HookResult.Continue;
        }

        return _roundEventHandlers?.OnRoundStart(@event, info) ?? HookResult.Continue;
    }

    private HookResult OnRoundPostStart(EventRoundPoststart @event, GameEventInfo info)
    {
        if (!IsPluginEnabled)
        {
            return HookResult.Continue;
        }

        return _roundEventHandlers?.OnRoundPostStart(@event, info) ?? HookResult.Continue;
    }

    private HookResult OnRoundFreezeEnd(EventRoundFreezeEnd @event, GameEventInfo info)
    {
        if (!IsPluginEnabled)
        {
            return HookResult.Continue;
        }

        return _roundEventHandlers?.OnRoundFreezeEnd(@event, info) ?? HookResult.Continue;
    }

    private HookResult OnRoundEnd(EventRoundEnd @event, GameEventInfo info)
    {
        if (!IsPluginEnabled)
        {
            return HookResult.Continue;
        }

        return _roundEventHandlers?.OnRoundEnd(@event, info) ?? HookResult.Continue;
    }

    private HookResult OnPlayerSpawn(EventPlayerSpawn @event, GameEventInfo info)
    {
        if (!IsPluginEnabled)
        {
            return HookResult.Continue;
        }

        return _playerEventHandlers?.OnPlayerSpawn(@event, info) ?? HookResult.Continue;
    }

    private HookResult OnPlayerDeath(EventPlayerDeath @event, GameEventInfo info)
    {
        if (!IsPluginEnabled)
        {
            return HookResult.Continue;
        }

        return _playerEventHandlers?.OnPlayerDeath(@event, info) ?? HookResult.Continue;
    }

    private HookResult OnBombPlanted(EventBombPlanted @event, GameEventInfo info)
    {
        if (!IsPluginEnabled)
        {
            return HookResult.Continue;
        }

        return _roundEventHandlers?.OnBombPlanted(@event, info) ?? HookResult.Continue;
    }

    private HookResult OnBombDefused(EventBombDefused @event, GameEventInfo info)
    {
        if (!IsPluginEnabled)
        {
            return HookResult.Continue;
        }

        return _roundEventHandlers?.OnBombDefused(@event, info) ?? HookResult.Continue;
    }

    private HookResult OnPlayerDisconnect(EventPlayerDisconnect @event, GameEventInfo info)
    {
        // Always run so we never leave stale players in the queues
        return _playerEventHandlers?.OnPlayerDisconnect(@event, info) ?? HookResult.Continue;
    }

    private HookResult OnPlayerTeam(EventPlayerTeam @event, GameEventInfo info)
    {
        if (!IsPluginEnabled)
        {
            return HookResult.Continue;
        }

        return _playerEventHandlers?.OnPlayerTeam(@event, info) ?? HookResult.Continue;
    }
    #endregion

    #region Command Handlers
    private HookResult OnCommandJoinTeam(CCSPlayerController? player, CommandInfo commandInfo)
    {
        if (!IsPluginEnabled)
        {
            return HookResult.Continue;
        }

        if (_gameManager == null)
        {
            Utils.Logger.LogWarning("Commands", "Game manager not loaded");
            return HookResult.Continue;
        }

        if (!PlayerHelper.IsValid(player) || commandInfo.ArgCount < 2 ||
            !Enum.TryParse<CounterStrikeSharp.API.Modules.Utils.CsTeam>(commandInfo.GetArg(1), out var toTeam))
        {
            return HookResult.Handled;
        }

        var fromTeam = player!.Team;
        Utils.Logger.LogDebug("Commands", $"[{player.PlayerName}] {fromTeam} -> {toTeam}");

        _gameManager.QueueManager.DebugQueues(true);
        var response = _gameManager.QueueManager.PlayerJoinedTeam(player, fromTeam, toTeam);
        _gameManager.QueueManager.DebugQueues(false);

        _gameManager.CheckMinimumPlayers();
        _gameManager.RestartGameIfEmpty();

        return response;
    }
    #endregion

    public override void Unload(bool hotReload)
    {
        Utils.Logger.LogInfo("Main", "Plugin unloading...");

        if (!hotReload)
        {
            ServerHelper.ExecuteRetakesUnloadConfiguration();
        }

        GameRulesHelper.Reset();
        _breakerManager = null;
        foreach (var clearProvider in _clearHudProviders)
            clearProvider();
        _clearHudProviders.Clear();
        base.Unload(hotReload);
    }
}
