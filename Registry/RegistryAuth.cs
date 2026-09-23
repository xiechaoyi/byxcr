using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Byxcr.Configuration;

namespace Byxcr.Registry;

/// <summary>解析后的 WWW-Authenticate 挑战。</summary>
internal sealed record AuthChallenge(string Scheme, IReadOnlyDictionary<string, string> Parameters);

/// <summary>Registry 鉴权公共逻辑：解析 WWW-Authenticate 挑战，并按需换取匿名 / 具名 Bearer token。</summary>
internal static class RegistryAuth
{
    private static readonly TimeSpan TokenTimeout = TimeSpan.FromSeconds(30);

    /// <summary>解析 <c>WWW-Authenticate: Bearer realm="...",service="...",scope="..."</c>（支持引号内含逗号）。</summary>
    public static AuthChallenge? Parse(string? header)
    {
        if (string.IsNullOrWhiteSpace(header)) return null;

        var text = header.Trim();
        var space = text.IndexOf(' ');
        var scheme = (space >= 0 ? text[..space] : text).Trim();
        var payload = space >= 0 ? text[(space + 1)..] : string.Empty;

        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var index = 0;
        while (index < payload.Length)
        {
            var equals = payload.IndexOf('=', index);
            if (equals < 0) break;

            var key = payload[index..equals].Trim().TrimStart(',').Trim();
            var valueStart = equals + 1;

            string value;
            if (valueStart < payload.Length && payload[valueStart] == '"')
            {
                var end = payload.IndexOf('"', valueStart + 1);
                if (end < 0) break;
                value = payload[(valueStart + 1)..end];
                index = end + 1;
            }
            else
            {
                var end = payload.IndexOf(',', valueStart);
                if (end < 0) end = payload.Length;
                value = payload[valueStart..end].Trim();
                index = end;
            }

            if (key.Length > 0) parameters[key] = value;
        }

        return new AuthChallenge(scheme.Length == 0 ? "Bearer" : scheme, parameters);
    }

    /// <summary>按挑战中的 realm 换取 pull 权限的 Bearer token；失败返回 null。</summary>
    public static async Task<string?> FetchBearerTokenAsync(
        HttpClient http,
        AuthChallenge challenge,
        string repository,
        bool isDockerHub,
        RegistryCredential? credential,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!challenge.Parameters.TryGetValue("realm", out var realm) || string.IsNullOrWhiteSpace(realm)) return null;

            var parameters = new Dictionary<string, string>(challenge.Parameters, StringComparer.OrdinalIgnoreCase);
            if (!parameters.ContainsKey("service") && isDockerHub) parameters["service"] = "registry.docker.io";
            if (!parameters.ContainsKey("scope")) parameters["scope"] = $"repository:{repository}:pull";

            var query = new StringBuilder();
            foreach (var (key, value) in parameters)
            {
                if (key.Equals("realm", StringComparison.OrdinalIgnoreCase)) continue;
                if (query.Length > 0) query.Append('&');
                query.Append(Uri.EscapeDataString(key)).Append('=').Append(Uri.EscapeDataString(value));
            }

            var url = realm + (realm.Contains('?') ? "&" : "?") + query;

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TokenTimeout);

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (credential is not null && credential.Username.Length > 0)
            {
                var raw = Convert.ToBase64String(Encoding.UTF8.GetBytes(credential.Username + ":" + credential.Password));
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic", raw);
            }

            using var response = await http
                .SendAsync(request, HttpCompletionOption.ResponseContentRead, cts.Token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;

            foreach (var key in (string[])["token", "access_token"])
            {
                if (document.RootElement.TryGetProperty(key, out var element)
                    && element.ValueKind == JsonValueKind.String)
                {
                    var value = element.GetString();
                    if (!string.IsNullOrWhiteSpace(value)) return value;
                }
            }

            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
