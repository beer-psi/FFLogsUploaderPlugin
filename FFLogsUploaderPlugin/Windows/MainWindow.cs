using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Utility;
using FFLogsUploaderPlugin.Ipc;

namespace FFLogsUploaderPlugin.Windows;

public partial class MainWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private readonly FileDialogManager fileDialogManager = new();
    private readonly IINACTIpc iinact;
    
    private string parserStartErrorMessage = string.Empty;
    
    private int selectedGuildIndex;
    private int selectedRegionIndex;
    private int selectedVisibilityIndex;
    
    private long SelectedGuildValue => plugin.FfLogs.User?.GuildSelectItems[selectedGuildIndex].Value ?? 0L;
    private long SelectedRegionValue => plugin.FfLogs.User?.RegionOrServerSelectItems[selectedRegionIndex].Value ?? 0L;
    private long SelectedVisibilityValue =>
        plugin.FfLogs.User?.ReportVisibilitySelectItems[selectedVisibilityIndex].Value ?? 0L;
    
    private string reportDescription = string.Empty;
    
    private bool AnyOperationInProgress => liveLoggingStatus == OperationStatus.InProgress
                                           || uploadALogStatus == OperationStatus.InProgress
                                           || splitALogStatus == OperationStatus.InProgress;

    // We give this window a hidden ID using ##.
    // The user will see "My Amazing Window" as window title,
    // but for ImGui the ID is "My Amazing Window##With a hidden ID"
    public MainWindow(Plugin plugin)
        : base("FFLogs Uploader###FFLogsMainWindow", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(375, 330),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };
        SizeCondition = ImGuiCond.FirstUseEver;
        
        this.plugin = plugin;
        iinact = new IINACTIpc(Plugin.PluginInterface);
        
        SetOptionsFromConfiguration();
        DoAutomaticLogin();
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }

    private enum OperationStatus {
        Idle,
        InProgress,
    }

    public override void Draw()
    {
        if (plugin.FfLogs.User == null)
        {
            DrawLoginScreen();
            return;
        }

        fileDialogManager.Draw();
        
        using var tabBar = ImRaii.TabBar("FFLogsTabs");
        if (tabBar.Success)
        {
            using (var liveLogTabItem = ImRaii.TabItem("Live Log"))
            {
                if (liveLogTabItem.Success)
                {
                    DrawLiveLogTab();
                }
            }

            using (var uploadALogTabItem = ImRaii.TabItem("Upload a Log"))
            {
                if (uploadALogTabItem.Success)
                {
                    DrawUploadALogTab();
                }
            }

            using (var splitALogTabItem = ImRaii.TabItem("Split a Log"))
            {
                if (splitALogTabItem.Success)
                {
                    DrawSplitALogTab();
                }
            }

            using (var settingsTabItem = ImRaii.TabItem("Settings"))
            {
                if (settingsTabItem.Success)
                {
                    DrawSettingsTab();
                }
            }
        }
    }

    private bool DrawParserStatus()
    {
        if (!parserStartErrorMessage.IsNullOrWhitespace())
        {
            ImGui.TextColored(new Vector4(1, 0.3f, 0.3f, 1), $"Parser failed to load, please check Dalamud logs (/xllog): {parserStartErrorMessage}");
            return false;
        } 
        
        if (!plugin.FfLogs.LogParser.Started)
        {
            ImGui.Text("Loading parser...");
            return false;
        }

        return true;
    }
    
    private void DrawSharedUploadOptions()
    {
        var guildNames = plugin.FfLogs.User!.GuildSelectItems.Select(item => item.Label).ToArray();
        var regionNames = plugin.FfLogs.User!.RegionOrServerSelectItems.Select(item => item.Label).ToArray();
        var visibilityNames = plugin.FfLogs.User!.ReportVisibilitySelectItems.Select(item => item.Label).ToArray();
        
        ImGui.Text("Guild to upload to:");
        ImGui.SameLine();
        
        ImGui.SetNextItemWidth(150);
        if (ImGui.Combo("##guild", ref selectedGuildIndex, guildNames))
        {
            plugin.Configuration.SelectedGuildValue = SelectedGuildValue;
            plugin.Configuration.Save();
        }
        ImGui.SameLine();

        if (plugin.FfLogs.User!.GuildSelectItems[selectedGuildIndex].Value == -1)
        {
            ImGui.SetNextItemWidth(60);
            if (ImGui.Combo("##region", ref selectedRegionIndex, regionNames))
            {
                plugin.Configuration.SelectedRegionValue = SelectedRegionValue;
                plugin.Configuration.Save();
            }
            ImGui.SameLine();
        }
        
        ImGui.SetNextItemWidth(80);
        if (ImGui.Combo("##visibility", ref selectedVisibilityIndex, visibilityNames))
        {
            plugin.Configuration.SelectedVisibilityValue = SelectedVisibilityValue;
            plugin.Configuration.Save();
        }

        ImGui.Spacing();
        ImGui.Text("Enter a description for the report:");
        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("##description", ref reportDescription);
    }
    
    internal void StartParser()
    {
        Task.Run(() => plugin.FfLogs.StartParserAsync(false, false, false))
            .ContinueWith(task =>
            {
                if (task.Exception != null)
                {
                    Plugin.Log.Error(task.Exception, "Loading parser failed");
                    parserStartErrorMessage = task.Exception.InnerExceptions.FirstOrDefault(task.Exception).Message;
                    return;
                }

                Task.Run(plugin.FfLogs.LogParser.GetParserVersionAsync).ContinueWith(task1 =>
                {
                    if (task1.Exception != null)
                    {
                        Plugin.Log.Error(task1.Exception, "Getting plugin version failed");
                        parserStartErrorMessage = task1.Exception.InnerExceptions.FirstOrDefault(task1.Exception).Message;
                        return;
                    }

                    Plugin.Log.Information("Parser version {0} loaded", task1.Result);
                });
            });
    }
    
    private static bool DrawActionButtonAndMessages(string buttonLabel, bool isButtonDisabled, string progressMessage, string errorMessage)
    {
        bool result;
        using (ImRaii.Disabled(isButtonDisabled))
        {
            result = ImGui.Button(buttonLabel);
        }
        
        if (!progressMessage.IsNullOrWhitespace())
        {
            ImGui.SameLine();
            ImGui.Text(progressMessage);
        }

        if (!errorMessage.IsNullOrWhitespace())
        {
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(1, 0.3f, 0.3f, 1), errorMessage);
        }

        return result;
    }

    private string GetDialogStartPath(string logFileOrFolder)
    {
        if (!logFileOrFolder.IsNullOrWhitespace())
        {
            if (File.Exists(logFileOrFolder)
                && Path.GetDirectoryName(logFileOrFolder) is { } folder
                && Directory.Exists(folder))
            {
                return folder;
            }

            if (Directory.Exists(logFileOrFolder))
            {
                return logFileOrFolder;
            }
        }

        if (iinact.IsActive() && iinact.GetLogFilePath() is { } iinactLogDirectory)
        {
            Plugin.Log.Debug("IINACT is active, opening file browser to IINACT log directory {0}", iinactLogDirectory);
            return iinactLogDirectory;
        }
        
        // General default locations for log files:
        // - %APPDATA%\Advanced Combat Tracker\FFXIVLogs
        // - Documents/IINACT

        var actFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Advanced Combat Tracker",
            "FFXIVLogs"
        );

        if (Directory.Exists(actFolder))
        {
            return actFolder;
        }

        var documentsFolder = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var iinactFolder = Path.Combine(documentsFolder, "IINACT");

        return Directory.Exists(iinactFolder) ? iinactFolder : documentsFolder;
    }
}
