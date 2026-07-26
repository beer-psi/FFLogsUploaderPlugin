using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Utility;

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
        ImGui.Spacing();
        if (plugin.FfLogs.User is { } user)
        {
            ImGui.Text($"Logged in as {user.User.UserName}");  
            
            ImGui.SameLine();
            
            // Disable logging out if logging is in operation or if the parser has not finished loading
            // (either successfully or failed)
            using (ImRaii.Disabled(AnyOperationInProgress ||
                                   (!plugin.FfLogs.LogParser.Started && parserStartErrorMessage.IsNullOrWhitespace())))
            {
                if (ImGui.Button("Log out"))
                {
                    email = string.Empty;
                    password = string.Empty;
                    automaticLogin = false;

                    Task.Run(plugin.FfLogs.LogoutAsync);
                }
            }
        }
        else
        {
            ImGui.Text("Currently not logged in.");
        }
        
        
        using (ImRaii.Disabled(AnyOperationInProgress))
        {
            if (ImGui.Checkbox("Start live logging when entering duty", ref startLiveLoggingWhenDutyStarts))
            {
                plugin.Configuration.StartLiveLoggingWhenDutyStarts = startLiveLoggingWhenDutyStarts;
                plugin.Configuration.Save();
            }

            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            {
                ImGui.SetTooltip("Covers dungeons, trials, raids, alliance raids, chaotic alliance raids, ultimate raids.\nUnrestricted parties do not automatically start live logging, but duty support currently will.\nOptions are taken from the Live Log tab, except \"Include entire file in report\"\nwill always be disabled, and description will always be empty.\nMay have issues with unsupported dungeons.");
            }

            if (ImGui.Checkbox("Stop live logging 5 seconds after leaving duty", ref stopLiveLoggingWhenDutyEnds))
            {
                plugin.Configuration.StopLiveLoggingWhenDutyEnds = stopLiveLoggingWhenDutyEnds;
                plugin.Configuration.Save();
            }

            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            {
                ImGui.SetTooltip("The delay is necessary to allow ACT to finish writing logs, and for the uploader to finish parsing them.");
            }

            if (ImGui.Checkbox("Automatically call wipes when live logging", ref automaticallyCallDutyWipe))
            {
                plugin.Configuration.AutomaticallyCallDutyWipe = automaticallyCallDutyWipe;
                plugin.Configuration.Save();
            }
        }
    }
}
