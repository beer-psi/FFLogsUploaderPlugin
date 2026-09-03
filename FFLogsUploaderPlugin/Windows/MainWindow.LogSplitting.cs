using System.IO;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Utility;

namespace FFLogsUploaderPlugin.Windows;

public partial class MainWindow
{
    private OperationStatus splitALogStatus = OperationStatus.Idle;
    
    private string logFilePathToSplit = string.Empty;
    private string splitLogProgressMessage = string.Empty;
    private string splitLogErrorMessage = string.Empty;
    private bool splitLogGroupSameContent = false;
    
    private void DrawSplitALogTab()
    {
        using (ImRaii.Disabled(AnyOperationInProgress))
        {
            ImGui.Spacing();
            ImGui.Text("Log file to split:");
            
            ImGui.SetNextItemWidth(-80);
            ImGui.InputText("##logFileToSplit", ref logFilePathToSplit);
            ImGui.SameLine();
            if (ImGui.Button("Browse##browseLogFileToSplit"))
                fileDialogManager.OpenFileDialog("Select Log File",
                                                 "Log files{.log},All files{.*}",
                                                 (success, paths) =>
                                                 {
                                                     if (success && paths.Count > 0)
                                                         logFilePathToSplit = paths[0];
                                                 },
                                                 1,
                                                 GetDialogStartPath(logFilePathToSplit));

            if (ImGui.Checkbox("Split when instanced content changes", ref splitLogGroupSameContent))
            {
                plugin.Configuration.SplitLogGroupSameContent = splitLogGroupSameContent;
                plugin.Configuration.Save();
            }

            ImGuiComponents.HelpMarker(
                "Instead of the default behavior of splitting when area changes. For instance, when splitting a log file with Raid A -> Limsa -> Raid A -> Limsa -> Raid B, the default behavior would create 5 log files, one for each area, but this will only create two splits, one for Raid A and one for Raid B.");
        }
        
        ImGui.Spacing();

        if (DrawActionButtonAndMessages("Split", AnyOperationInProgress, splitLogProgressMessage, splitLogErrorMessage))
            DoSplitLogFile();
    }
    
    private void DoSplitLogFile()
    {
        splitALogStatus = OperationStatus.InProgress;
        splitLogProgressMessage = string.Empty;
        splitLogErrorMessage = string.Empty;
     
        if (logFilePathToSplit.IsNullOrWhitespace())
        {
            splitALogStatus = OperationStatus.Idle;
            splitLogErrorMessage = "Path to log file is missing.";
            return;
        }
     
        if (Path.GetFileName(logFilePathToSplit).StartsWith("Split-"))
        {
            splitALogStatus = OperationStatus.Idle;
            splitLogErrorMessage = "Cowardly refusing to split a split log file.";
            return;
        }
     
        if (!File.Exists(logFilePathToSplit))
        {
            splitALogStatus = OperationStatus.Idle;
            splitLogErrorMessage = "Log file does not exist, or is not a file.";
            return;
        }
     
        FfLogsManager.SplitLogFileAsync(logFilePathToSplit, splitLogGroupSameContent).ContinueWith(task =>
        {
            splitALogStatus = OperationStatus.Idle;
     
            if (task.Exception?.InnerExceptions.FirstOrDefault() is SplitLogException sle)
            {
                splitLogProgressMessage = string.Empty;
                splitLogErrorMessage = sle.Message;
            }
            else if (task.Exception != null)
            {
                Plugin.Log.Error(task.Exception, "Split log file failed");
                splitLogProgressMessage = string.Empty;
                splitLogErrorMessage = task.Exception.InnerExceptions.FirstOrDefault(task.Exception).Message;
            }
            else
            {
                splitLogProgressMessage = "Successfully split log file";
            }
        });
    }
}
