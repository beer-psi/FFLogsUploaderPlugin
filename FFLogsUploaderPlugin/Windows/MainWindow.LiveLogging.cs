using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Utility;

namespace FFLogsUploaderPlugin.Windows;

public partial class MainWindow
{
    private OperationStatus liveLoggingStatus = OperationStatus.Idle;
    
    private string logFolder = string.Empty;
    private bool includeEntireFileInReport;
    private string liveLogProgressMessage = string.Empty;
    private Progress<string>? liveLogProgress;
    private string liveLogErrorMessage = string.Empty;
    private string liveLogReportCode = string.Empty;
    
    private void DrawLiveLogTab()
    {
        using (ImRaii.Disabled(AnyOperationInProgress))
        {
            ImGui.Spacing();
            ImGui.Text("Folder ACT writes log files to:");
        
            ImGui.SetNextItemWidth(-80);
            ImGui.InputText("##logFolder", ref logFolder);
            ImGui.SameLine();
            if (ImGui.Button("Browse##browseLogFolder"))
                fileDialogManager.OpenFolderDialog("Select Log Folder",
                                                   (success, path) =>
                                                   {
                                                       if (success && !path.IsNullOrWhitespace())
                                                           logFolder = path;

                                                       plugin.Configuration.LiveLogFolder = logFolder;
                                                       plugin.Configuration.Save();
                                                   },
                                                   GetDialogStartPath(logFolder));
        
            ImGui.Spacing();
            DrawSharedUploadOptions();

            ImGui.Spacing();
            if (ImGui.Checkbox("Include entire file in report", ref includeEntireFileInReport))
            {
                plugin.Configuration.IncludeEntireFileInReport = includeEntireFileInReport;
                plugin.Configuration.Save();
            }

            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            {
                ImGui.SetTooltip("Uploads the latest log file from the beginning; otherwise, only logs added\nsince starting live logging will be uploaded.");
            }
        }

        // Keep this interactable if live logging is active so the user can stop it.
        ImGui.Spacing();
        if (DrawActionButtonAndMessages(
                plugin.FfLogs.IsLiveLogging ? "Stop" : "Start",
                uploadALogStatus == OperationStatus.InProgress || splitALogStatus == OperationStatus.InProgress,
                liveLogProgressMessage,
                liveLogErrorMessage)
            )
        {
            if (plugin.FfLogs.IsLiveLogging)
                plugin.FfLogs.StopLiveLogging();
            else
                StartLiveLogging();
        }
        
        if (!liveLogReportCode.IsNullOrWhitespace())
        {
            ImGui.Spacing();
            ImGui.Text("Report created.");
            
            ImGui.SameLine();
            if (ImGui.Button("Copy report link"))
            {
                ImGui.SetClipboardText($"https://www.fflogs.com/reports/{liveLogReportCode}");
            }

            ImGui.SameLine();
            if (ImGui.Button("Open report link"))
            {
                Task.Run(() => Process.Start(new ProcessStartInfo
                {
                    FileName = $"https://www.fflogs.com/reports/{liveLogReportCode}", UseShellExecute = true
                }));
            }
        }
    }
    
    internal void StartLiveLogging(bool isAutomaticOperation = false)
    {
        liveLoggingStatus = OperationStatus.InProgress;
        liveLogReportCode = string.Empty;
        liveLogProgressMessage = string.Empty;
        liveLogErrorMessage = string.Empty;

        if (logFolder.IsNullOrWhitespace())
        {
            liveLoggingStatus = OperationStatus.Idle;
            liveLogErrorMessage = "Path to log folder is missing.";
            return;
        }

        if (!Directory.Exists(logFolder))
        {
            liveLoggingStatus = OperationStatus.Idle;
            liveLogErrorMessage = "Log folder does not exist or is a file.";
            return;
        }
        
        var guildId = SelectedGuildValue;
        var visibility = SelectedVisibilityValue;
        var region = SelectedRegionValue;

        if (liveLogProgress == null)
        {
            liveLogProgress = new Progress<string>();
            liveLogProgress.ProgressChanged += (_, args) => { liveLogProgressMessage = args; };
        }

        // It doesn't make a lot of sense to upload the entire log file every time if you're going to have 
        // live logging start and stop every duty, hence includeEntireFileInReport && !isAutomaticOperation
        plugin.FfLogs.StartLiveLoggingAsync(logFolder, region, visibility, guildId == -1 ? null : guildId, reportDescription,
                                       includeEntireFileInReport && !isAutomaticOperation, liveLogProgress,
                                       reportCode => OnLiveLoggingReportCreated(reportCode, isAutomaticOperation))
              .ContinueWith(task =>
              {
                  liveLoggingStatus = OperationStatus.Idle;
                  
                  if (task.Exception != null)
                  {
                      Plugin.Log.Error(task.Exception, "Live logging operation failed");
                      liveLogProgressMessage = string.Empty;
                      liveLogErrorMessage = task.Exception.InnerExceptions.FirstOrDefault(task.Exception)
                                                .Message;
                  }
              });
    }

    private void OnLiveLoggingReportCreated(string reportCode, bool isAutomaticOperation)
    {
        liveLogReportCode = reportCode;

        if (isAutomaticOperation)
            Plugin.ChatGui.Print($"[FF Logs Uploader] Automatic live logging report created: https://www.fflogs.com/reports/{reportCode}");
    }
}
