using System;
using Dalamud.Configuration;

namespace FFLogsUploaderPlugin;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 0;

    public string FfLogsEmail { get; set; } = string.Empty;
    public string FfLogsPassword { get; set; } = string.Empty;
    public bool FfLogsAutomaticLogin { get; set; } = false;

    public long SelectedGuildValue { get; set; }
    public long SelectedRegionValue { get; set; }
    public long SelectedVisibilityValue { get; set; }

    public string LiveLogFolder { get; set; } = string.Empty;
    public bool IncludeEntireFileInReport { get; set; }
    
    public string LogFilePath { get; set; } = string.Empty;
    
    public bool DebugLogParserMessages { get; set; }
    public long DebugLogParserMessageLimit { get; set; } = 4096;

    // The below exists just to make saving less cumbersome
    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}
