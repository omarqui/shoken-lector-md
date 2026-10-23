using System.Net.Http;
using Azure.Core;
using Azure.Identity;
using LectorMd.Core.Settings;
using LectorMd.Core.Usage;

namespace LectorMd.App.Services;

/// <summary>
/// Inicio de sesión en Azure para leer el consumo real del recurso de Speech. La primera vez abre el navegador;
/// después usa la caché de tokens cifrada de Windows y solo recuerda qué cuenta se usó (sin secretos).
/// </summary>
internal sealed class AzureAccount(AppPaths paths)
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    private InteractiveBrowserCredential? _credential;
    private AuthenticationRecord? _record;

    public AzureUsageClient CreateClient() => new(Http, GetTokenAsync);

    private async ValueTask<string> GetTokenAsync(string? tenantId, CancellationToken cancellationToken)
    {
        InteractiveBrowserCredential credential = await GetCredentialAsync(cancellationToken);
        var context = new TokenRequestContext([AzureUsageClient.Scope], tenantId: string.IsNullOrWhiteSpace(tenantId) ? null : tenantId);
        if (_record is null)
        {
            _record = await credential.AuthenticateAsync(context, cancellationToken);
            await SaveRecordAsync(_record, cancellationToken);
        }

        AccessToken token = await credential.GetTokenAsync(context, cancellationToken);
        return token.Token;
    }

    private async ValueTask<InteractiveBrowserCredential> GetCredentialAsync(CancellationToken cancellationToken)
    {
        if (_credential is not null)
        {
            return _credential;
        }

        _record = await LoadRecordAsync(cancellationToken);
        var options = new InteractiveBrowserCredentialOptions
        {
            TokenCachePersistenceOptions = new TokenCachePersistenceOptions { Name = "LectorMd" },
            AuthenticationRecord = _record,
            TenantId = _record?.TenantId,
        };
        options.AdditionallyAllowedTenants.Add("*"); // el recurso puede estar en otro directorio de la misma cuenta
        _credential = new InteractiveBrowserCredential(options);
        return _credential;
    }

    private async Task<AuthenticationRecord?> LoadRecordAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(paths.AzureAccountFile))
            {
                return null;
            }

            await using FileStream stream = File.OpenRead(paths.AzureAccountFile);
            return await AuthenticationRecord.DeserializeAsync(stream, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return null; // se vuelve a iniciar sesión
        }
    }

    private async Task SaveRecordAsync(AuthenticationRecord record, CancellationToken cancellationToken)
    {
        try
        {
            paths.EnsureCreated();
            await using FileStream stream = File.Create(paths.AzureAccountFile);
            await record.SerializeAsync(stream, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorLog.Write(ex);
        }
    }
}
