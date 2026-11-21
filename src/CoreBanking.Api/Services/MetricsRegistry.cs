using System.Collections.Concurrent;

namespace CoreBanking.Api.Services;

/// <summary>
/// Lightweight process-local observability counters. No external exporter is
/// required; the admin endpoints expose both JSON and Prometheus text so a
/// scraper can be pointed at the service without adding a dependency.
/// </summary>
public sealed class MetricsRegistry
{
    private readonly ConcurrentDictionary<string, long> _counters = new();
    private readonly ConcurrentDictionary<string, double> _gauges = new();
    private long _requests;
    private long _errors;
    private double _lastRequestMs;

    public void RecordRequest(string method, string path, int status, double elapsedMs)
    {
        Interlocked.Increment(ref _requests);
        if (status >= 500) Interlocked.Increment(ref _errors);
        _lastRequestMs = elapsedMs;
        Add($"requests_total{{method=\"{method}\",path=\"{path}\",status=\"{status}\"}}", 1);
        Add("request_duration_ms", elapsedMs, gauge: true);
    }

    public void Increment(string name, long by = 1) => Add(name, by);

    private void Add(string name, double by, bool gauge = false)
    {
        if (gauge) { _gauges[name] = by; return; }
        _counters.AddOrUpdate(name, (long)by, (_, v) => v + (long)by);
    }

    public object Snapshot()
    {
        var uptime = DateTime.UtcNow - ProcessStartTime;
        return new
        {
            service = "core-banking-api",
            version = "1.0.0",
            uptimeSeconds = (long)uptime.TotalSeconds,
            requests = Interlocked.Read(ref _requests),
            errors5xx = Interlocked.Read(ref _errors),
            lastRequestMs = Math.Round(_lastRequestMs, 2),
            counters = _counters.OrderBy(c => c.Key)
                .ToDictionary(c => c.Key, c => c.Value),
            gauges = _gauges.OrderBy(g => g.Key)
                .ToDictionary(g => g.Key, g => Math.Round(g.Value, 2))
        };
    }

    public string Prometheus()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("# HELP corebanking_requests_total Total HTTP requests.");
        sb.AppendLine("# TYPE corebanking_requests_total counter");
        sb.AppendLine($"corebanking_requests_total {Interlocked.Read(ref _requests)}");
        sb.AppendLine("# HELP corebanking_errors_total Total 5xx responses.");
        sb.AppendLine("# TYPE corebanking_errors_total counter");
        sb.AppendLine($"corebanking_errors_total {Interlocked.Read(ref _errors)}");
        sb.AppendLine("# HELP corebanking_last_request_ms Duration of the last request.");
        sb.AppendLine("# TYPE corebanking_last_request_ms gauge");
        sb.AppendLine($"corebanking_last_request_ms {_lastRequestMs:F2}");
        sb.AppendLine("# HELP corebanking_custom_total Custom business counters.");
        sb.AppendLine("# TYPE corebanking_custom_total counter");
        foreach (var c in _counters.OrderBy(c => c.Key))
            sb.AppendLine($"corebanking_custom_total{{{Sanitize(c.Key)}}} {c.Value}");
        return sb.ToString();
    }

    private static string Sanitize(string key)
    {
        var i = key.IndexOf('{');
        return i < 0 ? $"name=\"{key}\"" : key[i..];
    }

    public static readonly DateTime ProcessStartTime = DateTime.UtcNow;
}
