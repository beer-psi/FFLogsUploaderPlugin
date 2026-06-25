// ReSharper disable ClassNeverInstantiated.Global
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ClearScript;
using Microsoft.ClearScript.V8;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace FFLogsUploaderPlugin.FFLogs;

/*
 * FFLogs log parser by executing the official parser in an isolated V8 environment with some shims and polyfills.
 */
public class LogParser(int id = 1) : IDisposable
{
    // ensure that everything runs in a single thread since V8 is single-threaded
    private readonly ConcurrentExclusiveSchedulerPair scheduler = new();
    private V8ScriptEngine engine = new();
    private ScriptObject? parserReceiveMessage;
    private readonly JsonSerializerSettings jsonSerializerSettings = new()
    {
        ContractResolver = new CamelCasePropertyNamesContractResolver()
    };
    private bool disposed;

    public int Id => id;
    public bool Started => parserReceiveMessage != null;

    public async Task StartAsync(bool gameContentDetectionEnabled, bool metersEnabled, bool liveFightDataEnabled, string parserCode)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        
        if (Started)
        {
            parserReceiveMessage = null;
            engine.Dispose();
            engine = new V8ScriptEngine();
        }
        
        await Task.Factory.StartNew(() =>
        {
            engine.Execute($$"""
                           globalThis.window = globalThis;
                           window.receiveMessageFns = [];
                           window.addEventListener = (type, listener, options) => {
                               if (type == "message") {
                                   receiveMessageFns.push(listener);
                               }
                           };
                           window.location = {
                               search: '?id=1'
                                   + '&ts={{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}}'
                                   + '&gameContentDetectionEnabled={{gameContentDetectionEnabled.ToString().ToLower()}}'
                                   + '&metersEnabled={{metersEnabled.ToString().ToLower()}}'
                                   + '&liveFightDataEnabled={{liveFightDataEnabled.ToString().ToLower()}}' 
                           };
                           """);
            engine.Execute(File.ReadAllText(Path.Combine(Plugin.PluginInterface.AssemblyLocation.Directory?.FullName!, "url-search-params.js")));
            engine.Execute(parserCode);
            engine.Execute("""
                           window.sendToHost = (msg, id, event, obj = null) => {
                                event.source.postMessage({ message: msg, id, data: obj }, event.origin);
                           };
                           window.__parserReceiveMessage = (data, callback) => {
                               const source = {
                                   postMessage(message, origin) {
                                       callback(JSON.stringify(
                                           message,
                                           (key, value) => {
                                               if (value instanceof Error) {
                                                   const error = {};
                                                   
                                                   Object.getOwnPropertyNames(value).forEach((name) => {
                                                       error[name] = value[name];
                                                   });
                                                   
                                                   return error;
                                               }
                                               
                                               return value;
                                           },
                                       ));
                                   },
                               };
                               
                               for (const fn of receiveMessageFns) {
                                   fn({ source, origin: 'http://localhost:8080', data: JSON.parse(data) });
                               }
                           };
                           """);
            parserReceiveMessage = engine.Script.__parserReceiveMessage;
        }, CancellationToken.None, TaskCreationOptions.DenyChildAttach, scheduler.ExclusiveScheduler);
    }

    private async Task<T> CallParserAsync<T>(ParserRequest request, string completedMessageType)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        
        if (parserReceiveMessage == null)
        {
            throw new InvalidOperationException("Parser has not been started");
        }

        var tcs = new TaskCompletionSource<T>();
        var serializedMessage = JsonConvert.SerializeObject(request, jsonSerializerSettings);

        await Task.Factory.StartNew(() => parserReceiveMessage.Invoke(
                           false,
                           serializedMessage,
                           (string callbackMessage) =>
                           {
                               var response =
                                   JsonConvert.DeserializeObject<ParserResponse>(
                                       callbackMessage, jsonSerializerSettings);

                               if (response == null)
                               {
                                   return;
                               }

                               if (response.Message == completedMessageType)
                               {
                                   tcs.TrySetResult(response.Data.ToObject<T>()!);
                               }
                               else switch (response.Message)
                               {
                                   case "set-error-text":
                                       tcs.TrySetException(new ParserException(response.Data.ToObject<string>()));
                                       break;
                                   case "set-warning-text":
                                       Plugin.Log.Warning("[LogParser] {0}", response.Data.ToObject<string>()!);
                                       break;
                                   case "event":
                                       Plugin.Log.Information("[LogParser] [Event] {0}", callbackMessage);
                                       break;
                                   case "log-message":
                                       var logMessage = response.Data.ToObject<List<string>>();
                                       if (logMessage != null)
                                       {
                                           Plugin.Log.Information("[LogParser] {0}", string.Join(" ", logMessage));
                                       }
                                       break;
                                   default:
                                       Plugin.Log.Warning("[LogParser] [Unknown] {0}", callbackMessage);
                                       break;
                               }
                           }), CancellationToken.None, TaskCreationOptions.DenyChildAttach, scheduler.ExclusiveScheduler);

        return await tcs.Task;
    }

    public async Task<long> GetParserVersionAsync()
    {
        return await CallParserAsync<long>(new ParserRequest { Id = id, Message = "get-parser-version" },
                                     "get-parser-version-completed");
    }

    public async Task SetReportCodeAsync(string reportCode)
    {
        await CallParserAsync<object?>(
            new SetReportCodeRequest { Id = id, Message = "set-report-code", ReportCode = reportCode },
            "set-report-code-completed");
    }

    public async Task SetStartDateAsync(long startDate)
    {
        await CallParserAsync<object?>(
            new SetStartDateRequest { Id = id, Message = "set-start-date", StartDate = startDate },
            "set-start-date-completed");
    }

    public async Task SetLiveLoggingStartTimeAsync(long startTime)
    {
        await CallParserAsync<object?>(
            new SetLiveLoggingStartTimeRequest
                { Id = id, Message = "set-live-logging-start-time", StartTime = startTime },
            "set-live-logging-start-time-completed");
    }

    /// <summary>
    /// Send a request to parse lines to the parser.
    /// </summary>
    /// <param name="lines">List of lines to parse. Each line must have its trailing newline removed.</param>
    /// <param name="selectedRegion">
    /// ID of the region this log is from. The region IDs are obtained server side from the desktop client's login
    /// response (<see cref="DesktopClient.LoginResponse"/>), so it is not an enum. Currently: NA = 1, EU = 2, JP = 3,
    /// OC = 6 (I assume CN/TW/KR are in between).
    /// </param>
    /// <param name="raidsToUpload">
    /// List of raids to upload, retrieved from <see cref="CollectScannedRaidsAsync"/>. This must be ordered by
    /// <see cref="ScannedRaid.Start"/> in ascending order.
    /// </param>
    /// <param name="scanning">
    /// Parse lines in scanning mode. This is used only in conjunction with <see cref="CollectScannedRaidsAsync"/>.
    /// </param>
    /// <param name="logFilePosition">Position of the file at the end of the log lines. This is mostly used for internal bookkeeping.</param>
    /// <returns></returns>
    public async Task<ParseLinesResponseData> ParseLinesAsync(
        List<string> lines, long selectedRegion, List<ScannedRaid> raidsToUpload, bool scanning, long logFilePosition)
    {
        return await CallParserAsync<ParseLinesResponseData>(
                   new ParseLinesRequest
                   {
                       Id = id, Message = "parse-lines", Lines = lines, SelectedRegion = selectedRegion,
                       RaidsToUpload = raidsToUpload, Scanning = scanning, LogFilePosition = logFilePosition
                   },
                   "parse-lines-completed");
    }

    public async Task ClearFightsAsync()
    {
        await CallParserAsync<object?>(new ParserRequest { Id = id, Message = "clear-fights" }, "clear-fights-completed");
    }

    public async Task ClearStateAsync()
    {
        await CallParserAsync<object?>(new ParserRequest { Id = id, Message = "clear-state" }, "clear-state-completed");
    }

    public async Task ClearMetersAsync()
    {
        await CallParserAsync<object?>(new ParserRequest { Id = id, Message = "clear-meters" }, "clear-meters-completed");
    }

    public async Task ClearAsync()
    {
        await ClearFightsAsync();
        await ClearStateAsync();
    }

    public async Task<List<ScannedRaid>> CollectScannedRaidsAsync()
    {
        return await CallParserAsync<List<ScannedRaid>>(new ParserRequest { Id = id, Message = "collect-scanned-raids" },
                                                   "collect-scanned-raids-completed");
    }

    public async Task<CollectFightsResponseData> CollectFightsAsync(bool pushFightIfNeeded, bool scanningOnly)
    {
        return await CallParserAsync<CollectFightsResponseData>(
                   new CollectFightsRequest
                   {
                       Id = id, Message = "collect-fights", PushFightIfNeeded = pushFightIfNeeded,
                       ScanningOnly = scanningOnly
                   }, "collect-fights-completed");
    }
    
    public async Task<CollectFightsResponseData> CollectInProgressFightAsync()
    {
        return await CallParserAsync<CollectFightsResponseData>(
                   new ParserRequest { Id = id, Message = "collect-in-progress-fight" },
                   "collect-in-progress-fight-completed");
    }

    public async Task<CollectMasterInfoResponseData> CollectMasterInfoAsync(string reportCode)
    {
        return await CallParserAsync<CollectMasterInfoResponseData>(
                   new SetReportCodeRequest { Id = id, Message = "collect-master-info", ReportCode = reportCode },
                   "collect-master-info-completed");
    }

    public async Task CallWipeAsync()
    {
        await CallParserAsync<object?>(new ParserRequest { Id = id, Message = "call-wipe" }, "call-wipe-completed");
    }
    
    // collect-game-content
    // collect-meters
    // collect-live-fight-data
    // clear-meters
    // check-dungeon-inactivity
    // force-end-game-content
    
    public void Dispose()
    {
        if (disposed) return;
        
        engine.Dispose();
        parserReceiveMessage = null;
        
        disposed = true;
        GC.SuppressFinalize(this);
    }

    internal class ParserException : Exception
    {
        internal ParserException(string? message) : base(message) { }
    }
    
    internal class ParserRequest
    {
        public required int Id { get; set; }
        public required string Message { get; set; }
    }
    
    internal class SetReportCodeRequest : ParserRequest
    {
        public required string ReportCode { get; set; }
    }
    
    internal class SetStartDateRequest : ParserRequest
    {
        public required long StartDate { get; set; }
    }
    
    internal class SetLiveLoggingStartTimeRequest : ParserRequest
    {
        public required long StartTime { get; set; }
    }
    
    internal class ParseLinesRequest : ParserRequest
    {
        public required List<string> Lines { get; set; }
        public required long SelectedRegion { get; set; }
        public required List<ScannedRaid> RaidsToUpload { get; set; }
        public required bool Scanning { get; set; }
        public required long LogFilePosition { get; set; }
    }
    
    internal class CollectFightsRequest : ParserRequest
    {
        public required bool PushFightIfNeeded { get; set; }
        public required bool ScanningOnly { get; set; }
    }
    
    internal class ParserResponse
    {
        public required string Message { get; set; }
        public required JToken Data { get; set; }
    }

    public class ParseLinesResponseData
    {
        public required bool Success { get; set; }
        public long? ParsedLineCount { get; set; }
        public string? Line { get; set; }
        public JToken? Exception { get; set; }
    }

    public class ScannedRaid
    {
        public required bool Success { get; set; }
        public required string Name { get; set; }
        public required List<string> Friendlies { get; set; }
        public required List<string> Enemies { get; set; }
        public required long Start { get; set; }
        public required long End { get; set; }
        public required long Boss { get; set; }
        public required long Difficulty { get; set; }
        public required long Pulls { get; set; }
        public required long ZoneStart { get; set; }
    }

    public class Fight
    {
        public required long EventCount { get; set; }
        public required string EventsString { get; set; }
    }

    public class CollectFightsResponseData
    {
        public required long LogVersion { get; set; }
        public required long GameVersion { get; set; }
        public required string LogFileDetails { get; set; }
        public required long Mythic { get; set; }
        public required long StartTime { get; set; }
        public required long EndTime { get; set; }
        public required List<Fight> Fights { get; set; }
    }

    public class CollectMasterInfoResponseData
    {
        public required bool Success { get; set; }
        public required long LastAssignedActorId { get; set; }
        public required string ActorsString { get; set; }
        public required long LastAssignedAbilityId { get; set; }
        public required string AbilitiesString { get; set; }
        public required long LastAssignedTupleId { get; set; }
        public required string TuplesString { get; set; }
        public required long LastAssignedPetId { get; set; }
        public required string PetsString { get; set; }
    }
}
