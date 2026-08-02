using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ppki.Infrastructure;

namespace Ppki.Api;

public static class SupabaseAuthenticationDefaults
{
    public const string Scheme = "Supabase";
}

public sealed class SupabaseAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> schemeOptions,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IHttpClientFactory httpClientFactory,
    IOptions<SupabaseOptions> supabaseOptions)
    : AuthenticationHandler<AuthenticationSchemeOptions>(schemeOptions, logger, encoder)
{
    private readonly SupabaseOptions _supabase = supabaseOptions.Value;

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return AuthenticateResult.NoResult();
        var token = header[7..].Trim();
        if (string.IsNullOrWhiteSpace(token)) return AuthenticateResult.NoResult();

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{_supabase.Url.TrimEnd('/')}/auth/v1/user");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.TryAddWithoutValidation("apikey", _supabase.PublishableKey);
        using var response = await httpClientFactory.CreateClient(nameof(SupabaseAuthenticationHandler)).SendAsync(request, Context.RequestAborted);
        if (!response.IsSuccessStatusCode) return AuthenticateResult.Fail("Supabase access token is invalid or expired.");

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Context.RequestAborted));
        var root = json.RootElement;
        var id = root.GetProperty("id").GetString();
        if (!Guid.TryParse(id, out _)) return AuthenticateResult.Fail("Supabase user id is invalid.");
        var email = root.TryGetProperty("email", out var emailNode) ? emailNode.GetString() : null;
        string? fullName = null;
        if (root.TryGetProperty("user_metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object && metadata.TryGetProperty("full_name", out var nameNode)) fullName = nameNode.GetString();

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, id!),
            new("sub", id!),
            new(ClaimTypes.Name, fullName ?? email ?? id!),
        };
        if (!string.IsNullOrWhiteSpace(email)) claims.Add(new Claim(ClaimTypes.Email, email));
        var identity = new ClaimsIdentity(claims, SupabaseAuthenticationDefaults.Scheme);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SupabaseAuthenticationDefaults.Scheme));
    }
}
