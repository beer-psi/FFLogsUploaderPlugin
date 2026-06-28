using System;
using Dalamud.Configuration;

namespace FFLogsUploaderPlugin;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    public string FfLogsEmail { get; set; } = string.Empty;
    public string FfLogsPassword { get; set; } = string.Empty;
    public bool FfLogsAutomaticLogin { get; set; } = false;

    public long SelectedGuildValue { get; set; } = -1; // Personal Logs
    public long SelectedRegionValue { get; set; } = 1; // NA
    public long SelectedVisibilityValue { get; set; } = 0; // Public

    public string LiveLogFolder { get; set; } = string.Empty;
    public bool IncludeEntireFileInReport { get; set; } = false;
    
    public string LogFilePath { get; set; } = string.Empty;

    public bool AutomaticallyCallDutyWipe { get; set; } = false;
    public bool StartLiveLoggingWhenDutyStarts { get; set; } = false;
    public bool StopLiveLoggingWhenDutyEnds { get; set; } = false;

    // The below exists just to make saving less cumbersome
    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}
