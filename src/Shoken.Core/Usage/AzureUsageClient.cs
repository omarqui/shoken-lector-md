using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Shoken.Core.Usage;

/// <summary>Un recurso de Azure que puede sintetizar voz (Speech, o los multiservicio que también lo hacen).</summary>
public sealed record SpeechResource(string Id, string Name, string Location, string Kind, string Sku, string TenantId)
{
    public bool IsInRegion(string region) =>
        string.Equals(Location.Replace(" ", "", StringComparison.Ordinal),
            region.Replace(" ", "", StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Consulta en Azure Resource Manager el consumo real del mes (métrica "SynthesizedCharacters" de Azure Monitor).
/// La clave de Speech no sirve para esto: hace falta un token de una cuenta con acceso de lectura al recurso.
/// </summary>
public sealed class AzureUsageClient(HttpClient http, Func<string?, CancellationToken, ValueTask<string>> getToken)
{
    public const string Scope = "https://management.azure.com/.default";
    private const string Arm = "https://management.azure.com";
    private const string MetricName = "SynthesizedCharacters";

    private static readonly string[] SpeechKinds = ["SpeechServices", "CognitiveServices", "AIServices"];

    /// <summary>
    /// Busca los recursos que pueden sintetizar voz en las suscripciones de la cuenta: en el directorio indicado,
    /// o en todos los directorios a los que tiene acceso.
    /// </summary>
    public async Task<IReadOnlyList<SpeechResource>> FindSpeechResourcesAsync(string? tenantId, CancellationToken cancellationToken)
    {
        var found = new List<SpeechResource>();
        var tenants = new List<string>();
        if (!string.IsNullOrWhiteSpace(tenantId))
        {
            tenants.Add(tenantId.Trim());
        }
        else
        {
            await foreach (JsonElement tenant in GetPagedAsync(null, $"{Arm}/tenants?api-version=2022-12-01", cancellationToken))
            {
                if (tenant.TryGetProperty("tenantId", out JsonElement id) && id.GetString() is { Length: > 0 } value)
                {
                    tenants.Add(value);
                }
            }
        }

        foreach (string tenant in tenants)
        {
            var subscriptions = new List<string>();
            try
            {
                await foreach (JsonElement subscription in GetPagedAsync(tenant, $"{Arm}/subscriptions?api-version=2022-12-01", cancellationToken))
                {
                    if (subscription.TryGetProperty("subscriptionId", out JsonElement id) && id.GetString() is { Length: > 0 } value)
                    {
                        subscriptions.Add(value);
                    }
                }
            }
            catch (HttpRequestException)
            {
                continue; // un directorio sin acceso (o que exige MFA aparte) no impide revisar los demás
            }

            foreach (string subscription in subscriptions)
            {
                string url = $"{Arm}/subscriptions/{subscription}/providers/Microsoft.CognitiveServices/accounts?api-version=2024-10-01";
                var page = new List<JsonElement>();
                try
                {
                    await foreach (JsonElement account in GetPagedAsync(tenant, url, cancellationToken))
                    {
                        page.Add(account);
                    }
                }
                catch (HttpRequestException)
                {
                    continue;
                }

                found.AddRange(ToSpeechResources(page, tenant));
            }
        }

        return found;
    }

    /// <summary>Caracteres sintetizados en el mes en curso (UTC) según Azure Monitor.</summary>
    public async Task<long> GetMonthCharactersAsync(string resourceId, string? tenantId, CancellationToken cancellationToken)
    {
        string json = await GetStringAsync(tenantId, BuildMetricsUrl(resourceId, DateTime.UtcNow), cancellationToken);
        return ParseMetricTotal(json);
    }

    public static string BuildMetricsUrl(string resourceId, DateTime utcNow)
    {
        var start = new DateTime(utcNow.Year, utcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        string timespan = $"{Iso(start)}/{Iso(utcNow)}";
        return $"{Arm}/{resourceId.Trim('/')}/providers/Microsoft.Insights/metrics" +
               $"?api-version=2023-10-01&metricnames={MetricName}&aggregation=Total&interval=P1D&timespan={timespan}";

        static string Iso(DateTime value) => value.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
    }

    public static long ParseMetricTotal(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        double total = 0;
        foreach (JsonElement metric in Array(document.RootElement, "value"))
        {
            foreach (JsonElement series in Array(metric, "timeseries"))
            {
                foreach (JsonElement point in Array(series, "data"))
                {
                    if (point.TryGetProperty("total", out JsonElement value) && value.ValueKind == JsonValueKind.Number)
                    {
                        total += value.GetDouble();
                    }
                }
            }
        }

        return (long)Math.Round(total);
    }

    public static IReadOnlyList<SpeechResource> ParseSpeechAccounts(string json, string tenantId)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return ToSpeechResources(Array(document.RootElement, "value"), tenantId);
    }

    private static List<SpeechResource> ToSpeechResources(IEnumerable<JsonElement> accounts, string tenantId) =>
        accounts
            .Select(a => new SpeechResource(
                Text(a, "id"), Text(a, "name"), Text(a, "location"), Text(a, "kind"),
                a.TryGetProperty("sku", out JsonElement sku) ? Text(sku, "name") : "", tenantId))
            .Where(r => r.Id.Length > 0 && SpeechKinds.Contains(r.Kind, StringComparer.OrdinalIgnoreCase))
            .ToList();

    private async IAsyncEnumerable<JsonElement> GetPagedAsync(string? tenantId, string url,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? next = url;
        while (next is not null)
        {
            using JsonDocument document = JsonDocument.Parse(await GetStringAsync(tenantId, next, cancellationToken));
            foreach (JsonElement item in Array(document.RootElement, "value"))
            {
                yield return item.Clone();
            }

            next = document.RootElement.TryGetProperty("nextLink", out JsonElement link) ? link.GetString() : null;
        }
    }

    private async Task<string> GetStringAsync(string? tenantId, string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await getToken(tenantId, cancellationToken));
        using HttpResponseMessage response = await http.SendAsync(request, cancellationToken);
        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Azure respondió {(int)response.StatusCode}: {ErrorMessage(body)}", null, response.StatusCode);
        }

        return body;
    }

    private static string ErrorMessage(string body)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("error", out JsonElement error) && error.ValueKind == JsonValueKind.Object)
            {
                return Text(error, "message") is { Length: > 0 } message ? message : Text(error, "code");
            }
        }
        catch (JsonException)
        {
        }

        return body.Length > 200 ? body[..200] : body;
    }

    private static IEnumerable<JsonElement> Array(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out JsonElement value)
        && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray()
            : [];

    private static string Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";
}
