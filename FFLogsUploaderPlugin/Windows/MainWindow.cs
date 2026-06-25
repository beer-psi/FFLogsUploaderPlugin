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

    private string email;
    private string password;
    private bool automaticLogin;

    private bool isWorking;
    private CancellationTokenSource? liveLogTokenSource;
    private Task? liveLogTask;
    
    private int selectedGuildIndex;
    private int selectedRegionIndex;
    private int selectedVisibilityIndex;
    private string reportDescription = string.Empty;

    private string logFolder;
    private bool includeEntireFileInReport;
    private string liveLogProgressMessage = string.Empty;
    private Progress<string>? liveLogProgress;
    private string liveLogErrorMessage = string.Empty;
    private string liveLogReportCode = string.Empty;

    private string logFilePath;
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
        
        this.plugin = plugin;
        iinact = new IINACTIpc(Plugin.PluginInterface);

        email = this.plugin.Configuration.FfLogsEmail;
        password = this.plugin.Configuration.FfLogsPassword;
        automaticLogin = this.plugin.Configuration.FfLogsAutomaticLogin;
        logFilePath = this.plugin.Configuration.LogFilePath;
        logFolder = this.plugin.Configuration.LiveLogFolder;
        includeEntireFileInReport = this.plugin.Configuration.IncludeEntireFileInReport;
    }

    public async ValueTask DisposeAsync()
    {
        liveLogTokenSource?.Cancel();
        liveLogTokenSource?.Dispose();
        liveLogTokenSource = null;

        if (liveLogTask != null)
            await liveLogTask;
        
        GC.SuppressFinalize(this);
    }

    public void SetOptionsFromConfiguration()
    {
        logFilePath = plugin.Configuration.LogFilePath;
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

    public override void Draw()
    {
        if (plugin.FfLogsUser == null)
        {
            DrawLoginScreen();
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
                Task.Run(DoLoginAsync);
            }
        
            ImGui.SetNextItemWidth(-1);
            if (ImGui.InputTextWithHint("Password##password", "Password", ref password,
                                        flags: ImGuiInputTextFlags.Password | ImGuiInputTextFlags.EnterReturnsTrue))
            {
                Task.Run(DoLoginAsync);
            }
        
            ImGui.Checkbox("Automatically login", ref automaticLogin);
        
            ImGui.Spacing();

            if (ImGui.Button(IsLoggingIn ? "Logging in..." : "Log in", new Vector2(-1, 30)))
            {
                Task.Run(DoLoginAsync);
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
        using (ImRaii.Disabled(isWorking))
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

        // This is outside ImRaii.Disabled because the user needs to be able to stop logging.
        ImGui.Spacing();
        if (DrawActionButtonAndMessages(liveLogTokenSource == null ? "Start" : "Stop", liveLogProgressMessage, liveLogErrorMessage))
        {
            if (liveLogTokenSource == null)
            {
                DoLiveLog();
            }
            else
            {
                liveLogTokenSource.Cancel();
                liveLogTokenSource.Dispose();
                liveLogTokenSource = null;
                liveLogTask = null;
                isWorking = false;
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
        using (ImRaii.Disabled(isWorking))
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
            if (DrawActionButtonAndMessages("Upload", uploadALogProgressMessage, uploadALogErrorMessage))
                Task.Run(DoUploadLogFileAsync);
        }

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
            plugin.Configuration.SelectedGuildValue = plugin.FfLogsUser!.GuildSelectItems[selectedGuildIndex].Value;
            plugin.Configuration.Save();
        }
        ImGui.SameLine();

        if (plugin.FfLogsUser!.GuildSelectItems[selectedGuildIndex].Value == -1)
        {
            ImGui.SetNextItemWidth(60);
            if (ImGui.Combo("##region", ref selectedRegionIndex, regionNames))
            {
                plugin.Configuration.SelectedRegionValue = plugin.FfLogsUser!.RegionOrServerSelectItems[selectedRegionIndex].Value;
                plugin.Configuration.Save();
            }
            ImGui.SameLine();
        }
        
        ImGui.SetNextItemWidth(80);
        if (ImGui.Combo("##visibility", ref selectedVisibilityIndex, visibilityNames))
        {
            plugin.Configuration.SelectedVisibilityValue = plugin.FfLogsUser!.ReportVisibilitySelectItems[selectedVisibilityIndex].Value;
            plugin.Configuration.Save();
        }

        ImGui.Spacing();
        ImGui.Text("Enter a description for the report:");
        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("##description", ref reportDescription);
    }
    
    private void DrawSplitALogTab()
    {
        using (ImRaii.Disabled(isWorking))
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
    
            ImGui.Spacing();
            if (DrawActionButtonAndMessages("Split", splitLogProgressMessage, splitLogErrorMessage))
                Task.Run(DoSplitLogFileAsync);
        }
    }

    private static bool DrawActionButtonAndMessages(string buttonLabel, string progressMessage, string errorMessage)
    {
        var result = ImGui.Button(buttonLabel);
        
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

    private void DrawSettingsTab()
    {
        using (ImRaii.Disabled(isWorking))
        {
            ImGui.Spacing();
            ImGui.Text($"Logged in as {plugin.FfLogsUser!.User.UserName}");

            ImGui.SameLine();
            if (ImGui.Button("Log out"))
            {
                Task.Run(DoLogoutAsync);
            }
        }
    }

    private async Task DoLoginAsync()
    {
        if (email.IsNullOrWhitespace() || password.IsNullOrWhitespace())
        {
            LoginErrorMessage = "Email or password is missing.";
            return;
        }
        
        IsLoggingIn = true;

        try
        {
            plugin.FfLogsUser = await plugin.FfLogsDesktopClient.LoginAsync(email, password);
            Plugin.Log.Information("Logged in as {0}", plugin.FfLogsUser.User.UserName);

            if (automaticLogin)
            {
                plugin.Configuration.FfLogsEmail = email;
                plugin.Configuration.FfLogsPassword = password;
                plugin.Configuration.FfLogsAutomaticLogin = true;
            }
            else
            {
                plugin.Configuration.FfLogsEmail = string.Empty;
                plugin.Configuration.FfLogsPassword = string.Empty;
                plugin.Configuration.FfLogsAutomaticLogin = false;
            }

            plugin.Configuration.Save();

            email = string.Empty;
            password = string.Empty;
        }
        catch (Exception e)
        {
            Plugin.Log.Error(e, "Login failed");
            LoginErrorMessage = e.Message;
        }
        finally
        {
            IsLoggingIn = false;
        }

        if (!plugin.FfLogParser.Started)
        {
            try
            {
                await plugin.FfLogParser.StartAsync(false, false, false,
                                                    await plugin.FfLogsDesktopClient.DownloadParserScript(
                                                        plugin.FfLogParser.Id, false, false, false));
                Plugin.Log.Information("Parser version {0} loaded", await plugin.FfLogParser.GetParserVersionAsync());
            }
            catch (Exception e)
            {
                Plugin.Log.Error(e, "Loading parser failed");
            }
        }
    }

    private async Task DoLogoutAsync()
    {
        try
        {
            await plugin.FfLogsDesktopClient.LogoutAsync();
        }
        catch (Exception e)
        {
            Plugin.Log.Error(e, "Logout failed");
        }
        
        plugin.FfLogsUser = null;
        email = string.Empty;
        password = string.Empty;
        automaticLogin = false;
        plugin.Configuration.FfLogsEmail = string.Empty;
        plugin.Configuration.FfLogsPassword = string.Empty;
        plugin.Configuration.FfLogsAutomaticLogin = false;
        
        plugin.Configuration.Save();
    }

    private void DoLiveLog()
    {
        isWorking = true;
        liveLogReportCode = string.Empty;
        liveLogProgressMessage = string.Empty;
        liveLogErrorMessage = string.Empty;

        if (logFolder.IsNullOrWhitespace())
        {
            isWorking = false;
            liveLogErrorMessage = "Path to log folder is missing.";
            return;
        }

        if (!Directory.Exists(logFolder))
        {
            isWorking = false;
            liveLogErrorMessage = "Log folder does not exist or is a file.";
            return;
        }

        var uploader = new LogUploader(plugin.FfLogsDesktopClient, plugin.FfLogParser);
        var guildId = plugin.FfLogsUser!.GuildSelectItems[selectedGuildIndex].Value;
        var visibility = plugin.FfLogsUser!.ReportVisibilitySelectItems[selectedVisibilityIndex].Value;
        var region = plugin.FfLogsUser!.RegionOrServerSelectItems[selectedRegionIndex].Value;

        if (liveLogProgress == null)
        {
            liveLogProgress = new Progress<string>();
            liveLogProgress.ProgressChanged += (_, args) => { liveLogProgressMessage = args; };
        }

        liveLogTokenSource ??= new CancellationTokenSource();
        liveLogTask = Task.Run(async () =>
        {
            try
            {
                await uploader.StartLiveLogAsync(logFolder, region, visibility, guildId == -1 ? null : guildId,
                                                 reportDescription,
                                                 includeEntireFileInReport, liveLogProgress,
                                                 reportCode => { liveLogReportCode = reportCode; },
                                                 liveLogTokenSource.Token);
            }
            catch (Exception e)
            {
                Plugin.Log.Error(e, "Live logging operation failed");
                liveLogProgressMessage = string.Empty;
                liveLogErrorMessage = e.Message;
            }
            finally
            {
                isWorking = false;
            }
        });
    }

    private async Task DoUploadLogFileAsync()
    {
        isWorking = true;
        uploadALogReportCode = string.Empty;
        uploadALogProgressMessage = string.Empty;
        uploadALogErrorMessage = string.Empty;
        
        if (logFilePath.IsNullOrWhitespace())
        {
            isWorking = false;
            uploadALogErrorMessage = "Path to log file is missing.";
            return;
        }
        
        if (!File.Exists(logFilePath))
        {
            isWorking = false;
            uploadALogErrorMessage = "Log file does not exist, or is not a file.";
            return;
        }
        
        var uploader = new LogUploader(plugin.FfLogsDesktopClient, plugin.FfLogParser);
        var guildId = plugin.FfLogsUser!.GuildSelectItems[selectedGuildIndex].Value;
        var visibility = plugin.FfLogsUser!.ReportVisibilitySelectItems[selectedVisibilityIndex].Value;
        var region = plugin.FfLogsUser!.RegionOrServerSelectItems[selectedRegionIndex].Value;

        if (uploadALogProgress == null)
        {
            uploadALogProgress = new Progress<string>();
            uploadALogProgress.ProgressChanged += (_, args) => { uploadALogProgressMessage = args; };
        }
        
        try
        {
            uploadALogReportCode = await uploader.UploadLogFileAsync(logFilePath, region, visibility, guildId == -1 ? null : guildId,
                                         reportDescription, [], uploadALogProgress);
        }
        catch (Exception e)
        {
            Plugin.Log.Error(e, "Failed to upload log");
            uploadALogErrorMessage = e.Message;
        }
        finally
        {
            isWorking = false;
            uploadALogProgressMessage = string.Empty;
        }
    }

    private async Task DoSplitLogFileAsync()
    {
        isWorking = true;
        splitLogProgressMessage = string.Empty;
        splitLogErrorMessage = string.Empty;

        if (logFilePathToSplit.IsNullOrWhitespace())
        {
            isWorking = false;
            splitLogErrorMessage = "Path to log file is missing.";
            return;
        }

        if (Path.GetFileName(logFilePathToSplit).StartsWith("Split-"))
        {
            isWorking = false;
            splitLogErrorMessage = "Cowardly refusing to split a split log file.";
            return;
        }

        if (!File.Exists(logFilePathToSplit))
        {
            isWorking = false;
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
            isWorking = false;
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
