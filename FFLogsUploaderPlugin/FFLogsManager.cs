using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.ClientState;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.DutyState;
using Dalamud.Interface.ImGuiNotification;
using Dalamud.Utility;
using FFLogsUploaderPlugin.Extensions;
using FFLogsUploaderPlugin.FFLogs;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;
using Lumina.Extensions;
// ReSharper disable InconsistentNaming

namespace FFLogsUploaderPlugin;

public class FFLogsManager : IAsyncDisposable
{
    private readonly Plugin plugin;
    internal readonly DesktopClient DesktopClient = new();
    private readonly LogParser LogParser = new();
    private readonly LogParser MetersLogParser = new(2);
    internal DesktopClient.LoginResponse? User { get; private set; }
    
    internal AggregateException? LoginError { get; private set; }
    internal bool IsLoggingIn { get; private set; }
    internal AggregateException? ParsersError { get; private set; }
    internal bool ParsersReady => LogParser.Started && MetersLogParser.Started;
    
    private CancellationTokenSource? liveLogCts;
    private Task? liveLogTask;
    private volatile bool isStoppingLiveLogging;
    private readonly Progress<string> liveLogProgress;
    
    public bool IsLiveLogging => liveLogTask is { IsCompleted: false };
    
    public event EventHandler? LiveLoggingStarted;
    public event EventHandler<string>? LiveLoggingReportCreated;
    public event EventHandler<string>? LiveLoggingProgress;
    public event EventHandler<AggregateException?>? LiveLoggingEnded;

    private CancellationTokenSource? monitorCts;
    private Task? monitorTask;
    private Task? collectTask;
    internal bool IsMonitoringActive => monitorTask is { IsCompleted: false };
    internal TimeSpan FightDuration { get; private set; } = TimeSpan.Zero;
    internal TimeSpan SegmentDuration { get; private set; } = TimeSpan.Zero;
    
    public FFLogsManager(Plugin plugin)
    {
        this.plugin = plugin;
        
        liveLogProgress = new Progress<string>();
        liveLogProgress.ProgressChanged += (_, s) => OnLiveLoggingProgress(s);
        
        Plugin.ClientState.ZoneInit += OnZoneInit;
        Plugin.DutyState.DutyWiped += OnDutyWipe;
    }

    public Task InitAsync(CancellationToken token = default)
    {
        return Task.Run(async () =>
        {
            await LoginFromConfigurationAsync(token);

            if (User != null)
                await StartParsersAsync(false, true, true, token);

            if (plugin.Configuration.EngageTimerPerPhase && MetersLogParser.Started)
                _ = StartMetersLogCollectionAsync();
        }, token);
    }
    
    public async ValueTask DisposeAsync()
    {
        StopLiveLogging();
        StopMetersLogCollection();

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

        Plugin.DutyState.DutyWiped -= OnDutyWipe;
        Plugin.ClientState.ZoneInit -= OnZoneInit;
        User = null;

        MetersLogParser.Dispose();
        LogParser.Dispose();
        DesktopClient.Dispose();
        
        GC.SuppressFinalize(this);
    }
    
    internal async Task<DesktopClient.LoginResponse> LoginAsync(
        string email, string password, bool automaticLogin, CancellationToken token = default)
    {
        IsLoggingIn = true;
        LoginError = null;
        try
        {
            if (email.IsNullOrWhitespace())
                throw new ArgumentException("email cannot be empty", nameof(email));

            if (password.IsNullOrWhitespace())
                throw new ArgumentException("password cannot be empty", nameof(password));

            User = await DesktopClient.LoginAsync(email, password, token);

            if (automaticLogin)
            {
                plugin.Configuration.FfLogsEmail = email;
                plugin.Configuration.FfLogsPassword = password;
            }
            else
            {
                plugin.Configuration.FfLogsEmail = string.Empty;
                plugin.Configuration.FfLogsPassword = string.Empty;
            }

            plugin.Configuration.FfLogsAutomaticLogin = automaticLogin;
            plugin.Configuration.Save();

            return User;
        }
        catch (Exception e)
        {
            LoginError = new AggregateException(e);
            throw;
        } 
        finally
        {
            IsLoggingIn = false;
        }
    }
    
    internal async Task<DesktopClient.LoginResponse?> LoginFromConfigurationAsync(CancellationToken token = default)
    {
        if (!plugin.Configuration.FfLogsAutomaticLogin)
            return null;

        return await LoginAsync(
                   plugin.Configuration.FfLogsEmail,
                   plugin.Configuration.FfLogsPassword,
                   plugin.Configuration.FfLogsAutomaticLogin,
                   token);
    }
    
    internal async Task LogoutAsync(CancellationToken token = default)
    {
        try
        {
            await DesktopClient.LogoutAsync(token);
        }
        catch (Exception e)
        {
            Plugin.Log.Error(e, "Logout failed");
            throw;
        }
        finally
        {
            User = null;
        
            plugin.Configuration.FfLogsEmail = string.Empty;
            plugin.Configuration.FfLogsPassword = string.Empty;
            plugin.Configuration.FfLogsAutomaticLogin = false;
            plugin.Configuration.Save();
        }
    }
    
    internal Task StartParsersAsync(
        bool gameContentDetectionEnabled, bool metersEnabled, bool liveFightDataEnabled, CancellationToken token = default)
    {
        ParsersError = null;

        return Task.Run(async () =>
        {
            var script = await DesktopClient.DownloadParserScript(
                             LogParser.Id, gameContentDetectionEnabled, false, false, token);
            var script2 = await DesktopClient.DownloadParserScript(
                              MetersLogParser.Id, gameContentDetectionEnabled, metersEnabled, liveFightDataEnabled,
                              token);

            await LogParser.StartAsync(gameContentDetectionEnabled, false, false, script, token);
            await MetersLogParser.StartAsync(gameContentDetectionEnabled, metersEnabled, liveFightDataEnabled, script2,
                                             token);
        }, token).ContinueWith(t => ParsersError = t.Exception, token);
    }
    
    private Task StartLiveLoggingAsync(string logFolder,
                                        long region,
                                        long visibility,
                                        long? guildId = null,
                                        string description = "",
                                        bool includeEntireFileInReport = false)
    {
        if (liveLogCts is { IsCancellationRequested: false })
            throw new InvalidOperationException("Live logging is already running.");
        
        liveLogCts = new CancellationTokenSource();
        liveLogTask = Task.Run(async () =>
        {
            if (logFolder.IsNullOrWhitespace())
                throw new ArgumentException("Path to log folder is missing.", nameof(logFolder));

            if (!Directory.Exists(logFolder))
                throw new ArgumentException("Specified log folder does not exist, or is a file.", nameof(logFolder));
            
            OnLiveLoggingStarted();
            
            // This is a bit of a hack, but since we do a lot of serialization between the log parser in V8 and the
            // .NET code, there is a lot of GC pressure. While this has also been alleviated with changes in other places,
            // setting this to SustainedLowLatency during live logging prevents the game from stuttering badly during
            // live logging, should bad come to worse.
            // TODO: Make this a configurable option.
            var oldLatencyMode = GCSettings.LatencyMode;
            GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;

            try
            {
                var logUploader = new LogUploader(DesktopClient, LogParser);

                await logUploader.StartLiveLogAsync(logFolder, region, visibility, guildId, description,
                                                    includeEntireFileInReport, liveLogProgress,
                                                    OnLiveLoggingReportCreated,
                                                    liveLogCts.Token);
            } 
            finally
            {
                GCSettings.LatencyMode = oldLatencyMode;
            }
        }).ContinueWith(task => OnLiveLoggingEnded(task.Exception));

        return liveLogTask;
    }

    internal Task StartLiveLoggingAsync(string description = "", bool includeEntireFileInReport = false)
    {
        var guildId = plugin.Configuration.SelectedGuildValue;

        return StartLiveLoggingAsync(plugin.Configuration.LiveLogFolder,
                                     plugin.Configuration.SelectedRegionValue,
                                     plugin.Configuration.SelectedVisibilityValue,
                                     guildId == -1 ? null : guildId,
                                     description, includeEntireFileInReport);
    }
    
    internal void StopLiveLogging()
    {
        liveLogCts?.Cancel();
        liveLogCts?.Dispose();
        liveLogCts = null;
    }

    protected virtual void OnLiveLoggingStarted()
    {
        LiveLoggingStarted?.Invoke(this, EventArgs.Empty);
    }
    
    protected virtual void OnLiveLoggingReportCreated(string reportCode)
    {
        LiveLoggingReportCreated?.Invoke(this, reportCode);
    }

    protected virtual void OnLiveLoggingProgress(string progress)
    {
        LiveLoggingProgress?.Invoke(this, progress);
    }

    protected virtual void OnLiveLoggingEnded(AggregateException? exception)
    {
        LiveLoggingEnded?.Invoke(this, exception);
    }

    internal Task StartMetersLogCollectionAsync()
    {
        monitorCts = new CancellationTokenSource();
        monitorTask = Task.Run(async () =>
            {
                var liveLogFolder = plugin.Configuration.LiveLogFolder;

                if (liveLogFolder.IsNullOrWhitespace() || !Directory.Exists(liveLogFolder))
                    return;

                Plugin.Log.Debug("Starting meters log parsing, LogFolder={LogFolder}", liveLogFolder);

                var logReader = new DirectoryLogReader(liveLogFolder);

                await MetersLogParser.ClearAsync();
                await MetersLogParser.SetLiveLoggingStartTimeAsync(
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

                await foreach (var chunk in logReader.IterateChunksAsync(token: monitorCts.Token))
                {
                    if (chunk == null)
                        continue;

                    await MetersLogParser.ParseLinesAsync(
                        chunk.Lines, plugin.Configuration.SelectedRegionValue, [], false,
                        chunk.EndPosition);
                }
            }, monitorCts.Token)
            .ContinueWith(t =>
            {
                if (t.Exception is { } e)
                    Plugin.Log.Error(e, "Meters log parsing failed");
            }, monitorCts.Token);
        collectTask = Task.Run(async () =>
            {
                Plugin.Log.Debug("Starting meters collection");
                
                while (true)
                {
                    if (await Task.DelayOrCancel(TimeSpan.FromMilliseconds(500), monitorCts.Token))
                        break;
                    
                    var meters = await MetersLogParser.CollectMetersAsync();
                    var fight = meters.Fights.LastOrDefault();

                    if (fight == null)
                        continue;

                    var segment = fight.Segments.MaxBy(s => s.StartTime);
                    
                    // Plugin.Log.Debug("Updating DTR entry: Fight={FightName} Segment={SegmentName}", fight.Encounter.Name, segment?.Encounter.Name);
                    
                    plugin.DtrBarEntry.Update(fight, segment);
                }
            }, monitorCts.Token)
            .ContinueWith(t =>
            {
                if (t.Exception is { } e)
                    Plugin.Log.Error(e, "Failed to collect meters");
            }, monitorCts.Token);
        
        return Task.WhenAll(monitorTask, collectTask);
    }

    internal void StopMetersLogCollection()
    {
        monitorCts?.Cancel();
        monitorCts?.Dispose();
        monitorCts = null;
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

    internal static async Task SplitLogFileAsync(string logFilePathToSplit, bool groupSameContent, IProgress<string>? progress = null)
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

        uint lastZoneId = 0;
        uint lastContentFinderId = 0; 
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
                if (!uint.TryParse(lineSplit.ElementAtOrDefault(2), NumberStyles.HexNumber,
                                  CultureInfo.InvariantCulture, out var zoneId))
                {
                    throw new SplitLogException($"Log file is invalid: line {lineNumber}'s zone ID is invalid");
                }

                if (groupSameContent)
                {
                    // If the user wants to group consecutive instanced raids together (regardless of in-between zones)
                    // then we need to check what instanced raid this zone belongs to and compare it against the last value
                    var contentFinderCondition = Plugin.DataManager
                        .GetExcelSheet<ContentFinderCondition>()
                        .FirstOrNull(cfc => cfc.TerritoryType.RowId == zoneId);

                    if (contentFinderCondition != null)
                    {
                        shouldSplitLog = lastContentFinderId != 0 && contentFinderCondition.Value.RowId != lastContentFinderId;
                        lastContentFinderId = contentFinderCondition.Value.RowId;
                    }
                }
                else
                {
                    shouldSplitLog = lastZoneId != 0 && zoneId != lastZoneId;
                    lastZoneId = zoneId;
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

    internal Task<long> GetParserVersionAsync() => LogParser.GetParserVersionAsync();
    internal Task CallWipeAsync() => LogParser.CallWipeAsync();
    
    // ZoneInit
    // - Valid duty and live logging is not active -> start live logging
    // - No duty and live logging is active -> stop live logging
    private void OnZoneInit(ZoneInitEventArgs args)
    {
        var cfCondition = args.ContentFinderCondition.ValueNullable;
        var territoryType = args.TerritoryType.ValueNullable;

        if (territoryType is null or { IsPvpZone: true })
        {
            Plugin.Log.Debug("Ignoring unknown territory or PvP zone (RowId={0})", territoryType?.RowId ?? 0L);
            return;
        }
        
        unsafe
        {
            var cf = ContentsFinder.Instance();
            ContentsFinderQueueInfo* cfQueueInfo;

            if (
                cf != null && (cfQueueInfo = cf->GetQueueInfo()) != null
                           && (cfQueueInfo->PoppedContentIsUnrestrictedParty ||
                               cfQueueInfo->PoppedContentIsExplorerMode)
            )
            {
                Plugin.Log.Debug("Ignoring automatic live logging for unrestricted parties/explorer mode");
                return;
            }
        }
        
        // To automatically start live logging:
        // - User must enable the option
        // - The parser has been successfully loaded
        // - Is not already live logging
        // - Duty has not started (should handle mid-duty joins)
        // - Valid content type for parsing
        //   - https://exd.camora.dev/sheet/ContentType
        //   - 1 = Dungeons, 3 = Trials, 4 = Raids, 6 = Ultimate Raids, 7 = Chaotic Alliance Raid
        if (plugin.Configuration.StartLiveLoggingWhenDutyStarts
            && LogParser.Started
            && !IsLiveLogging
            && !Plugin.DutyState.IsDutyStarted
            && !Plugin.Condition[ConditionFlag.DutyRecorderPlayback]
            && cfCondition?.ContentType.ValueNullable is { Unknown2: 1 or 3 or 4 or 6 or 7 })
        {
            StartLiveLoggingAsync();
        }
        else if (plugin.Configuration.StopLiveLoggingWhenDutyEnds
                 && LogParser.Started
                 && IsLiveLogging
                 && !isStoppingLiveLogging
                 && cfCondition is null or { RowId: 0 })
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
        if (plugin.Configuration.AutomaticallyCallDutyWipe && LogParser.Started && IsLiveLogging)
            Task.Run(LogParser.CallWipeAsync).ContinueWith(task =>
            {
                if (task.Exception == null)
                {
                    Plugin.NotificationManager.AddNotification(new Notification
                    {
                        Type = NotificationType.Success,
                        Title = "Called wipe automatically.",
                        MinimizedText = "Called wipe automatically.",
                        Minimized = true,
                    });
                    return;
                }
                
                Plugin.Log.Error(task.Exception, "Failed to automatically call a wipe");
                Plugin.NotificationManager.AddNotification(new Notification
                {
                    Type = NotificationType.Error,
                    Title = "Failed to automatically call a wipe",
                    Content = "Use /callwipe to call a wipe manually. View logs from /xllog for details.",
                    MinimizedText = "Failed to automatically call a wipe",
                    Minimized = false,
                });
            });
    }
}
