using System.Text.Json;

namespace ThreeCommerce.Admin.Services;

/// <summary>
/// Live LGTM-backend stats for Mission Control: Loki (logs), Tempo (traces) and Mimir (metrics),
/// read straight from each backend's HTTP API. Read-only and best-effort like BusStatsService —
/// every call is individually guarded, so one dead backend degrades its own tiles to down/0 and
/// never blanks the page. Short per-call timeouts (3s, on the named clients) keep a dead backend
/// from stalling the whole dashboard load.
/// </summary>
public sealed class ObservabilityStatsService(IHttpClientFactory factory)
{
    public sealed record ObsSnapshot(
        bool LokiUp, bool TempoUp, bool MimirUp,
        long LogLines1h, long ErrorLogs1h, int Traces1h, int ServicesReporting);

    // Loki 3 promotes `service_name` (from the OTLP resource) to a stream label, so instant
    // vector queries can aggregate across services. `detected_level` (Loki's log-level detection)
    // is structured metadata, NOT a stream label — it must be a pipe filter, not a selector matcher.
    private const string LokiTotalQuery = """sum(count_over_time({service_name=~".+"}[1h]))""";
    private const string LokiErrorQuery = """sum(count_over_time({service_name=~".+"} | detected_level=~"error|fatal|critical" [1h]))""";

    // OTel `http.server.request.duration` (seconds histogram) lands in Mimir as
    // `http_server_request_duration_seconds_*` via the collector's prometheusremotewrite exporter,
    // whose resource_to_telemetry_conversion copies `service.name` onto every datapoint as a
    // `service_name` label — one series-group per reporting service.
    private const string MimirServicesQuery = "count(count by (service_name) (http_server_request_duration_seconds_count))";

    public async Task<ObsSnapshot> GetSnapshotAsync(CancellationToken ct = default)
    {
        // All calls are independent — run them concurrently so worst case is one timeout, not seven.
        var lokiUp = ReadyAsync("loki", ct);
        var tempoUp = ReadyAsync("tempo", ct);
        var mimirUp = ReadyAsync("mimir", ct);
        var logLines = InstantVectorAsync("loki", "/loki/api/v1/query", LokiTotalQuery, ct);
        var errorLogs = InstantVectorAsync("loki", "/loki/api/v1/query", LokiErrorQuery, ct);
        var traces = TempoTraceCountAsync(ct);
        var services = InstantVectorAsync("mimir", "/prometheus/api/v1/query", MimirServicesQuery, ct);
        await Task.WhenAll(lokiUp, tempoUp, mimirUp, logLines, errorLogs, traces, services);

        return new ObsSnapshot(
            LokiUp: await lokiUp,
            TempoUp: await tempoUp,
            MimirUp: await mimirUp,
            LogLines1h: (long)await logLines,
            ErrorLogs1h: (long)await errorLogs,
            Traces1h: await traces,
            ServicesReporting: (int)await services);
    }

    private async Task<bool> ReadyAsync(string clientName, CancellationToken ct)
    {
        try
        {
            using var response = await factory.CreateClient(clientName).GetAsync("/ready", ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (IsDegradable(ex))
        {
            return false;
        }
    }

    /// <summary>
    /// Runs a Prometheus-style instant query (Loki and Mimir share the response shape:
    /// data.result[0].value = [ts, "number"]) and returns the first sample, or 0 on any failure.
    /// </summary>
    private async Task<double> InstantVectorAsync(string clientName, string path, string query, CancellationToken ct)
    {
        try
        {
            var client = factory.CreateClient(clientName);
            using var response = await client.GetAsync($"{path}?query={Uri.EscapeDataString(query)}", ct);
            if (!response.IsSuccessStatusCode)
            {
                return 0;
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (!doc.RootElement.TryGetProperty("data", out var data)
                || !data.TryGetProperty("result", out var result)
                || result.ValueKind != JsonValueKind.Array)
            {
                return 0;
            }

            foreach (var sample in result.EnumerateArray())
            {
                if (sample.TryGetProperty("value", out var value)
                    && value.ValueKind == JsonValueKind.Array
                    && value.GetArrayLength() == 2
                    && double.TryParse(value[1].GetString(), out var parsed))
                {
                    return parsed;
                }
            }

            return 0; // empty vector — no matching series (yet)
        }
        catch (Exception ex) when (IsDegradable(ex))
        {
            return 0;
        }
    }

    private async Task<int> TempoTraceCountAsync(CancellationToken ct)
    {
        try
        {
            var end = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var start = end - 3600;
            var client = factory.CreateClient("tempo");
            using var response = await client.GetAsync($"/api/search?limit=50&start={start}&end={end}", ct);
            if (!response.IsSuccessStatusCode)
            {
                return 0;
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            return doc.RootElement.TryGetProperty("traces", out var traces) && traces.ValueKind == JsonValueKind.Array
                ? traces.GetArrayLength()
                : 0;
        }
        catch (Exception ex) when (IsDegradable(ex))
        {
            return 0;
        }
    }

    private static bool IsDegradable(Exception ex) =>
        ex is HttpRequestException or TaskCanceledException or JsonException or UriFormatException;
}
