using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Utility;
using Newtonsoft.Json;
using Serilog.Events;

namespace FFLogsUploaderPlugin.FFLogs;

public class LogUploader(DesktopClient desktopClient, LogParser logParser)
{
    public class LogUploaderException(string message) : Exception(message);
    
    public int FightsUploaded { get; private set; }

    public async Task StartLiveLogAsync(
        string logFolder,
        long region,
        long visibility,
        long? guildId = null,
        string description = "",
        bool includeEntireFileInReport = false,
        IProgress<string>? progress = null,
        Action<string>? reportCodeCallback = null,
        CancellationToken token = default)
    {
        progress?.Report("Live logging started.");
        FightsUploaded = 0;
        await logParser.ClearAsync();
        
        progress?.Report("Creating FFLogs report.");
        var uploadTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var report = await desktopClient.CreateReportAsync(
                         await logParser.GetParserVersionAsync(),
                         uploadTime,
                         uploadTime,
                         guildId,
                         "live.log",
                         region,
                         visibility,
                         description,
                         token);
        reportCodeCallback?.Invoke(report.Code);

        await logParser.SetReportCodeAsync(report.Code);

        var segmentId = 1L;
        var latestLogFile = FindLatestLogFileInFolder(logFolder);
        var latestLogFileInfo = latestLogFile != null ? new FileInfo(latestLogFile) : null;
        var latestLogFilePosition = 0L;

        if (latestLogFile != null && latestLogFileInfo is { Length: >0 })
        {
            if (!includeEntireFileInReport)
                await logParser.SetLiveLoggingStartTimeAsync(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

            var catchupAction = includeEntireFileInReport ? "Uploading" : "Parsing";
            
            progress?.Report($"{catchupAction} latest log file {Path.GetFileName(latestLogFile)} (0%)");
            
            // We intentionally don't use a cancellation token here because we check for cancellation every time
            // after a chunk is uploaded.
            // ReSharper disable once UseCancellationTokenForIAsyncEnumerable
            await foreach (var chunk in LogReader.ReadFileChunkedLinesAsync(latestLogFile))
            {
                // SetLiveLoggingStartTimeAsync above will stop previous logs from being uploaded.
                segmentId = await UploadLogPartAsync(report.Code, chunk.Lines, chunk.EndPosition, chunk.IsEof,
                                                     segmentId, region,
                                                     [], true, false, false);
                latestLogFilePosition = chunk.EndPosition;
                progress?.Report($"{catchupAction} latest log file {Path.GetFileName(latestLogFile)} ({Math.Min(100, chunk.EndPosition * 100 / latestLogFileInfo.Length)}%, {chunk.EndPosition}/{latestLogFileInfo.Length})");

                if (token.IsCancellationRequested)
                    break;
            }
        }
        
        latestLogFileInfo = latestLogFile != null ? new FileInfo(latestLogFile) : null;
        Plugin.Log.Debug("Catch-up completed: LatestLogFile={0} CurrentPosition={1} Length={2}", latestLogFile ?? "None",
                         latestLogFilePosition, latestLogFileInfo?.Length ?? 0L);

        if (token.IsCancellationRequested)
        {
            await desktopClient.TerminateReport(report.Code);
            return;
        }

        Plugin.Log.Debug("Staring main live log watch loop");

        progress?.Report(latestLogFile != null
                             ? $"Watching for new logs from {Path.GetFileName(latestLogFile)}."
                             : "Waiting for a log file. Log files last written more than 6 hours ago are not considered for live logging.");

        while (true)
        {
            var newLatestLogFile = FindLatestLogFileInFolder(logFolder);
            
            // If there are no new log files, wait for one
            if (newLatestLogFile == null) {
                // But if cancellation has been requested, finish up the current log file and bail
                if (token.IsCancellationRequested)
                {
                    if (latestLogFile != null)
                    {
                        await UploadLogPartAsync(report.Code, [], latestLogFileInfo!.Length, true, segmentId,
                                                region, [], true, false, true);
                    }
                    
                    break;
                }

                // We already checked cancellation above.
                // ReSharper disable once MethodSupportsCancellation
#pragma warning disable CA2016
                await Task.Delay(1000);
#pragma warning restore CA2016
                continue;
            }
            
            // If there is a latest log file, and it is different from the current latest log file (aka there's a newer
            // one), then switch to reading that file
            if (newLatestLogFile != latestLogFile)
            {
                Plugin.Log.Debug($"[LiveLog] Log file changed: {latestLogFile} -> {newLatestLogFile}");
                
                latestLogFile = newLatestLogFile;
                latestLogFilePosition = 0L;
                
                progress?.Report($"Watching for new logs from {Path.GetFileName(latestLogFile)}.");
            }
            
            // Update log file info all the time so we get proper file sizes.
            latestLogFileInfo = new FileInfo(latestLogFile);

            // Push fights if the log file has not changed for 120 seconds, or if cancellation is requested.
            var isIdleLogFile = DateTime.UtcNow.Subtract(File.GetLastWriteTimeUtc(latestLogFile)).TotalSeconds > 120;
            var pushFightIfNeeded = token.IsCancellationRequested || isIdleLogFile;

            if (pushFightIfNeeded)
            {
                Plugin.Log.Debug("[LiveLog] PushFightIfNeeded={0} (CancellationRequested={1}, IdleLogFile={2})",
                                   pushFightIfNeeded, token.IsCancellationRequested, isIdleLogFile);
            }
            
            // Upload logs from the latest log file, starting from the log position
            // ReSharper disable once UseCancellationTokenForIAsyncEnumerable
            await foreach (var chunk in LogReader.ReadFileChunkedLinesAsync(latestLogFile,
                                                                    startingPosition: latestLogFilePosition))
            {
                if (Plugin.Log.MinimumLogLevel <= LogEventLevel.Verbose)
                {
                    var joinedLines = string.Join("\n", chunk.Lines);
                    joinedLines = joinedLines[..Math.Min(500, joinedLines.Length)];

                    if (!joinedLines.IsNullOrWhitespace())
                    {
                        Plugin.Log.Verbose(joinedLines[..Math.Min(500, joinedLines.Length)]);    
                    }
                }
                
                segmentId = await UploadLogPartAsync(report.Code, chunk.Lines, chunk.EndPosition, chunk.IsEof,
                                                     segmentId,
                                                     region, [], true, false,
                                                     pushFightIfNeeded);
                latestLogFilePosition = chunk.EndPosition;
                
                progress?.Report($"Uploading latest log file {Path.GetFileName(latestLogFile)} ({Math.Min(100, chunk.EndPosition * 100 / latestLogFileInfo.Length)}%, {chunk.EndPosition}/{latestLogFileInfo.Length}), {FightsUploaded} fights uploaded");

                if (token.IsCancellationRequested)
                    break;
            }
            
            if (token.IsCancellationRequested)
                break;
        }

        await desktopClient.TerminateReport(report.Code);
        progress?.Report("Live logging completed.");
    }

    public async Task<string> UploadLogFileAsync(
        string logFilePath,
        long region,
        long visibility,
        long? guildId = null,
        string description = "",
        List<LogParser.ScannedRaid>? raidsToUpload = null,
        IProgress<string>? progress = null)
    {
        progress?.Report("Starting log file upload");
        await logParser.ClearAsync();
        
        progress?.Report("Creating FFLogs report");
        var uploadTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var report = await desktopClient.CreateReportAsync(
                         await logParser.GetParserVersionAsync(),
                         uploadTime,
                         uploadTime,
                         guildId,
                         Path.GetFileName(logFilePath),
                         region,
                         visibility,
                         description);
        var reportCode = report.Code;
        var segmentId = 1L;
        var logFileSize = new FileInfo(logFilePath).Length;
        
        await logParser.SetReportCodeAsync(reportCode);

        progress?.Report("Uploading log file (0%)");
        
        // The official uploader will process 5000 lines of the log file at a time, maximum 8MB per chunk.
        // ACT log files should not be 8MB per 5000 lines, so we don't care about that.
        await foreach (var chunk in LogReader.ReadFileChunkedLinesAsync(logFilePath))
        {
            segmentId = await UploadLogPartAsync(reportCode, chunk.Lines, chunk.EndPosition, chunk.IsEof, segmentId, region,
                                                 raidsToUpload ?? [], false, false, false);
            progress?.Report($"Uploading log file ({Math.Min(100, chunk.EndPosition * 100 / logFileSize)}%, {chunk.EndPosition}/{logFileSize}), {FightsUploaded} fights uploaded");
        }
        
        progress?.Report("Finalizing FFLogs report");

        await logParser.ClearAsync();
        await desktopClient.TerminateReport(reportCode);

        return reportCode;
    }

    private static string? FindLatestLogFileInFolder(string logFolder)
    {
        var latestLogFile = Directory.EnumerateFiles(logFolder, "Network_*.log", SearchOption.TopDirectoryOnly)
                 .OrderByDescending(File.GetLastWriteTimeUtc)
                 .FirstOrDefault();

        if (latestLogFile == null || DateTime.UtcNow.Subtract(File.GetLastWriteTimeUtc(latestLogFile)).TotalHours >= 6)
            return null;

        return latestLogFile;
    }
    
    private async Task<long> UploadLogPartAsync(
        string reportCode, List<string> lines, long startPosition, bool isEof, long segmentId, long region,
        List<LogParser.ScannedRaid> raidsToUpload, bool isLiveLog, bool isRealTime, bool pushFightIfNeeded)
    {
        var result = await logParser.ParseLinesAsync(lines, region, raidsToUpload, false, startPosition);

        if (!result.Success)
        {
            Plugin.Log.Error($"[LogUploader] Failed to parse log line {result.ParsedLineCount}\n{result.Line}\n{JsonConvert.SerializeObject(result.Exception, Formatting.Indented)}");
            throw new LogUploaderException("Failed to parse a log line, please check Dalamud logs (/xllog)");
        }

        var fightData = await logParser.CollectFightsAsync(
                            pushFightIfNeeded || (isEof && !isLiveLog),
                            false);
        var hasInProgressFight = false;

        if (isLiveLog && fightData.Fights.Count <= 0)
        {
            var inProgressFightData = await logParser.CollectInProgressFightAsync();

            hasInProgressFight = inProgressFightData.Fights.Count > 0;

            if (isRealTime)
            {
                fightData = inProgressFightData;
            }
        }

        if (fightData.Fights.Count <= 0)
        {
            return segmentId;
        }

        var masterInfo = await logParser.CollectMasterInfoAsync(reportCode);

        if (!masterInfo.Success)
        {
            await logParser.ClearAsync();
            throw new LogUploaderException("Failed to collect master info from parser");
        }

        var masterTable = BuildMasterTable(fightData.LogVersion, fightData.GameVersion,
                                           fightData.LogFileDetails, masterInfo);

        //Plugin.Log.Debug("[LogUploader] Master table: {0}", masterTable);

        try
        {
            await desktopClient.SetReportMasterTable(reportCode, segmentId, isRealTime, CompressStringIntoZipBlob(masterTable));
        }
        catch (Exception e)
        {
            Plugin.Log.Error(e, "Failed to set report master table");
            await logParser.ClearAsync();
            throw;
        }

        var fightsTable = BuildFightsTable(fightData.LogVersion, fightData.GameVersion, fightData.Fights);

        //Plugin.Log.Debug("[LogUploader] Fights table: {0}", fightsTable);

        DesktopClient.AddReportSegmentResponse addReportSegmentResponse;
        try
        {
            addReportSegmentResponse = await desktopClient.AddReportSegment(
                                           reportCode,
                                           CompressStringIntoZipBlob(fightsTable),
                                           fightData.StartTime,
                                           fightData.EndTime,
                                           fightData.Mythic,
                                           isLiveLog,
                                           isRealTime,
                                           (isRealTime && hasInProgressFight) ? fightData.Fights[0].EventCount : 0L,
                                           segmentId);
        }
        catch (Exception e)
        {
            Plugin.Log.Error(e, "Failed to add report segment");
            await logParser.ClearAsync();
            throw;
        }

        FightsUploaded += fightData.Fights.Count;

        await logParser.ClearFightsAsync();
        return addReportSegmentResponse.NextSegmentId;
    }

    private static string BuildMasterTable(
        long logVersion, long gameVersion, string logFileDetails, LogParser.CollectMasterInfoResponseData masterInfo)
    {
        var sb = new StringBuilder(
            logFileDetails.Length
            + masterInfo.ActorsString.Length
            + masterInfo.AbilitiesString.Length
            + masterInfo.TuplesString.Length
            + masterInfo.PetsString.Length
            + 7 + 114); // separator characters + numbers

        sb.Append(logVersion);
        sb.Append('|');
        sb.Append(gameVersion);
        sb.Append('|');
        sb.Append(logFileDetails);
        sb.Append('\n');

        sb.Append(masterInfo.LastAssignedActorId);
        sb.Append('\n');
        sb.Append(masterInfo.ActorsString);

        if (!masterInfo.ActorsString.EndsWith('\n'))
            sb.Append('\n');

        sb.Append(masterInfo.LastAssignedAbilityId);
        sb.Append('\n');
        sb.Append(masterInfo.AbilitiesString);
        
        if (!masterInfo.AbilitiesString.EndsWith('\n'))
            sb.Append('\n');
        
        sb.Append(masterInfo.LastAssignedTupleId);
        sb.Append('\n');
        sb.Append(masterInfo.TuplesString);
        
        if (!masterInfo.TuplesString.EndsWith('\n'))
            sb.Append('\n');
        
        sb.Append(masterInfo.LastAssignedPetId);
        sb.Append('\n');
        sb.Append(masterInfo.PetsString);
        
        if (!masterInfo.PetsString.EndsWith('\n'))
            sb.Append('\n');

        return sb.ToString();
    }

    private static string BuildFightsTable(long logVersion, long gameVersion, List<LogParser.Fight> fights)
    {
        var totalEvents = 0L;
        var eventsStringBuilder = new StringBuilder();

        foreach (var fight in fights)
        {
            totalEvents += fight.EventCount;
            eventsStringBuilder.Append(fight.EventsString);
        }

        return $"{logVersion}|{gameVersion}\n{totalEvents}\n{eventsStringBuilder}";
    }

    private static byte[] CompressStringIntoZipBlob(string data)
    {
        using var ms = new MemoryStream();
        using (var zipArchive = new ZipArchive(ms, ZipArchiveMode.Create))
        {
            var entry = zipArchive.CreateEntry("log.txt", CompressionLevel.SmallestSize);
            using var sw = new StreamWriter(entry.Open());
        
            sw.Write(data);
        }
        
        return ms.ToArray();
    }
}
