using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Common.Storage;

internal sealed class ReadOnlyStorageHttpHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    private readonly ConcurrentDictionary<int,int> statuses=new();
    public IReadOnlyDictionary<int,int> Statuses=>statuses;
    public double? RetryAfterSeconds {get;private set;}
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
    {
        var uri=request.RequestUri!;
        bool token=request.Method==HttpMethod.Post && uri.Host=="login.microsoftonline.com" && uri.AbsolutePath.EndsWith("/oauth2/v2.0/token",StringComparison.Ordinal);
        if(uri.Scheme!="https" || !(request.Method==HttpMethod.Get && uri.Host=="graph.microsoft.com" || token))
            throw new InvalidOperationException("The read-only storage probe refused a non-read operation.");
        var response=await base.SendAsync(request,ct);
        statuses.AddOrUpdate((int)response.StatusCode,1,(_,n)=>n+1);
        if(response.Headers.RetryAfter is { } retry)
            RetryAfterSeconds=retry.Delta?.TotalSeconds ?? (retry.Date-DateTimeOffset.UtcNow)?.TotalSeconds;
        return response;
    }
}

internal static class DashboardStorageDiagnostics
{
    internal sealed record Failure(string Operation,string ErrorType,string? StorageCode,string? GraphCode);
    internal sealed record Probe(int Examined,int Missing,int Readable,int LegacyJsonInvalid,int Concurrency,
        double ElapsedMs,IReadOnlyList<Failure> Failures,bool StoppedAfterFailure);
    private static readonly string[] GraphCodes=["activityLimitReached","tooManyRequests","throttledRequest","accessDenied","itemNotFound","invalidAuthenticationToken","serviceNotAvailable","generalException","resourceNotFound"];
    internal static Failure Classify(Exception error,string operation)
    {
        string? code=error is StorageException storage && Regex.IsMatch(storage.Code,"^STORAGE-[A-Z0-9-]{1,90}$") ? storage.Code : null;
        // Only fixed provider error identifiers may leave the process. Never return messages, keys, IDs or bodies.
        string? graph=GraphCodes.FirstOrDefault(code=>Regex.IsMatch(error.Message,"\\\"code\\\"\\s*:\\s*\\\""+Regex.Escape(code)+"\\\"",RegexOptions.IgnoreCase));
        return new(operation,error.GetType().Name,code,graph);
    }
    internal static async Task<Probe> ProbeAsync(IFileStorage storage,IReadOnlyList<Guid> ids,int concurrency,CancellationToken ct)
    {
        concurrency=Math.Clamp(concurrency,1,32);
        int examined=0,missing=0,readable=0,invalid=0;
        var failures=new ConcurrentBag<Failure>();var watch=Stopwatch.StartNew();
        using var stop=CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            await Parallel.ForEachAsync(ids,new ParallelOptions {MaxDegreeOfParallelism=concurrency,CancellationToken=stop.Token},async(id,token)=>
            {
                string operation="metadata";
                try
                {
                    Interlocked.Increment(ref examined);
                    string key=$"requestportal/locations/{id:N}/resolved-location.json";
                    var metadata=await storage.GetMetadataAsync(key,token);
                    if(metadata is null || metadata.Status!=StorageStatus.Stored) {Interlocked.Increment(ref missing);return;}
                    operation="read";
                    await using var stream=await storage.OpenReadAsync(key,token);
                    using var document=await JsonDocument.ParseAsync(stream,cancellationToken:token);
                    Interlocked.Increment(ref readable);
                }
                catch(OperationCanceledException) when(stop.IsCancellationRequested) { }
                catch(JsonException) {Interlocked.Increment(ref invalid);}
                catch(Exception error) {failures.Add(Classify(error,operation));await stop.CancelAsync();}
            });
        }
        catch(OperationCanceledException) when(!ct.IsCancellationRequested && !failures.IsEmpty) { }
        ct.ThrowIfCancellationRequested();
        return new(examined,missing,readable,invalid,concurrency,watch.Elapsed.TotalMilliseconds,
            failures.Distinct().OrderBy(x=>x.StorageCode).ToArray(),!failures.IsEmpty);
    }
}
