using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.ClientState;
using Dalamud.Game.DutyState;
using Dalamud.Utility;
using FFLogsUploaderPlugin.FFLogs;

namespace FFLogsUploaderPlugin;

// TODO: Starting when duty starts might not be a great idea for dungeons?
internal class FfLogsManager : IAsyncDisposable
{
    internal Plugin Plugin { get; init; }
    internal DesktopClient DesktopClient { get; private set; }
    internal LogParser LogParser { get; private set; }
    internal DesktopClient.LoginResponse? User { get; private set; }

    private CancellationTokenSource? liveLogCts;
    private Task? liveLogTask;

    private volatile bool isStoppingLiveLogging;
    
    internal bool IsLiveLogging => liveLogTask is { IsCompleted: false };

    internal FfLogsManager(Plugin plugin)
    {
        Plugin = plugin;
        DesktopClient = new DesktopClient();
        LogParser = new LogParser();

        Plugin.ClientState.ZoneInit += OnZoneInit;
        Plugin.DutyState.DutyWiped += OnDutyWipe;
    }
    
    public async ValueTask DisposeAsync()
    {
        liveLogCts?.Cancel();
        liveLogCts?.Dispose();
        liveLogCts = null;

        if (liveLogTask is { IsCompleted: false } llTask)
        {
            try
            {
                await llTask;
            }
            catch (Exception e)
            {
                Plugin.Log.Error(e, "Error occured waiting for live logging to finish at plugin shutdown");
            }
            finally
            {
                llTask.Dispose();
                liveLogTask = null;
            }
        }

        Plugin.ClientState.ZoneInit -= OnZoneInit;
        Plugin.DutyState.DutyWiped -= OnDutyWipe;
        User = null;
        LogParser.Dispose();
        DesktopClient.Dispose();
        
        GC.SuppressFinalize(this);
    }

    internal async Task<DesktopClient.LoginResponse> LoginAsync(string email, string password, bool automaticLogin)
    {
        if (email.IsNullOrWhitespace())
            throw new ArgumentException("email cannot be empty", nameof(email));

        if (password.IsNullOrWhitespace())
            throw new ArgumentException("password cannot be empty", nameof(password));

        User = await DesktopClient.LoginAsync(email, password);
        
        if (automaticLogin)
        {
            Plugin.Configuration.FfLogsEmail = email;
            Plugin.Configuration.FfLogsPassword = password;
        }
        else
        {
            Plugin.Configuration.FfLogsEmail = string.Empty;
            Plugin.Configuration.FfLogsPassword = string.Empty;
        }

        Plugin.Configuration.FfLogsAutomaticLogin = automaticLogin;
        Plugin.Configuration.Save();

        return User;
    }

    internal async Task<DesktopClient.LoginResponse?> AutomaticLoginAsync()
    {
        if (!Plugin.Configuration.FfLogsAutomaticLogin)
            return null;

        return await LoginAsync(
            Plugin.Configuration.FfLogsEmail,
            Plugin.Configuration.FfLogsPassword,
            Plugin.Configuration.FfLogsAutomaticLogin);
    }

    internal async Task LogoutAsync()
    {
        try
        {
            await DesktopClient.LogoutAsync();
        }
        catch (Exception e)
        {
            Plugin.Log.Error(e, "Logout failed");
            throw;
        }
        finally
        {
            User = null;
        
            Plugin.Configuration.FfLogsEmail = string.Empty;
            Plugin.Configuration.FfLogsPassword = string.Empty;
            Plugin.Configuration.FfLogsAutomaticLogin = false;
            Plugin.Configuration.Save();
        }
    }

    internal async Task StartParserAsync(bool gameContentDetectionEnabled, bool metersEnabled, bool liveFightDataEnabled)
    {
        var script = await DesktopClient.DownloadParserScript(LogParser.Id, gameContentDetectionEnabled, metersEnabled, liveFightDataEnabled);

        await LogParser.StartAsync(gameContentDetectionEnabled, metersEnabled, liveFightDataEnabled, script);
    }

    internal Task StartLiveLoggingAsync(string logFolder,
                                   long region,
                                   long visibility,
                                   long? guildId = null,
                                   string description = "",
                                   bool includeEntireFileInReport = false,
                                   IProgress<string>? progress = null,
                                   Action<string>? onReportCreated = null)
    {
        liveLogCts = new CancellationTokenSource();
        liveLogTask = Task.Run(async () =>
        {
            var logUploader = new LogUploader(DesktopClient, LogParser);

            await logUploader.StartLiveLogAsync(logFolder, region, visibility, guildId, description,
                                                includeEntireFileInReport, progress,
                                                onReportCreated, liveLogCts.Token);
        });

        return liveLogTask;
    }

    internal void StopLiveLogging()
    {
        liveLogCts?.Cancel();
        liveLogCts?.Dispose();
        liveLogCts = null;
    }

    internal Task<string> UploadLogFileAsync(
        string logFilePath,
        long region,
        long visibility,
        long? guildId = null,
        string description = "",
        List<LogParser.ScannedRaid>? raidsToUpload = null,
        IProgress<string>? progress = null)
    {
        var logUploader = new LogUploader(DesktopClient, LogParser);
        
        return logUploader.UploadLogFileAsync(logFilePath, region, visibility, guildId, description, raidsToUpload, progress);
    }

    internal static async Task SplitLogFileAsync(string logFilePathToSplit, IProgress<string>? progress = null)
    {
        string? firstLogLine;
        await using (var fs = new FileStream(logFilePathToSplit, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            using var sr = new StreamReader(fs);
            firstLogLine = await sr.ReadLineAsync();
        }

        if (firstLogLine == null)
        {
            throw new SplitLogException("Log file is empty.");
        }

        var splitTimestamp = firstLogLine.Split("|").ElementAtOrDefault(1);

        if (splitTimestamp == null)
        {
            throw new SplitLogException("Invalid log file. First log line is missing a timestamp.");
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
            progress?.Report($"Reading line {lineNumber}");
            
            var lineSplit = line.Split("|");
            var eventIdStr = lineSplit.ElementAtOrDefault(0);
            var timestamp = lineSplit.ElementAtOrDefault(1);

            if (eventIdStr == null || timestamp == null || !int.TryParse(eventIdStr, CultureInfo.InvariantCulture, out var eventId))
            {
                throw new SplitLogException($"Log file is invalid: line {lineNumber} is missing event ID or timestamp, or event ID is not a valid number");
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
                    throw new SplitLogException($"Log file is invalid: line {lineNumber}'s zone ID is invalid");
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
    }

    // ZoneInit
    // - Valid duty and live logging is not active -> start live logging
    // - No duty and live logging is active -> stop live logging
    private void OnZoneInit(ZoneInitEventArgs args)
    {
        var condition = args.ContentFinderCondition.ValueNullable;
        var territoryType = args.TerritoryType.ValueNullable;

        if (territoryType is null or { IsPvpZone: true })
        {
            Plugin.Log.Debug("Ignoring unknown territory or PvP zone (RowId={0})", territoryType?.RowId ?? 0L);
            return;
        }

        // https://exd.camora.dev/sheet/ContentType
        // 1 = Dungeons, 3 = Trials, 4 = Raids, 6 = Ultimate Raids, 7 = Chaotic Alliance Raid
        if (Plugin.Configuration.StartLiveLoggingWhenDutyStarts
            && LogParser.Started
            && !IsLiveLogging
            && condition?.ContentType.ValueNullable is { Unknown2: 1 or 3 or 4 or 6 or 7 })
        {
            Plugin.MainWindow.StartLiveLogging(true); // Call it there to also handle UI state
        }
        else if (Plugin.Configuration.StopLiveLoggingWhenDutyEnds
                 && LogParser.Started
                 && IsLiveLogging
                 && !isStoppingLiveLogging
                 && condition is null or { RowId: 0 })
        {
            isStoppingLiveLogging = true;
            
            Task.Run(async () =>
            {
                Plugin.ChatGui.Print("[FF Logs Uploader] Duty ended. Stopping live logging after 5 seconds.");
                await Task.Delay(TimeSpan.FromSeconds(5));
                StopLiveLogging();
                isStoppingLiveLogging = false;
                Plugin.ChatGui.Print("[FF Logs Uploader] Live logging stopped.");
            });
        }
    }

    private void OnDutyWipe(IDutyStateEventArgs args)
    {
        if (Plugin.Configuration.AutomaticallyCallDutyWipe && LogParser.Started && IsLiveLogging)
            Task.Run(LogParser.CallWipeAsync).ContinueWith(task =>
            {
                if (task.Exception == null)
                {
                    Plugin.ToastGui.ShowNormal("Called wipe automatically.");
                    return;
                }
                
                Plugin.Log.Error(task.Exception, "Failed to automatically call a wipe");
                Plugin.ToastGui.ShowError("Failed to automatically call a wipe, view /xllog for details");
            });
    }
}
