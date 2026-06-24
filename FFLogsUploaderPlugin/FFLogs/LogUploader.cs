using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FFLogsUploaderPlugin.FFLogs;

public class LogUploader(DesktopClient desktopClient, LogParser logParser)
{
    public class LogUploaderException(string message) : Exception(message);

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
            await foreach (var chunk in ReadFileByChunkedLinesAsync(latestLogFile))
            {
                // SetLiveLoggingStartTimeAsync above will stop previous logs from being uploaded.
                segmentId = await UploadLogPartAsync(report.Code, chunk.Lines, chunk.EndPosition, chunk.IsEof,
                                                     segmentId, region,
                                                     [], true, false, false);
                latestLogFilePosition = chunk.EndPosition;
                progress?.Report($"{catchupAction} latest log file {Path.GetFileName(latestLogFile)} ({Math.Min(100, chunk.EndPosition * 100 / latestLogFile.Length)}%, {chunk.EndPosition}/{latestLogFile.Length})");

                if (token.IsCancellationRequested)
                    break;
            }
        }

        if (token.IsCancellationRequested)
        {
            await desktopClient.TerminateReport(report.Code);
            return;
        }

        progress?.Report(latestLogFile != null
                             ? $"Watching for new logs from {Path.GetFileName(latestLogFile)}."
                             : "Waiting for a log file. Log files last written more than 6 hours ago are not considered for live logging.");

        while (!token.IsCancellationRequested)
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
                latestLogFile = newLatestLogFile;
                latestLogFilePosition = 0L;
                
                progress?.Report($"Watching for new logs from {Path.GetFileName(latestLogFile)}.");
            }
            
            // Update log file info all the time so we get proper file sizes.
            latestLogFileInfo = new FileInfo(latestLogFile);

            // Push fights if the log file has not changed for 120 minutes, or if cancellation is requested.
            var pushFightIfNeeded = token.IsCancellationRequested ||
                                    DateTime.UtcNow.Subtract(File.GetLastWriteTimeUtc(latestLogFile)).TotalSeconds > 120;

            // Upload logs from the latest log file, starting from the log position
            // ReSharper disable once UseCancellationTokenForIAsyncEnumerable
            await foreach (var chunk in ReadFileByChunkedLinesAsync(latestLogFile,
                                                                    startingPosition: latestLogFilePosition))
            {
                segmentId = await UploadLogPartAsync(report.Code, chunk.Lines, chunk.EndPosition, chunk.IsEof,
                                                     segmentId,
                                                     region, [], true, false,
                                                     pushFightIfNeeded);
                latestLogFilePosition = chunk.EndPosition;
                
                progress?.Report($"Uploading latest log file {Path.GetFileName(latestLogFile)} ({Math.Min(100, chunk.EndPosition * 100 / latestLogFileInfo.Length)}%, {chunk.EndPosition}/{latestLogFileInfo.Length})");

                if (token.IsCancellationRequested)
                    break;
            }
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
        await foreach (var chunk in ReadFileByChunkedLinesAsync(logFilePath))
        {
            segmentId = await UploadLogPartAsync(reportCode, chunk.Lines, chunk.EndPosition, chunk.IsEof, segmentId, region,
                                                 raidsToUpload ?? [], false, false, false);
            progress?.Report($"Uploading log file ({Math.Min(100, chunk.EndPosition * 100 / logFileSize)}%, {chunk.EndPosition}/{logFileSize})");
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

    private static readonly FieldInfo CharPosField = typeof(StreamReader).GetField("_charPos", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)!;
    private static readonly FieldInfo CharLenField = typeof(StreamReader).GetField("_charLen", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)!;
    private static readonly FieldInfo CharBufferField = typeof(StreamReader).GetField("_charBuffer", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)!;

    private static long GetStreamPosition(StreamReader sr)
    {
        var charBuffer = (char[])CharBufferField.GetValue(sr)!;
        var charLen = (int)CharLenField.GetValue(sr)!;
        var charPos = (int)CharPosField.GetValue(sr)!;
        
        return sr.BaseStream.Position - sr.CurrentEncoding.GetByteCount(charBuffer, charPos, charLen - charPos);
    }

    // Enumerates through chunks of maxLinesPerChunk of the given file at a time.
    // Returns a 3-tuple: (startPosition, isEof, lines)
    private static async IAsyncEnumerable<FileChunk> ReadFileByChunkedLinesAsync(
        string filePath, int maxLinesPerChunk = 5000, long startingPosition = 0L)
    {
        // Have to create a raw filestream for seeking first since StreamReader is horrendously unusable for seeking
        // and determining stream position
        await using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        if (fs.CanSeek && startingPosition != 0L)
            fs.Seek(startingPosition, SeekOrigin.Begin);
        
        using var sr = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var lines = new List<string>(maxLinesPerChunk);

        while (true)
        {
            var line = await sr.ReadLineAsync();

            if (line != null)
            {
                lines.Add(line);
            }

            // If there are enough lines in the chunk, or there are no lines left, yield what we have.
            if (lines.Count >= maxLinesPerChunk || line == null)
            {
                yield return new FileChunk
                {
                    EndPosition = GetStreamPosition(sr),
                    IsEof = line == null,
                    Lines = lines,
                };

                if (line != null)
                {
                    lines = new List<string>(maxLinesPerChunk);
                }
            }

            // If there are no lines left, break.
            if (line == null)
            {
                break;
            }
        }
    }
    
    private async Task<long> UploadLogPartAsync(
        string reportCode, List<string> lines, long startPosition, bool isEof, long segmentId, long region,
        List<LogParser.ScannedRaid> raidsToUpload, bool isLiveLog, bool isRealTime, bool pushFightIfNeeded)
    {
        await logParser.ParseLinesAsync(lines, region, raidsToUpload, false, startPosition);

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

        sb.Append(masterInfo.LastAssignedAbilityId);
        sb.Append('\n');
        sb.Append(masterInfo.AbilitiesString);
        
        sb.Append(masterInfo.LastAssignedTupleId);
        sb.Append('\n');
        sb.Append(masterInfo.TuplesString);
        
        sb.Append(masterInfo.LastAssignedPetId);
        sb.Append('\n');
        sb.Append(masterInfo.PetsString);

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

    private class FileChunk
    {
        public required long EndPosition;
        public required bool IsEof;
        public required List<string> Lines;
    }
}
