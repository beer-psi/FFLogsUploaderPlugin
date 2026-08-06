using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HtmlAgilityPack;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace FFLogsUploaderPlugin.FFLogs;

public class DesktopClient : IDisposable
{
    private const string BaseUrl = "https://www.fflogs.com";
    private const string ArchonAppLiteVersion = "9.5.0";
    private const string UserAgent =
        $"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) ArchonAppLite/{ArchonAppLiteVersion} Chrome/138.0.7204.251 Electron/37.9.0 Safari/537.36";
    
    private readonly JsonSerializerSettings jsonSerializerSettings = new()
    {
        ContractResolver = new CamelCasePropertyNamesContractResolver()
    };
    private readonly CookieContainer cookies = new();
    private readonly HttpClient httpClient;

    public DesktopClient()
    {
        httpClient = new HttpClient(new HttpClientHandler
                                    {
                                        CookieContainer = cookies,
                                        UseCookies = true,
                                        AutomaticDecompression = DecompressionMethods.All
                                    })
        {
            BaseAddress = new Uri(BaseUrl),
            DefaultRequestHeaders =
            {
                { "user-agent", UserAgent },
                { "accept", "*/*" },
                { "accept-language", "en-US" },
                { "sec-ch-ua", "\"Not)A;Brand\";v=\"8\", \"Chromium\";v=\"138\"" },
                { "sec-ch-ua-mobile", "?0" },
                { "sec-ch-ua-platform", "\"Windows\"" },
                { "sec-fetch-site", "cross-site" },
                { "sec-fetch-mode", "cors" },
                { "sec-fetch-dest", "empty" },
                { "sec-fetch-storage-access", "active" }
            },
        };
    }

    public async Task<LoginResponse> LoginAsync(string email, string password)
    {
        using var content = new StringContent(
            JsonConvert.SerializeObject(
                new LoginRequest
                {
                    Email = email,
                    Password = password,
                    Version = ArchonAppLiteVersion,
                    ClientTime = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")
                },
                jsonSerializerSettings),
            Encoding.UTF8, "application/json");
        using var resp = await httpClient.PostAsync("/desktop-client/log-in", content);

        return await HandleResponse<LoginResponse>(resp);
    }

    public async Task LogoutAsync()
    {
        using var content = new StringContent("{}", Encoding.UTF8, "application/json");
        using var resp = await httpClient.PostAsync("/desktop-client/log-out", content);

        resp.EnsureSuccessStatusCode();
    }

    public async Task RefreshTokenV2Async()
    {
        using var resp = await httpClient.PostAsync("/desktop-client/token/v2", null);

        resp.EnsureSuccessStatusCode();
    }

    public async Task<CreateReportResponse> CreateReportAsync(
        long parserVersion, long startTime, long endTime, long? guildId, string fileName, long region, long visibility,
        string description, CancellationToken token = default)
    {
        using var content = new StringContent(
            JsonConvert.SerializeObject(
                new CreateReportRequest
                {
                    ClientVersion = ArchonAppLiteVersion,
                    ParserVersion = parserVersion,
                    StartTime = startTime,
                    EndTime = endTime,
                    GuildId = guildId,
                    FileName = fileName,
                    ServerOrRegion = region,
                    Visibility = visibility,
                    ReportTagId = null,
                    Description = description,
                },
                jsonSerializerSettings),
            Encoding.UTF8, "application/json");
        using var resp = await httpClient.PostAsync("/desktop-client/create-report", content, token);

        return await HandleResponse<CreateReportResponse>(resp);
    }

    public async Task SetReportMasterTable(string reportCode, long segmentId, bool isRealTime, byte[] logfile)
    {
        var boundary = GenerateWebKitBoundary();
        using var content = new MultipartFormDataContent(boundary);

        content.Headers.Remove("Content-Type");
        content.Headers.TryAddWithoutValidation("Content-Type", $"multipart/form-data; boundary={boundary}");
        content.Add(CreateStringPart("segmentId", segmentId.ToString()));
        content.Add(CreateStringPart("isRealTime", isRealTime.ToString().ToLowerInvariant()));
        content.Add(CreateFilePart("logfile", "blob", "application/zip", logfile));

        using var resp = await httpClient.PostAsync($"/desktop-client/set-report-master-table/{reportCode}", content);

        resp.EnsureSuccessStatusCode();
    }

    public async Task<AddReportSegmentResponse> AddReportSegment(
        string reportCode, byte[] logfile, long startTime, long endTime, long mythic, bool isLiveLog, bool isRealTime,
        long inProgressEventCount, long segmentId)
    {
        var boundary = GenerateWebKitBoundary();
        using var content = new MultipartFormDataContent(boundary);

        content.Headers.Remove("Content-Type");
        content.Headers.TryAddWithoutValidation("Content-Type", $"multipart/form-data; boundary={boundary}");
        content.Add(CreateFilePart("logfile", "blob", "application/zip", logfile));
        content.Add(CreateStringPart("parameters", JsonConvert.SerializeObject(new AddReportSegmentRequest
        {
            StartTime = startTime,
            EndTime = endTime,
            Mythic = mythic,
            IsLiveLog = isLiveLog,
            IsRealTime = isRealTime,
            InProgressEventCount = inProgressEventCount,
            SegmentId = segmentId
        }, jsonSerializerSettings)));

        using var resp = await httpClient.PostAsync($"/desktop-client/add-report-segment/{reportCode}", content);

        return await HandleResponse<AddReportSegmentResponse>(resp);
    }

    public async Task TerminateReport(string reportCode)
    {
        var resp = await httpClient.PostAsync($"/desktop-client/terminate-report/{reportCode}", null);

        resp.EnsureSuccessStatusCode();
    }

    public async Task<string> DownloadParserScript(
        int id, bool gameContentDetectionEnabled, bool metersEnabled, bool liveFightDataEnabled)
    {
        // ReSharper disable once UseStringInterpolation
        var uri = string.Format(
            "{0}/desktop-client/parser?id={1}&ts={2}&gameContentDetectionEnabled={3}&metersEnabled={4}&liveFightDataEnabled={5}",
            BaseUrl,
            id,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            gameContentDetectionEnabled.ToString().ToLowerInvariant(),
            metersEnabled.ToString().ToLowerInvariant(),
            liveFightDataEnabled.ToString().ToLowerInvariant()
        );
        using var requestMessage = new HttpRequestMessage(HttpMethod.Get, uri);
        requestMessage.Headers.Add("upgrade-insecure-requests", "1");
        requestMessage.Headers.Add("accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8,application/signed-exchange;v=b3;q=0.7");
        requestMessage.Headers.Add("sec-fetch-mode", "navigate");
        requestMessage.Headers.Add("sec-fetch-dest", "iframe");
        
        var resp = await httpClient.SendAsync(requestMessage);

        resp.EnsureSuccessStatusCode();
        
        var doc = new HtmlDocument();
        
        doc.LoadHtml(await resp.Content.ReadAsStringAsync());

        var mergedScript = new StringBuilder();

        foreach (var node in doc.DocumentNode.SelectNodes("//script"))
        {
            var src = node.GetAttributeValue("src", string.Empty);

            if (src.Contains("parser-ff"))
            {
                using var requestMessage2 = new HttpRequestMessage(HttpMethod.Get, src);
                requestMessage2.Headers.Add("sec-fetch-mode", "no-cors");
                requestMessage2.Headers.Add("sec-fetch-dest", "script");

                var resp2 = await httpClient.SendAsync(requestMessage2);

                resp2.EnsureSuccessStatusCode();
                mergedScript.AppendLine(await resp2.Content.ReadAsStringAsync());
            } 
            else if (node.InnerHtml.Contains("window.gameContentTypes")
                       || node.InnerHtml.Contains("ipcCollectFights"))
            {
                mergedScript.AppendLine(node.InnerHtml);
            }
        }

        return mergedScript.ToString();
    }

    public void Dispose()
    {
        httpClient.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task<T> HandleResponse<T>(HttpResponseMessage resp)
    {
        var content = await resp.Content.ReadAsStringAsync();
        
        try
        {
            if (resp.IsSuccessStatusCode)
            {
                return JsonConvert.DeserializeObject<T>(content)!;
            }
        
            var error = JsonConvert.DeserializeObject<ErrorMessage>(content);

            throw new DesktopClientException(error?.Message ?? "Unknown error.");
        }
        catch (JsonReaderException e)
        {
            Plugin.Log.Error(e, "Failed to parse response as JSON: {0}", content);
            throw;
        }
    }

    private static string GenerateWebKitBoundary()
    {
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
        var random = new Random();
        var boundary = new StringBuilder(38);

        boundary.Append("----WebKitFormBoundary");

        for (var i = 0; i < 16; i++)
        {
            boundary.Append(chars[random.Next(chars.Length)]);
        }

        return boundary.ToString();
    }
    
    private static StringContent CreateStringPart(string name, string value)
    {
        var content = new StringContent(value);
        
        content.Headers.ContentType = null;
        content.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data")
        {
            Name = $"\"{name}\""
        };
        
        return content;
    }
    
    private static ByteArrayContent CreateFilePart(string name, string filename, string contentType, byte[] data)
    {
        var content = new ByteArrayContent(data);
        
        content.Headers.Clear();
        content.Headers.TryAddWithoutValidation("Content-Disposition", $"form-data; name=\"{name}\"; filename=\"{filename}\"");
        content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        
        return content;
    }

    public class DesktopClientException(string message) : Exception(message);

    private class LoginRequest
    {
        public required string Email { get; set; }
        public required string Password { get; set; }
        public required string Version { get; set; }
        public required string ClientTime { get; set; }
    }

    private class ErrorMessage
    {
        public required string Message { get; set; }
    }

    public class User
    {
        public required ulong Id { get; set; }
        public required string UserName { get; set; }
        public required string? EmailAddress { get; set; } = null;
        public required bool IsAdmin { get; set; }
        public required List<JToken> Guilds { get; set; }
        public required List<JToken> Characters { get; set; }
        public required string Thumbnail { get; set; }
    }

    public class SelectItem
    {
        public required string Label { get; set; }
        public required long Value { get; set; }
    }

    public class GuildSelectLogo
    {
        public required string Url { get; set; }
        public required bool IsCustom { get; set; }
        public required string FallbackUrl { get; set; }
    }

    public class GuildSelectItem : SelectItem
    {
        public required GuildSelectLogo Logo { get; set; }
        public required string CssClassName { get; set; }
        public required long? RegionId { get; set; } = null;
    }

    public class EnabledFeatures
    {
        public bool NoAds { get; set; }
        public bool RealTimeLiveLogging { get; set; }
        public bool Meters { get; set; }
        public bool LiveFightData { get; set; }
        public bool TooltipAddon { get; set; }
        public bool TooltipAddonTierTwoData { get; set; }
        public bool AutoLog { get; set; }
        public bool MetersLiveParse { get; set; }
        public bool MetersRaceTheGhost { get; set; }
        public bool Video { get; set; }
        public bool CloudVideo { get; set; }
    }

    public class LoginResponse
    {
        public required User User { get; set; }
        public required EnabledFeatures EnabledFeatures { get; set; }
        public required List<GuildSelectItem> GuildSelectItems { get; set; }
        public required List<SelectItem> ReportVisibilitySelectItems { get; set; }
        public required List<SelectItem>? ReportTagSelectItems { get; set; } = null;
        public required List<SelectItem> RegionOrServerSelectItems { get; set; }
        // contentTypes, lastCharacterImport, characterImportUrl, isOnTooltipAddonWaitingList
    }
    
    private class CreateReportRequest
    {
        public required string ClientVersion { get; set; }
        public required long ParserVersion { get; set; }
        public required long StartTime { get; set; }
        public required long EndTime { get; set; }
        public required long? GuildId { get; set; }
        public required string FileName { get; set; }
        public required long ServerOrRegion { get; set; }
        public required long Visibility { get; set; }
        public required long? ReportTagId { get; set; } = null;
        public required string Description { get; set; }
    }

    public class CreateReportResponse
    {
        public required string Code { get; set; }
    }

    private class AddReportSegmentRequest
    {
        public required long StartTime { get; set; }
        public required long EndTime { get; set; }
        public required long Mythic { get; set; }
        public required bool IsLiveLog { get; set; }
        public required bool IsRealTime { get; set; }
        public required long InProgressEventCount { get; set; }
        public required long SegmentId { get; set; }
    }
    
    public class AddReportSegmentResponse
    {
        public required long NextSegmentId { get; set; }
    }
}

