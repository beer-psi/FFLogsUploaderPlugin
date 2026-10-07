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
    
    public DtrBarEntry(Plugin plugin)
    {
        this.plugin = plugin;
        dtrBarEntry = Plugin.DtrBar.Get("FFLogs Phase Timer");
        dtrBarEntry.OnClick = _ => plugin.ToggleMainUi();

        Plugin.Condition.ConditionChange += OnConditionChange;
        Plugin.Framework.Update += OnFrameworkUpdate;
        
        UpdateShown();
    }
    
    public void Dispose()
    {
        Plugin.Framework.Update -= OnFrameworkUpdate;
        Plugin.Condition.ConditionChange -= OnConditionChange;

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
    public void SetFightAndSegment(LogParser.MeterFight fight, LogParser.MeterFightSegment? segment)
    {
        this.fight = fight;
        this.segment = segment;
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (!dtrBarEntry.Shown)
            return;

        if (fight == null)
        {
            dtrBarEntry.Text = "Unknown - 00:00";
            return;
        }

        var encounterName = segment?.Encounter.Name ?? fight.Encounter.Name;
        var startTime =
            DateTimeOffset.FromUnixTimeMilliseconds(segment?.StartTime ?? fight.StartTime);
        var isInCombat = Plugin.Condition[ConditionFlag.InCombat]
                      || Plugin.PartyList.Any(actor => actor.GameObject is ICharacter character &&
                                                       (character.StatusFlags & StatusFlags.InCombat) != 0);
        var endTime = isInCombat
                          ? DateTimeOffset.UtcNow
                          : DateTimeOffset.FromUnixTimeMilliseconds(segment?.EndTime ?? fight.EndTime);
        var duration = endTime - startTime;

        dtrBarEntry.Text = $"{encounterName} - {duration:mm\\:ss}";
    }
}
