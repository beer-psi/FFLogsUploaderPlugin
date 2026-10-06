using System;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Gui.Dtr;
using FFLogsUploaderPlugin.FFLogs;

namespace FFLogsUploaderPlugin;

public class DtrBarEntry : IDisposable
{
    private readonly Plugin plugin;
    private readonly IDtrBarEntry dtrBarEntry;
    
    public DtrBarEntry(Plugin plugin)
    {
        this.plugin = plugin;
        dtrBarEntry = Plugin.DtrBar.Get("FFLogs Phase Timer");
        dtrBarEntry.OnClick = _ => plugin.ToggleMainUi();

        Plugin.Condition.ConditionChange += OnConditionChange;
        
        UpdateShown();
    }
    
    public void Dispose()
    {
        Plugin.Condition.ConditionChange -= OnConditionChange;

        dtrBarEntry.Remove();
        GC.SuppressFinalize(this);
    }

    private void UpdateShown()
    {
        dtrBarEntry.Shown = plugin.Configuration.EngageTimerPerPhase && Plugin.Condition.Any(ConditionFlag.BoundByDuty,
            ConditionFlag.BoundByDuty56,
            ConditionFlag.BoundByDuty95);
    }

    private void OnConditionChange(ConditionFlag flag, bool newValue) => UpdateShown();

    public void Update(LogParser.MeterFight fight, LogParser.MeterFightSegment? segment)
    {
        if (!dtrBarEntry.Shown)
            return;
        
        var encounterName = segment?.Encounter.Name ?? fight.Encounter.Name;
        var duration = segment != null
            ? TimeSpan.FromMilliseconds(segment.EndTime - segment.StartTime)
            : TimeSpan.FromMilliseconds(fight.EndTime - fight.StartTime);

        dtrBarEntry.Text = $"{encounterName} - {duration:mm\\:ss}";
    }
}
