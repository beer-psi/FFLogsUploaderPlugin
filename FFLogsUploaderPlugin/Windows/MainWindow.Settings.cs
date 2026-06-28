using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace FFLogsUploaderPlugin.Windows;

public partial class MainWindow
{
    private bool automaticallyCallDutyWipe;
    private bool startLiveLoggingWhenDutyStarts;
    private bool stopLiveLoggingWhenDutyEnds;
    
    public void SetOptionsFromConfiguration()
    {
        email = plugin.Configuration.FfLogsEmail;
        password = plugin.Configuration.FfLogsPassword;
        automaticLogin = plugin.Configuration.FfLogsAutomaticLogin;
        logFilePath = plugin.Configuration.LogFilePath;
        logFolder = plugin.Configuration.LiveLogFolder;
        includeEntireFileInReport = plugin.Configuration.IncludeEntireFileInReport;
        automaticallyCallDutyWipe = plugin.Configuration.AutomaticallyCallDutyWipe;
        startLiveLoggingWhenDutyStarts = plugin.Configuration.StartLiveLoggingWhenDutyStarts;
        stopLiveLoggingWhenDutyEnds = plugin.Configuration.StopLiveLoggingWhenDutyEnds;

        if (plugin.FfLogs.User != null)
        {
            selectedGuildIndex =
                plugin.FfLogs.User!.GuildSelectItems.FindIndex(item => item.Value ==
                                                                       plugin.Configuration.SelectedGuildValue);
            selectedRegionIndex =
                plugin.FfLogs.User.RegionOrServerSelectItems.FindIndex(item => item.Value ==
                                                                               plugin.Configuration.SelectedRegionValue);
            selectedVisibilityIndex =
                plugin.FfLogs.User.ReportVisibilitySelectItems.FindIndex(item => item.Value ==
                                                                             plugin.Configuration.SelectedVisibilityValue);

            if (selectedGuildIndex == -1) selectedGuildIndex = 0;
            if (selectedRegionIndex == -1) selectedRegionIndex = 0;
            if (selectedVisibilityIndex == -1) selectedVisibilityIndex = 0;
        }
    }
    
    private void DrawSettingsTab()
    {
        using (ImRaii.Disabled(AnyOperationInProgress))
        {
            ImGui.Spacing();
            ImGui.Text($"Logged in as {plugin.FfLogs.User!.User.UserName}");

            ImGui.SameLine();
            if (ImGui.Button("Log out"))
            {
                email = string.Empty;
                password = string.Empty;
                automaticLogin = false;

                Task.Run(plugin.FfLogs.LogoutAsync);
            }
            
            if (ImGui.Checkbox("Start live logging when starting duty", ref startLiveLoggingWhenDutyStarts))
            {
                plugin.Configuration.StartLiveLoggingWhenDutyStarts = startLiveLoggingWhenDutyStarts;
                plugin.Configuration.Save();
            }
            
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Start live logging when the \"Duty Started\" flytext appears and the ring around spawn is removed.\nUses options configured in the Live Log tab.\nDoes not trigger when loading into a duty that was in progress, or from loading in after a disconnect.");    
            }

            if (ImGui.Checkbox("Stop live logging when leaving duty", ref stopLiveLoggingWhenDutyEnds))
            {
                plugin.Configuration.StopLiveLoggingWhenDutyEnds = stopLiveLoggingWhenDutyEnds;
                plugin.Configuration.Save();
            }

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("May occasionally give invalid logs e.g. when viewing post-clear cutscenes");
            }

            if (ImGui.Checkbox("Automatically call wipes when live logging", ref automaticallyCallDutyWipe))
            {
                plugin.Configuration.AutomaticallyCallDutyWipe = automaticallyCallDutyWipe;
                plugin.Configuration.Save();
            }
        }
    }
}
