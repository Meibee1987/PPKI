using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Ppki.Application;

namespace Ppki.Infrastructure;

public sealed class SupabaseFileStorage(IHttpClientFactory httpClientFactory, IOptions<SupabaseOptions> options, IStorageObjectPathBuilder pathBuilder) : IFileStorage
{
    private readonly SupabaseOptions _options = options.Value;

    public async Task<StoredFile> SaveAsync(Stream source, string originalFilename, string contentType, string bucket, string objectPath, CancellationToken cancellationToken)
    {
        pathBuilder.ValidateStoredPath(bucket, objectPath);
        var temp = Path.Combine(Path.GetTempPath(), $"ppki-upload-{Guid.NewGuid():N}.tmp");
        try
        {
            byte[] hash;
            await using (var output = File.Create(temp)) hash = await CopyAndHashAsync(source, output, cancellationToken);
            var info = new FileInfo(temp);
            var sha256 = Convert.ToHexStringLower(hash);

            using var request = CreateRequest(HttpMethod.Post, $"/storage/v1/object/{Escape(bucket)}/{EscapePath(objectPath)}");
            request.Headers.TryAddWithoutValidation("x-upsert", "false");
            request.Content = new StreamContent(File.OpenRead(temp));
            request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(string.IsNullOrWhiteSpace(contentType)
                ? "application/vnd.openxmlformats-officedocument.wordprocessingml.document"
                : contentType);

            using var response = await Client().SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Supabase Storage upload failed ({(int)response.StatusCode}).");

            return new StoredFile(bucket, objectPath, Path.GetFileName(originalFilename), request.Content.Headers.ContentType!.MediaType!, info.Length, sha256);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public async Task<string> MaterializeToTempFileAsync(string bucket, string objectPath, CancellationToken cancellationToken)
    {
        pathBuilder.ValidateStoredPath(bucket, objectPath);
        using var request = CreateRequest(HttpMethod.Get, $"/storage/v1/object/authenticated/{Escape(bucket)}/{EscapePath(objectPath)}");
        using var response = await Client().SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Supabase Storage download failed ({(int)response.StatusCode}).");
        }

        var temp = Path.Combine(Path.GetTempPath(), $"ppki-{Guid.NewGuid():N}.docx");
        await using var output = File.Create(temp);
        await response.Content.CopyToAsync(output, cancellationToken);
        return temp;
    }

    public async Task<string> CreateSignedDownloadUrlAsync(string bucket, string objectPath, TimeSpan lifetime, CancellationToken cancellationToken)
    {
        pathBuilder.ValidateStoredPath(bucket, objectPath);
        using var request = CreateRequest(HttpMethod.Post, $"/storage/v1/object/sign/{Escape(bucket)}/{EscapePath(objectPath)}");
        request.Content = new StringContent(JsonSerializer.Serialize(new { expiresIn = Math.Max(1, (int)lifetime.TotalSeconds) }), Encoding.UTF8, "application/json");
        using var response = await Client().SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Signed URL creation failed ({(int)response.StatusCode}).");
        using var json = JsonDocument.Parse(body);
        var relative = json.RootElement.TryGetProperty("signedURL", out var upper) ? upper.GetString() : json.RootElement.GetProperty("signedUrl").GetString();
        if (string.IsNullOrWhiteSpace(relative)) throw new InvalidOperationException("Supabase returned an empty signed URL.");
        return relative.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? relative
            : $"{_options.Url.TrimEnd('/')}/storage/v1{relative}";
    }

    public async Task DeleteAsync(string bucket, string objectPath, CancellationToken cancellationToken)
    {
        pathBuilder.ValidateStoredPath(bucket, objectPath);
        using var request = CreateRequest(HttpMethod.Delete, $"/storage/v1/object/{Escape(bucket)}/{EscapePath(objectPath)}");
        using var response = await Client().SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Supabase Storage delete failed ({(int)response.StatusCode}).");
    }

    private HttpClient Client() => httpClientFactory.CreateClient(nameof(SupabaseFileStorage));
    private HttpRequestMessage CreateRequest(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, $"{_options.Url.TrimEnd('/')}{path}");
        // New sb_secret_* keys are API keys, not JWTs. Send them only via apikey.
        // Supabase Gateway maps the secret key to service_role for Storage.
        request.Headers.TryAddWithoutValidation("apikey", _options.SecretKey);
        return request;
    }
    private static string Escape(string value) => Uri.EscapeDataString(value);
    private static string EscapePath(string path) => string.Join('/', path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));

    private static async Task<byte[]> CopyAndHashAsync(Stream source, Stream destination, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        int read;
        while ((read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
        {
            hash.AppendData(buffer, 0, read);
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        return hash.GetHashAndReset();
    }
}
