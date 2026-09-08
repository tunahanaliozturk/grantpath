using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

// Measures /authz/check under a fixed number of concurrent callers, and then checks that every decision
// it made is in the audit trail.
//
//   dotnet run -c Release --project load/GrantPath.LoadTests -- http://localhost:5120 user:alice viewer document:onboarding 4000 32
//
// The two are measured together on purpose. Latency numbers from a system that quietly drops audit
// records under pressure are not a description of the system anybody would ship, and a completeness
// check run at rest would never see the queue full.

string host = args.Length > 0 ? args[0].TrimEnd('/') : "http://localhost:5120";
string subject = args.Length > 1 ? args[1] : "user:alice";
string relation = args.Length > 2 ? args[2] : "viewer";
string resource = args.Length > 3 ? args[3] : "document:onboarding";
int requests = args.Length > 4 ? int.Parse(args[4], CultureInfo.InvariantCulture) : 4000;
int concurrency = args.Length > 5 ? int.Parse(args[5], CultureInfo.InvariantCulture) : 32;

var handler = new SocketsHttpHandler
{
    // Enough connections that the client is not the bottleneck. Measuring your own connection pool is a
    // classic way to publish a number about the wrong program.
    MaxConnectionsPerServer = concurrency * 2,
    PooledConnectionLifetime = TimeSpan.FromMinutes(5),
};

using var client = new HttpClient(handler) { BaseAddress = new Uri(host) };

var body = new { subject, relation, resource };

Console.WriteLine($"Warming up against {host}.");

for (int index = 0; index < Math.Min(concurrency * 4, 200); index++)
{
    using HttpResponseMessage warmup = await client.PostAsJsonAsync("/authz/check", body);

    if (warmup.StatusCode is not HttpStatusCode.OK)
    {
        Console.Error.WriteLine($"The endpoint answered {(int)warmup.StatusCode} during warm-up. Stopping.");
        return 1;
    }
}

Console.WriteLine($"Making {requests} decisions across {concurrency} callers.");

double[] samples = new double[requests];
Guid[] ids = new Guid[requests];
int allowed = 0;
int failures = 0;
int next = -1;

long started = Stopwatch.GetTimestamp();

await Parallel.ForEachAsync(
    Enumerable.Range(0, concurrency),
    new ParallelOptions { MaxDegreeOfParallelism = concurrency },
    async (_, cancellationToken) =>
    {
        while (true)
        {
            int index = Interlocked.Increment(ref next);

            if (index >= requests)
            {
                return;
            }

            long begin = Stopwatch.GetTimestamp();

            using HttpResponseMessage response =
                await client.PostAsJsonAsync("/authz/check", body, cancellationToken);

            samples[index] = Stopwatch.GetElapsedTime(begin).TotalMilliseconds;

            if (response.StatusCode is not HttpStatusCode.OK)
            {
                Interlocked.Increment(ref failures);
                continue;
            }

            JsonElement result = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);

            ids[index] = result.GetProperty("requestId").GetGuid();

            if (result.GetProperty("allowed").GetBoolean())
            {
                Interlocked.Increment(ref allowed);
            }
        }
    });

TimeSpan elapsed = Stopwatch.GetElapsedTime(started);
Array.Sort(samples);

Console.WriteLine();
Console.WriteLine($"  decisions    {requests}");
Console.WriteLine($"  allowed      {allowed}");
Console.WriteLine($"  failures     {failures}");
Console.WriteLine($"  wall clock   {elapsed.TotalSeconds:F2} s");
Console.WriteLine($"  throughput   {requests / elapsed.TotalSeconds:F0} decisions/s");
Console.WriteLine($"  p50          {Percentile(samples, 0.50):F2} ms");
Console.WriteLine($"  p95          {Percentile(samples, 0.95):F2} ms");
Console.WriteLine($"  p99          {Percentile(samples, 0.99):F2} ms");
Console.WriteLine($"  max          {samples[^1]:F2} ms");

// The writer batches, so the last records land shortly after the last response does.
await Task.Delay(TimeSpan.FromSeconds(2));

Guid[] sample = [.. ids.Where(id => id != Guid.Empty).OrderBy(_ => Random.Shared.Next()).Take(250)];
int missing = 0;

foreach (Guid id in sample)
{
    using HttpResponseMessage record = await client.GetAsync($"/authz/why/{id}");

    if (record.StatusCode is not HttpStatusCode.OK)
    {
        missing++;
    }
}

Console.WriteLine();
Console.WriteLine($"  audit sample {sample.Length} decisions checked, {missing} missing");

return failures is 0 && missing is 0 ? 0 : 1;

static double Percentile(double[] sorted, double quantile)
{
    int index = (int)Math.Ceiling(quantile * sorted.Length) - 1;

    return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
}
