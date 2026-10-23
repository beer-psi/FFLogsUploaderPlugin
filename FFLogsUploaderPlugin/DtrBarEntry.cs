using System;
using System.Linq;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Plugin.Services;
using FFLogsUploaderPlugin.FFLogs;

namespace FFLogsUploaderPlugin;

public class DtrBarEntry : IDisposable
{
    private readonly Plugin plugin;
    private readonly IDtrBarEntry dtrBarEntry;
    private LogParser.MeterFight? fight;
    private LogParser.MeterFightSegment? segment;
    private DateTimeOffset segmentStartTime = DateTimeOffset.MinValue;
    
    public DtrBarEntry(Plugin plugin)
    {
        this.plugin = plugin;
        
        dtrBarEntry = Plugin.DtrBar.Get("FFLogs Phase Timer");
        dtrBarEntry.OnClick = _ => plugin.ToggleMainUi();

        plugin.FFLogs.MetersCollected += OnMetersCollected;

        Plugin.Condition.ConditionChange += OnConditionChange;
        Plugin.Framework.Update += OnFrameworkUpdate;
        
        UpdateShown();
    }
    
    public void Dispose()
    {
        Plugin.Framework.Update -= OnFrameworkUpdate;
        Plugin.Condition.ConditionChange -= OnConditionChange;
        
        plugin.FFLogs.MetersCollected -= OnMetersCollected;

        dtrBarEntry.Remove();
        GC.SuppressFinalize(this);
    }

    internal void UpdateShown()
    {
        dtrBarEntry.Shown = plugin.Configuration.EngageTimerPerPhase && Plugin.Condition.Any(ConditionFlag.BoundByDuty,
            ConditionFlag.BoundByDuty56,
            ConditionFlag.BoundByDuty95);
    }

    private void OnConditionChange(ConditionFlag flag, bool newValue) => UpdateShown();

    // ReSharper disable once ParameterHidesMember
    private void OnMetersCollected(object? sender, LogParser.CollectMetersResponseData meters)
    {
        fight = meters.Fights.LastOrDefault();

        if (fight == null)
            return;

        var newSegment = fight.Segments.LastOrDefault();
        var newSegmentStartTime = newSegment != null
            ? DateTimeOffset.FromUnixTimeMilliseconds(newSegment.StartTime)
            : DateTimeOffset.MinValue;

        segment = newSegment;
        segmentStartTime = newSegmentStartTime;
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (!dtrBarEntry.Shown)
            return;
        
        // ReSharper disable once LocalVariableHidesMember
        var fight = this.fight;
        // ReSharper disable once LocalVariableHidesMember
        var segment = this.segment;

        if (fight == null)
        {
            dtrBarEntry.Text = "Unknown - 00:00";
            return;
        }

        var state = segment?.State ?? fight.State;
        var isInCombat = Plugin.Condition[ConditionFlag.InCombat]
                      || Plugin.PartyList.Any(actor => actor.GameObject is ICharacter character &&
                                                       (character.StatusFlags & StatusFlags.InCombat) != 0);
        var isStateFinal = state != "inprogress";
        var startTime = segmentStartTime;
        var endTime = isInCombat || !isStateFinal
                          ? DateTimeOffset.UtcNow
                          : DateTimeOffset.FromUnixTimeMilliseconds(segment?.EndTime ?? fight.EndTime);
        var duration = endTime - startTime;
        
        var encounterName = segment?.Encounter.Name ?? fight.Encounter.Name;

        dtrBarEntry.Text = $"{encounterName} - {duration:mm\\:ss}";
    }
}
