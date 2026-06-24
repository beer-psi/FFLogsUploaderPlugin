// See https://aka.ms/new-console-template for more information

using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text;
using Microsoft.ClearScript;
using Microsoft.ClearScript.V8;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

// Console.WriteLine(Directory.EnumerateFiles("/home/beerpsi/Documents/IINACT", "Network_*.log", SearchOption.TopDirectoryOnly)
//                            .OrderByDescending(File.GetLastWriteTimeUtc)
//                            .FirstOrDefault());

using var parser = new LogParser();

await parser.StartAsync(false, false, false, string.Empty);
await parser.ClearAsync();
await parser.SetReportCodeAsync("test");
// await parser.SetLiveLoggingStartTimeAsync(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

using var sr = new StreamReader("/home/beerpsi/Documents/IINACT/Network_30202_20260620.log", Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
var logLines = new List<string>(5000);
var logFilePosition = 0L;

while (true)
{
    var line = await sr.ReadLineAsync();

    if (line != null)
    {
        logLines.Add(line);
    }

    if (logLines.Count >= 5000 || line == null)
    {
        await parser.ParseLinesAsync(logLines, 3, [], true, logFilePosition);
        
        logLines.Clear();
        logFilePosition = GetStreamPosition(sr);
    }

    if (line == null)
    {
        break;
    }
}

var collectScannedRaids = await parser.CollectScannedRaidsAsync();
Console.WriteLine(collectScannedRaids.Count);

var collectFights = await parser.CollectFightsAsync(true, true);
Console.WriteLine(collectFights.Fights.Count);
return;


static long GetStreamPosition(StreamReader sr)
{
    var charPosField = typeof(StreamReader).GetField("_charPos", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)!;
    var charLenField = typeof(StreamReader).GetField("_charLen", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)!;
    var charBufferField = typeof(StreamReader).GetField("_charBuffer", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)!;
    
    var charBuffer = (char[])charBufferField.GetValue(sr)!;
    var charLen = (int)charLenField.GetValue(sr)!;
    var charPos = (int)charPosField.GetValue(sr)!;
        
    return sr.BaseStream.Position - sr.CurrentEncoding.GetByteCount(charBuffer, charPos, charLen - charPos);
}

public class LogParser(int id = 1) : IDisposable
{
    // ensure that everything runs in a single thread since V8 is single-threaded
    private readonly ConcurrentExclusiveSchedulerPair scheduler = new();
    private readonly V8ScriptEngine engine = new();
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
            engine.Execute(File.ReadAllText("/home/beerpsi/projects/fflogs-uploader-cli/url-search-params.js"));
            engine.Execute(File.ReadAllText("/home/beerpsi/projects/fflogs-uploader-cli/test.js"));
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
        if (parserReceiveMessage == null)
        {
            throw new InvalidOperationException("Parser has not been started");
        }

        var tcs = new TaskCompletionSource<T>();
        var serializedMessage = JsonConvert.SerializeObject(request, jsonSerializerSettings);

        string loggedSerializedMessage;
        if (request is ParseLinesRequest plRequest)
        {
            loggedSerializedMessage = JsonConvert.SerializeObject(
                new ParseLinesRequest { Id = plRequest.Id, Message = "parse-lines", Lines = [], SelectedRegion = plRequest.SelectedRegion,
                    RaidsToUpload = plRequest.RaidsToUpload, Scanning = plRequest.Scanning, LogFilePosition = plRequest.LogFilePosition },
                jsonSerializerSettings);
        }
        else
        {
            loggedSerializedMessage = JsonConvert.SerializeObject(request, jsonSerializerSettings);
        }
        
        //Plugin.Log.Debug("[LogParser] --> {0}", loggedSerializedMessage);

        await Task.Factory.StartNew(() => parserReceiveMessage.Invoke(
                           false,
                           serializedMessage,
                           (string callbackMessage) =>
                           {
                               //Console.WriteLine(callbackMessage);
                               //Plugin.Log.Debug("[LogParser] <-- {0}", callbackMessage);
                               
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
                                       //Plugin.Log.Warning("[LogParser] {0}", response.Data.ToObject<string>()!);
                                       break;
                                   case "event":
                                       //Plugin.Log.Information("[LogParser] [Event] {0}", callbackMessage);
                                       break;
                                   case "log-message":
                                       var logMessage = response.Data.ToObject<List<string>>();
                                       if (logMessage != null)
                                       {
                                           //Plugin.Log.Information("[LogParser] {0}", string.Join(" ", logMessage));
                                       }
                                       break;
                                   default:
                                       //Plugin.Log.Warning("[LogParser] [Unknown] {0}", callbackMessage);
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
