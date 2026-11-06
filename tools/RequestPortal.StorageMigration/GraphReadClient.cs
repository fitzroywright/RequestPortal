using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Common.Storage;

namespace RequestPortal.StorageMigration;

public sealed class MigrationSourceException(string code) : IOException(code)
{
    public string Code { get; } = code;
}

/// <summary>Serial, paced source reads. The only POST is OAuth token acquisition; source writes are impossible.</summary>
public sealed class GraphReadClient(HttpClient http, SharePointStorageOptions options, string stateDirectory,
    Func<TimeSpan,CancellationToken,Task>? delay = null, TimeSpan? minimumInterval = null)
{
    private readonly SemaphoreSlim gate = new(1,1);
    private readonly Func<TimeSpan,CancellationToken,Task> pause = delay ?? Task.Delay;
    private readonly TimeSpan interval = minimumInterval ?? TimeSpan.FromSeconds(1);
    private DateTimeOffset lastRequest, tokenExpiry;
    private string? token;
    public int ThrottledResponses { get; private set; }
    public int Requests { get; private set; }
    private string ThrottleFile => Path.Combine(stateDirectory,"throttle.json");

    public async Task<JsonDocument> JsonAsync(string url,CancellationToken ct)
    {
        using var response = await SendAsync(url,ct);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonDocument.ParseAsync(stream,cancellationToken:ct);
    }
    public async Task<List<JsonElement>> PagesAsync(string url,CancellationToken ct)
    {
        var rows = new List<JsonElement>(); var seen = new HashSet<string>(StringComparer.Ordinal);
        string? next = url;
        while(next is not null)
        {
            if(!seen.Add(next))throw new MigrationSourceException("GRAPH_PAGINATION_CYCLE");
            using var page = await JsonAsync(next,ct);
            foreach(var item in page.RootElement.GetProperty("value").EnumerateArray()) rows.Add(item.Clone());
            next = page.RootElement.TryGetProperty("@odata.nextLink",out var link) ? link.GetString() : null;
        }
        return rows;
    }
    public async Task<HttpResponseMessage> SendAsync(string url,CancellationToken ct)
    {
        var uri = new Uri(url);
        if(uri.Scheme!="https" || uri.Host!="graph.microsoft.com" || uri.UserInfo.Length!=0)
            throw new MigrationSourceException("GRAPH_ENDPOINT_GUARD");
        await gate.WaitAsync(ct);
        try
        {
            for(int attempt=0;attempt<7;attempt++)
            {
                await TokenAsync(ct);
                await PaceAsync(ct);
                using var request=new HttpRequestMessage(HttpMethod.Get,uri);
                request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",token);
                var response=await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,ct);Requests++;
                if(response.StatusCode==HttpStatusCode.Unauthorized && attempt==0) {response.Dispose();token=null;continue;}
                if(response.IsSuccessStatusCode)return response;
                if(response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout)
                {
                    await BackoffAsync(response,attempt,ct);response.Dispose();continue;
                }
                int code=(int)response.StatusCode;response.Dispose();throw new MigrationSourceException("GRAPH_HTTP_"+code);
            }
            throw new MigrationSourceException("GRAPH_RETRY_DEFERRED");
        }
        finally {gate.Release();}
    }
    private async Task TokenAsync(CancellationToken ct)
    {
        if(token is not null && tokenExpiry>DateTimeOffset.UtcNow.AddMinutes(2))return;
        for(int attempt=0;attempt<7;attempt++)
        {
            await PaceAsync(ct);
            using var form=new FormUrlEncodedContent(new Dictionary<string,string> {
                ["client_id"]=options.ClientId,["client_secret"]=options.ClientSecret,
                ["scope"]="https://graph.microsoft.com/.default",["grant_type"]="client_credentials"});
            using var response=await http.PostAsync("https://login.microsoftonline.com/"+Uri.EscapeDataString(options.TenantId)+"/oauth2/v2.0/token",form,ct);
            Requests++;
            if(response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout)
            {await BackoffAsync(response,attempt,ct);continue;}
            if(!response.IsSuccessStatusCode)throw new MigrationSourceException("GRAPH_AUTH_HTTP_"+(int)response.StatusCode);
            using var json=JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            token=json.RootElement.GetProperty("access_token").GetString() ?? throw new MigrationSourceException("GRAPH_AUTH_TOKEN_MISSING");
            tokenExpiry=DateTimeOffset.UtcNow.AddSeconds(json.RootElement.GetProperty("expires_in").GetInt32());return;
        }
        throw new MigrationSourceException("GRAPH_AUTH_RETRY_DEFERRED");
    }
    private async Task PaceAsync(CancellationToken ct)
    {
        DateTimeOffset notBefore=lastRequest+interval;
        if(File.Exists(ThrottleFile))
        {
            var persisted=JsonSerializer.Deserialize<DateTimeOffset>(await File.ReadAllTextAsync(ThrottleFile,ct));
            if(persisted>notBefore)notBefore=persisted;
        }
        var wait=notBefore-DateTimeOffset.UtcNow;
        if(wait>TimeSpan.Zero)await pause(wait,ct);
        lastRequest=DateTimeOffset.UtcNow;
    }
    private async Task BackoffAsync(HttpResponseMessage response,int attempt,CancellationToken ct)
    {
        ThrottledResponses++;
        var retry=response.Headers.RetryAfter;
        var duration=retry?.Delta ?? (retry?.Date-DateTimeOffset.UtcNow) ?? TimeSpan.FromSeconds(Math.Min(300,Math.Pow(2,attempt+1))+Random.Shared.NextDouble());
        if(duration<TimeSpan.Zero)duration=TimeSpan.Zero;
        Directory.CreateDirectory(stateDirectory);
        string candidate=ThrottleFile+".writing";
        await File.WriteAllTextAsync(candidate,JsonSerializer.Serialize(DateTimeOffset.UtcNow+duration),ct);
        if(!OperatingSystem.IsWindows())File.SetUnixFileMode(candidate,UnixFileMode.UserRead|UnixFileMode.UserWrite);
        File.Move(candidate,ThrottleFile,true);
        // Persist before waiting so an interrupted/resumed migration honors provider Retry-After.
        await pause(duration,ct);
    }
}
