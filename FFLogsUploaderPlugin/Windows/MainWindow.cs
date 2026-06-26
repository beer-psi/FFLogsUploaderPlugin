using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Utility;
using FFLogsUploaderPlugin.FFLogs;
using FFLogsUploaderPlugin.Ipc;

namespace FFLogsUploaderPlugin.Windows;

public class MainWindow : Window, IAsyncDisposable
{
    private readonly Plugin plugin;
    private readonly FileDialogManager fileDialogManager = new();
    private readonly IINACTIpc iinact;

    private string email = string.Empty;
    private string password = string.Empty;
    private bool automaticLogin;
    
    private OperationStatus liveLoggingStatus = OperationStatus.Idle;
    private OperationStatus uploadALogStatus = OperationStatus.Idle;
    private OperationStatus splitALogStatus = OperationStatus.Idle;
    private bool AnyOperationInProgress => liveLoggingStatus == OperationStatus.InProgress
                                           || uploadALogStatus == OperationStatus.InProgress
                                           || splitALogStatus == OperationStatus.InProgress;
    
    private CancellationTokenSource? liveLogTokenSource;
    private Task? liveLogTask;

    internal string ParserStartErrorMessage = string.Empty;
    
    private int selectedGuildIndex;
    private int selectedRegionIndex;
    private int selectedVisibilityIndex;
    
    private long SelectedGuildValue => plugin.FfLogsUser?.GuildSelectItems[selectedGuildIndex].Value ?? 0L;
    private long SelectedRegionValue => plugin.FfLogsUser?.RegionOrServerSelectItems[selectedRegionIndex].Value ?? 0L;
    private long SelectedVisibilityValue =>
        plugin.FfLogsUser?.ReportVisibilitySelectItems[selectedVisibilityIndex].Value ?? 0L;
    
    private string reportDescription = string.Empty;

    private string logFolder = string.Empty;
    private bool includeEntireFileInReport;
    private string liveLogProgressMessage = string.Empty;
    private Progress<string>? liveLogProgress;
    private string liveLogErrorMessage = string.Empty;
    private string liveLogReportCode = string.Empty;

    private string logFilePath = string.Empty;
    //private bool selectFightsToUpload;
    private string uploadALogProgressMessage = string.Empty;
    private Progress<string>? uploadALogProgress; 
    private string uploadALogErrorMessage = string.Empty;
    private string uploadALogReportCode = string.Empty;

    private string logFilePathToSplit = string.Empty;
    private string splitLogProgressMessage = string.Empty;
    private string splitLogErrorMessage = string.Empty;
    
    internal bool IsLoggingIn;
    internal string LoginErrorMessage = string.Empty;

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
    }
    
    private enum OperationStatus {
        Idle,
        InProgress,
    }

    public async ValueTask DisposeAsync()
    {
        liveLogTokenSource?.Cancel();
        liveLogTokenSource?.Dispose();
        liveLogTokenSource = null;

        if (liveLogTask != null)
        {
            try
            {
                await liveLogTask;
            } catch (OperationCanceledException) { }
        }
            
        
        GC.SuppressFinalize(this);
    }

    public void SetOptionsFromConfiguration()
    {
        email = plugin.Configuration.FfLogsEmail;
        password = plugin.Configuration.FfLogsPassword;
        automaticLogin = plugin.Configuration.FfLogsAutomaticLogin;
        logFilePath = plugin.Configuration.LogFilePath;
        logFolder = plugin.Configuration.LiveLogFolder;
        includeEntireFileInReport = plugin.Configuration.IncludeEntireFileInReport;

        if (plugin.FfLogsUser != null)
        {
            selectedGuildIndex =
                plugin.FfLogsUser!.GuildSelectItems.FindIndex(item => item.Value ==
                                                                      plugin.Configuration.SelectedGuildValue);
            selectedRegionIndex =
                plugin.FfLogsUser.RegionOrServerSelectItems.FindIndex(item => item.Value ==
                                                                              plugin.Configuration.SelectedRegionValue);
            selectedVisibilityIndex =
                plugin.FfLogsUser.ReportVisibilitySelectItems.FindIndex(item => item.Value ==
                                                                            plugin.Configuration.SelectedVisibilityValue);

            if (selectedGuildIndex == -1) selectedGuildIndex = 0;
            if (selectedRegionIndex == -1) selectedRegionIndex = 0;
            if (selectedVisibilityIndex == -1) selectedVisibilityIndex = 0;
        }
    }

    public override void Draw()
    {
        if (plugin.FfLogsUser == null)
        {
            DrawLoginScreen();
            return;
        }

        if (!ParserStartErrorMessage.IsNullOrWhitespace())
        {
            ImGui.TextColored(new Vector4(1, 0.3f, 0.3f, 1), $"Parser failed to load, please check Dalamud logs (/xllog): {ParserStartErrorMessage}");
            return;
        } 
        
        if (!plugin.FfLogParser.Started)
        {
            ImGui.Text("Loading parser...");
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

    private void DrawLoginScreen()
    {
        ImGui.Text("Log in to FFLogs");
        ImGui.Separator();
        ImGui.Spacing();

        using (ImRaii.Disabled(IsLoggingIn))
        {
            ImGui.SetNextItemWidth(-1);
            if (ImGui.InputTextWithHint("Email##email", "Email", ref email,
                                        flags: ImGuiInputTextFlags.EnterReturnsTrue))
            {
                DoLogin();
            }
        
            ImGui.SetNextItemWidth(-1);
            if (ImGui.InputTextWithHint("Password##password", "Password", ref password,
                                        flags: ImGuiInputTextFlags.Password | ImGuiInputTextFlags.EnterReturnsTrue))
            {
                DoLogin();
            }
        
            ImGui.Checkbox("Automatically login", ref automaticLogin);
        
            ImGui.Spacing();

            if (ImGui.Button(IsLoggingIn ? "Logging in..." : "Log in", new Vector2(-1, 30)))
            {
                DoLogin();
            }
        }

        if (!LoginErrorMessage.IsNullOrWhitespace())
        {
            ImGui.Spacing();
            ImGui.TextColored(new Vector4(1, 0.3f, 0.3f, 1), LoginErrorMessage);
        }
    }

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
                liveLogTokenSource == null ? "Start" : "Stop",
                uploadALogStatus == OperationStatus.InProgress || splitALogStatus == OperationStatus.InProgress,
                liveLogProgressMessage,
                liveLogErrorMessage)
            )
        {
            if (liveLogTokenSource == null)
            {
                StartLiveLogging();
            }
            else
            {
                liveLogTokenSource.Cancel();
                liveLogTokenSource.Dispose();
                liveLogTokenSource = null;
                liveLogTask = null;
                liveLoggingStatus = OperationStatus.Idle;
            }
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

    private void DrawUploadALogTab()
    {
        using (ImRaii.Disabled(AnyOperationInProgress))
        {
            ImGui.Spacing();
            ImGui.Text("Log file to upload:");
        
            ImGui.SetNextItemWidth(-80);
            ImGui.InputText("##logFile", ref logFilePath);
            ImGui.SameLine();
            if (ImGui.Button("Browse##browseLogFile"))
                fileDialogManager.OpenFileDialog("Select Log File",
                                                 "Log files{.log},All files{.*}",
                                                 (success, paths) =>
                                                 {
                                                     if (success && paths.Count > 0)
                                                         logFilePath = paths[0];

                                                     plugin.Configuration.LogFilePath = logFilePath;
                                                     plugin.Configuration.Save();
                                                 },
                                                 1,
                                                 GetDialogStartPath(logFilePath));

            ImGui.Spacing();
            DrawSharedUploadOptions();

            ImGui.Spacing();
            // TODO
            // if (ImGui.Button("Select fights to upload"))
            // {
            //     
            // }
            //
            // ImGui.SameLine();
        }
        
        if (DrawActionButtonAndMessages("Upload", AnyOperationInProgress, uploadALogProgressMessage, uploadALogErrorMessage))
            DoUploadLogFile();

        if (!uploadALogReportCode.IsNullOrWhitespace())
        {
            ImGui.Spacing();
            ImGui.Text("Log file successfully uploaded!");
            
            ImGui.SameLine();
            if (ImGui.Button("Copy report link"))
            {
                ImGui.SetClipboardText($"https://www.fflogs.com/reports/{uploadALogReportCode}");
            }

            ImGui.SameLine();
            if (ImGui.Button("Open report link"))
            {
                Task.Run(() => Process.Start(new ProcessStartInfo
                {
                    FileName = $"https://www.fflogs.com/reports/{uploadALogReportCode}", UseShellExecute = true
                }));
            }
        }
    }
    
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
        }
        
        ImGui.Spacing();
        if (DrawActionButtonAndMessages("Split", AnyOperationInProgress, splitLogProgressMessage, splitLogErrorMessage))
            Task.Run(DoSplitLogFileAsync);
    }

    private void DrawSettingsTab()
    {
        using (ImRaii.Disabled(AnyOperationInProgress))
        {
            ImGui.Spacing();
            ImGui.Text($"Logged in as {plugin.FfLogsUser!.User.UserName}");

            ImGui.SameLine();
            if (ImGui.Button("Log out"))
            {
                Task.Run(plugin.FfLogsDesktopClient.LogoutAsync)
                    .ContinueWith(task =>
                    {
                        if (task.Exception != null)
                            Plugin.Log.Error(task.Exception, "Logout failed");

                        plugin.FfLogsUser = null;
                        email = string.Empty;
                        password = string.Empty;
                        automaticLogin = false;
                        plugin.Configuration.FfLogsEmail = string.Empty;
                        plugin.Configuration.FfLogsPassword = string.Empty;
                        plugin.Configuration.FfLogsAutomaticLogin = false;

                        plugin.Configuration.Save();
                    });
            }
        }
    }
    
    private void DrawSharedUploadOptions()
    {
        var guildNames = plugin.FfLogsUser!.GuildSelectItems.Select(item => item.Label).ToArray();
        var regionNames = plugin.FfLogsUser!.RegionOrServerSelectItems.Select(item => item.Label).ToArray();
        var visibilityNames = plugin.FfLogsUser!.ReportVisibilitySelectItems.Select(item => item.Label).ToArray();
        
        ImGui.Text("Guild to upload to:");
        ImGui.SameLine();
        
        ImGui.SetNextItemWidth(150);
        if (ImGui.Combo("##guild", ref selectedGuildIndex, guildNames))
        {
            plugin.Configuration.SelectedGuildValue = SelectedGuildValue;
            plugin.Configuration.Save();
        }
        ImGui.SameLine();

        if (plugin.FfLogsUser!.GuildSelectItems[selectedGuildIndex].Value == -1)
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
    
    private void DoLogin()
    {
        if (email.IsNullOrWhitespace() || password.IsNullOrWhitespace())
        {
            LoginErrorMessage = "Email or password is missing.";
            return;
        }
        
        IsLoggingIn = true;

        Task.Run(async () => plugin.FfLogsUser = await plugin.FfLogsDesktopClient.LoginAsync(email, password))
                  .ContinueWith(task =>
                  {
                      IsLoggingIn = false;

                      if (task.Exception != null)
                      {
                          Plugin.Log.Error(task.Exception, "Log in failed");
                          LoginErrorMessage = task.Exception.InnerExceptions.FirstOrDefault(task.Exception).Message;
                          return;
                      }

                      if (plugin.FfLogsUser == null)
                      {
                          Plugin.Log.Error("Unexpected state: logged in successfully but FfLogsUser is null");
                          return;
                      }
                      
                      Plugin.Log.Information("Logged in as {0}", plugin.FfLogsUser.User.UserName);
                      plugin.Configuration.FfLogsAutomaticLogin = automaticLogin;

                      if (plugin.Configuration.FfLogsAutomaticLogin)
                      {
                          plugin.Configuration.FfLogsEmail = email;
                          plugin.Configuration.FfLogsPassword = password;
                      }
                      else
                      {
                          plugin.Configuration.FfLogsEmail = string.Empty;
                          plugin.Configuration.FfLogsPassword = string.Empty;
                      }

                      email = string.Empty;
                      password = string.Empty;

                      plugin.Configuration.Save();
                      StartParser();
                  });
    }

    internal void StartParser()
    {
        Task.Run(async () =>
        {
            var script =
                await plugin.FfLogsDesktopClient.DownloadParserScript(plugin.FfLogParser.Id, false, false, false);
            await plugin.FfLogParser.StartAsync(false, false, false, script);
        }).ContinueWith(task =>
        {
            if (task.Exception != null)
            {
                Plugin.Log.Error(task.Exception, "Loading parser failed");
                ParserStartErrorMessage = task.Exception.InnerExceptions.FirstOrDefault(task.Exception).Message;
                return;
            }

            Task.Run(plugin.FfLogParser.GetParserVersionAsync).ContinueWith(task1 =>
            {
                if (task1.Exception != null)
                {
                    Plugin.Log.Error(task1.Exception, "Getting plugin version failed");
                    ParserStartErrorMessage = task1.Exception.InnerExceptions.FirstOrDefault(task1.Exception).Message;
                    return;
                }

                Plugin.Log.Information("Parser version {0} loaded", task1.Result);
            });
        });
    }

    private void StartLiveLogging()
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

        var uploader = new LogUploader(plugin.FfLogsDesktopClient, plugin.FfLogParser);
        var guildId = SelectedGuildValue;
        var visibility = SelectedVisibilityValue;
        var region = SelectedRegionValue;

        if (liveLogProgress == null)
        {
            liveLogProgress = new Progress<string>();
            liveLogProgress.ProgressChanged += (_, args) => { liveLogProgressMessage = args; };
        }

        liveLogTokenSource ??= new CancellationTokenSource();
        liveLogTask = Task.Run(() => uploader.StartLiveLogAsync(logFolder, region, visibility,
                                                                guildId == -1 ? null : guildId,
                                                                reportDescription,
                                                                includeEntireFileInReport, liveLogProgress,
                                                                reportCode => { liveLogReportCode = reportCode; },
                                                                liveLogTokenSource.Token))
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

    private void DoUploadLogFile()
    {
        uploadALogStatus = OperationStatus.InProgress;
        uploadALogReportCode = string.Empty;
        uploadALogProgressMessage = string.Empty;
        uploadALogErrorMessage = string.Empty;
        
        if (logFilePath.IsNullOrWhitespace())
        {
            uploadALogStatus = OperationStatus.Idle;
            uploadALogErrorMessage = "Path to log file is missing.";
            return;
        }
        
        if (!File.Exists(logFilePath))
        {
            uploadALogStatus = OperationStatus.Idle;
            uploadALogErrorMessage = "Log file does not exist, or is not a file.";
            return;
        }
        
        var uploader = new LogUploader(plugin.FfLogsDesktopClient, plugin.FfLogParser);
        var guildId = SelectedGuildValue;
        var visibility = SelectedVisibilityValue;
        var region = SelectedRegionValue;

        if (uploadALogProgress == null)
        {
            uploadALogProgress = new Progress<string>();
            uploadALogProgress.ProgressChanged += (_, args) => { uploadALogProgressMessage = args; };
        }

        Task.Run(() => uploader.UploadLogFileAsync(logFilePath, region, visibility, guildId == -1 ? null : guildId,
                                                   reportDescription, [], uploadALogProgress))
            .ContinueWith(task =>
            {
                uploadALogStatus = OperationStatus.Idle;
                uploadALogProgressMessage = string.Empty;
                
                if (task.Exception != null)
                {
                    Plugin.Log.Error(task.Exception, "Failed to upload log");
                    uploadALogErrorMessage = task.Exception.InnerExceptions.FirstOrDefault(task.Exception).Message;
                }
                else
                    uploadALogReportCode = task.Result;
            });
    }

    private async Task DoSplitLogFileAsync()
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

        try
        {
            string? firstLogLine;
            await using (var fs = new FileStream(logFilePathToSplit, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                using var sr = new StreamReader(fs);
                firstLogLine = await sr.ReadLineAsync();
            }

            if (firstLogLine == null)
            {
                splitLogErrorMessage = "Log file is empty.";
                return;
            }

            var splitTimestamp = firstLogLine.Split("|").ElementAtOrDefault(1);

            if (splitTimestamp == null)
            {
                splitLogErrorMessage = "Invalid log file. First log line is missing a timestamp.";
                return;
            }

            var currentZoneId = -1L;
            var headerLines = new List<string>();
            await using var fs2 = new FileStream(logFilePathToSplit, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr2 = new StreamReader(fs2);
            var splitFile = new FileStream(
                Path.Combine(Path.GetDirectoryName(logFilePathToSplit)!,
                             $"Split-{Path.GetFileNameWithoutExtension(logFilePathToSplit)}-{splitTimestamp.Replace(":", "")}.log"),
                FileMode.Create,
                FileAccess.Write,
                FileShare.None
            );
            var splitFileStreamWriter = new StreamWriter(splitFile);
            var lineNumber = 0L;

            while (await sr2.ReadLineAsync() is { } line)
            {
                lineNumber++;
                splitLogProgressMessage = $"Readling line {lineNumber}";
                
                var lineSplit = line.Split("|");
                var eventIdStr = lineSplit.ElementAtOrDefault(0);
                var timestamp = lineSplit.ElementAtOrDefault(1);

                if (eventIdStr == null || timestamp == null || !int.TryParse(eventIdStr, CultureInfo.InvariantCulture, out var eventId))
                {
                    splitLogProgressMessage = string.Empty;
                    splitLogErrorMessage = $"Log file is invalid: line {lineNumber} is missing event ID or timestamp, or event ID is not a valid number";
                    return;
                }

                if (eventId == 253)
                    headerLines.Clear();
                
                if (eventId == 253 || eventId == 250 || (eventId == 251 && line.Contains("DeucalionClient")))
                    headerLines.Add(line);

                var shouldSplitLog = false;

                if (eventId == 1)
                {
                    if (!int.TryParse(lineSplit.ElementAtOrDefault(2), NumberStyles.HexNumber,
                                      CultureInfo.InvariantCulture, out var zoneId))
                    {
                        splitLogProgressMessage = string.Empty;
                        splitLogErrorMessage = $"Log file is invalid: line {lineNumber}'s zone ID is invalid";
                        return;
                    }

                    if (currentZoneId == -1)
                        currentZoneId = zoneId;
                    else if (zoneId != currentZoneId)
                    {
                        shouldSplitLog = true;
                        currentZoneId = zoneId;
                    }
                }
                else
                {
                    shouldSplitLog = DateTime.TryParse(splitTimestamp, null, DateTimeStyles.RoundtripKind,
                                                       out var splitDateTime)
                                     && DateTime.TryParse(timestamp, null, DateTimeStyles.RoundtripKind,
                                                          out var currentDateTime)
                                     && currentDateTime.Subtract(splitDateTime).TotalSeconds >= 14400.0;
                }

                if (shouldSplitLog)
                {
                    await splitFileStreamWriter.DisposeAsync();
                    await splitFile.DisposeAsync();

                    splitTimestamp = timestamp;
                    splitFile = new FileStream(
                        Path.Combine(Path.GetDirectoryName(logFilePathToSplit)!,
                                     $"Split-{Path.GetFileNameWithoutExtension(logFilePathToSplit)}-{splitTimestamp.Replace(":", "")}.log"),
                        FileMode.Create,
                        FileAccess.Write,
                        FileShare.None
                    );
                    splitFileStreamWriter = new StreamWriter(splitFile);

                    foreach (var headerLine in headerLines)
                    {
                        await splitFileStreamWriter.WriteAsync(headerLine);
                        await splitFileStreamWriter.WriteAsync("\n");
                    }
                }
                
                await splitFileStreamWriter.WriteAsync(line);
                await splitFileStreamWriter.WriteAsync("\n");
            }

            splitLogProgressMessage = "Successfully split log file";
        }
        catch (Exception e)
        {
            Plugin.Log.Error(e, "Split log file failed");
            splitLogErrorMessage = e.Message;
        }
        finally
        {
            splitALogStatus = OperationStatus.Idle;
        }
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
