using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Entities;
using CounterStrikeSharp.API.Modules.Utils;

using RetakesPlugin.Utils;

namespace RetakesPlugin.Managers;

public class BreakerManager
{
    private readonly Dictionary<string, string> _entityActions = new(StringComparer.Ordinal);
    private readonly HashSet<uint> _entities = [];
    private bool _indexed;

    private static readonly HashSet<string> MapsWithPropDynamic = [
        "de_vertigo",
        "de_nuke",
        "de_mirage"
    ];

    private static readonly HashSet<string> MapsWithFuncButton = ["de_nuke"];

    public BreakerManager(bool? shouldBreakBreakables, bool? shouldOpenDoors)
    {
        if (shouldBreakBreakables ?? false)
        {
            _entityActions.Add("func_breakable", "Break");
            _entityActions.Add("func_breakable_surf", "Break");
            _entityActions.Add("prop.breakable.01", "Break");
            _entityActions.Add("prop.breakable.02", "Break");

            if (MapsWithPropDynamic.Contains(Server.MapName))
            {
                _entityActions.Add("prop_dynamic", "Break");
            }

            if (MapsWithFuncButton.Contains(Server.MapName))
            {
                _entityActions.Add("func_button", "Kill");
            }

            Logger.LogInfo("BreakerManager", "Breakables enabled");
        }

        if (shouldOpenDoors ?? false)
        {
            _entityActions.Add("prop_door_rotating", "open");
            Logger.LogInfo("BreakerManager", "Door opening enabled");
        }
    }

    public void OnSpawn(CEntityInstance entity)
    {
        if (_entityActions.Count != 0 && entity.IsValid &&
            entity.DesignerName is { } name && _entityActions.ContainsKey(name))
            _entities.Add(entity.EntityHandle.Raw);
    }

    public void OnDelete(CEntityInstance entity)
    {
        if (_entities.Count != 0)
            _entities.Remove(entity.EntityHandle.Raw);
    }

    public void Handle()
    {
        if (_entityActions.Count == 0)
            return;

        if (!_indexed)
        {
            foreach (var entity in Utilities.GetAllEntities())
                OnSpawn(entity);
            _indexed = true;
        }

        // Break and Kill may delete entities synchronously.
        foreach (var handle in _entities.ToArray())
        {
            if (EntitySystem.GetEntityByHandle(handle) is not { } pointer || pointer == IntPtr.Zero)
            {
                _entities.Remove(handle);
                continue;
            }

            var entity = new CBaseEntity(pointer);
            if (entity.IsValid && entity.DesignerName is { } name && _entityActions.TryGetValue(name, out var action))
                entity.AcceptInput(action);
        }
    }
}
