using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace OBK.Core;

public sealed class KomensClient : IDisposable
{
    public const string DefaultUrl = "https://zsbitovska.bakalari.cz/";
    private const int MaxResponseBytes = 2 * 1024 * 1024;
    private readonly HttpClient http;
    public string BaseUrl { get; }

    public KomensClient(string url, HttpMessageHandler? handler = null)
    {
        BaseUrl = NormalizeUrl(url);
        http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(20)
        };
    }

    public static string NormalizeUrl(string value)
    {
        var text = value.Trim();
        if (text.Contains('\\') || text.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)) ||
            !Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            string.IsNullOrEmpty(uri.Host) || uri.UserInfo.Length != 0 ||
            text.Contains('?') || text.Contains('#') || uri.Port is < 1 or > 65535)
            throw new ObkException("Use an HTTPS school URL without credentials, query, or fragment.");
        if (uri.AbsolutePath.TrimEnd('/').EndsWith("/login", StringComparison.OrdinalIgnoreCase))
            throw new ObkException("Enter the school's base URL, not its /login page.");
        // Keep the original spelling to reuse the Python cache's account keys.
        return text.TrimEnd('/');
    }

    private async Task<HttpResponseMessage> OpenAsync(
        string path, string? token = null, HttpContent? content = null, CancellationToken cancellation = default)
    {
        using var request = new HttpRequestMessage(content is null ? HttpMethod.Get : HttpMethod.Post, BaseUrl + path);
        request.Content = content;
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(content is null && path.Contains("/attachment/") ? "*/*" : "application/json"));
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException)
        {
            throw new ObkException("Connection failed. Check internet access and the school's HTTPS certificate. No certificate checks were bypassed.");
        }
        if (response.IsSuccessStatusCode) return response;
        var status = (int)response.StatusCode;
        response.Dispose();
        if (status is >= 300 and < 400)
            throw new ObkException("The school redirected the request. Check the base URL; redirects are blocked to protect credentials.");
        if (path == "/api/login" && status is 400 or 401 or 403)
            throw new ObkException($"Sign-in rejected (HTTP {status}). Check credentials. The documented mobile login may also be disabled or incompatible.");
        if (status is 401 or 403)
            throw new ObkException($"API access rejected (HTTP {status}). The account or token may not have Komens access.");
        throw new ObkException($"API request failed (HTTP {status}). Check school availability and API compatibility.");
    }

    private async Task<JsonDocument> JsonAsync(string path, string? token = null, HttpContent? body = null)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var response = await OpenAsync(path, token, body, timeout.Token);
        try
        {
            await using var source = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var output = new MemoryStream();
            var buffer = new byte[65536];
            int count;
            while ((count = await source.ReadAsync(buffer, timeout.Token)) != 0)
            {
                if (output.Length + count > MaxResponseBytes)
                    throw new ObkException("The API response exceeded the 2 MB limit.");
                output.Write(buffer, 0, count);
            }
            return JsonDocument.Parse(output.ToArray());
        }
        catch (JsonException)
        {
            throw new ObkException("The school returned an invalid JSON response. Check API compatibility.");
        }
        catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException)
        {
            throw new ObkException("The API response was interrupted. Check the connection and retry.");
        }
    }

    public async Task<string> SignInAsync(string username, string password)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
            throw new ObkException("Enter both a username and password.");
        using var result = await JsonAsync("/api/login", body: new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["client_id"] = "ANDR", ["grant_type"] = "password",
                ["username"] = username.Trim(), ["password"] = password
            }));
        if (result.RootElement.ValueKind != JsonValueKind.Object ||
            !result.RootElement.TryGetProperty("access_token", out var field) ||
            field.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(field.GetString()) ||
            field.GetString()!.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
            throw new ObkException("Sign-in returned no usable access token.");
        return field.GetString()!;
    }

    public async Task<string> SchoolNameAsync(string token)
    {
        using var result = await JsonAsync("/api/3/user", token);
        if (result.RootElement.ValueKind != JsonValueKind.Object ||
            !result.RootElement.TryGetProperty("SchoolOrganizationName", out var field) ||
            field.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(field.GetString()))
            throw new ObkException("The school profile did not return a school name.");
        return field.GetString()!.Trim();
    }

    public async Task<List<Message>> FetchFolderAsync(
        string token, string folder, SchoolYear year, List<Message>? cached = null, IProgress<string>? progress = null)
    {
        if (!Folders.Names.ContainsKey(folder)) throw new ObkException("Unknown Komens folder.");
        var messages = new Dictionary<string, Message>();
        foreach (var message in cached ?? [])
        {
            message.Validate();
            if (message.Date.Date >= year.Start && message.Date.Date < year.End)
                messages[message.Id] = message;
        }
        var since = messages.Count == 0 ? year.Start : messages.Values.MaxBy(m => m.Date)!.Date.DateTime;
        var seen = new HashSet<string>();
        for (var page = 1; page <= 10000; page++)
        {
            using var result = await JsonAsync($"/api/3/komens/messages/{folder}", token,
                new StringContent(JsonSerializer.Serialize(new
                {
                    DateFrom = since.ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture),
                    DateTo = year.End.AddMilliseconds(-1).ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture),
                    IncludeHidden = false,
                    Pagination = new { PageNumber = page, PageSize = 50 }
                }), Encoding.UTF8, "application/json"));
            if (result.RootElement.ValueKind != JsonValueKind.Object ||
                !result.RootElement.TryGetProperty("Messages", out var list) || list.ValueKind != JsonValueKind.Array)
                throw new ObkException("Komens returned an unrecognized message-list format.");
            List<Message> batch;
            try { batch = list.Deserialize<List<Message>>()!; }
            catch (JsonException) { throw new ObkException("Komens returned an unrecognized message format."); }
            if (batch.Count == 0)
                return messages.Values.OrderByDescending(m => m.Date.DateTime).ToList();
            var newIds = 0;
            foreach (var message in batch)
            {
                if (message is null) throw new ObkException("Komens returned an unrecognized message format.");
                message.Validate();
                if (seen.Add(message.Id)) newIds++;
                if (message.Date.Date >= year.Start && message.Date.Date < year.End)
                    messages[message.Id] = message;
            }
            if (newIds == 0)
                throw new ObkException("The school repeated a page without new messages. Synchronization stopped rather than saving an incomplete archive.");
            progress?.Report($"{Folders.Names[folder]}: page {page}, {messages.Count} messages...");
        }
        throw new ObkException("The school did not finish pagination after 10,000 pages. No incomplete archive was saved.");
    }

    public async Task<Dictionary<string, List<Message>>> FetchYearAsync(
        string token, SchoolYear year, Dictionary<string, List<Message>>? cached = null, IProgress<string>? progress = null)
    {
        var result = Folders.Empty();
        foreach (var folder in Folders.Names.Keys)
        {
            try { result[folder] = await FetchFolderAsync(token, folder, year, cached?[folder], progress); }
            catch (ObkException error) { throw new ObkException($"{Folders.Names[folder]}: {error.Message}"); }
        }
        return result;
    }

    public async Task<int> DownloadAttachmentsAsync(string token, Dictionary<string, List<Message>> folders,
        LocalStore store, string username, IProgress<string>? progress = null)
    {
        var attachments = new Dictionary<string, Attachment>();
        foreach (var attachment in folders.Values.SelectMany(m => m).SelectMany(m => m.Attachments))
        {
            _ = attachment.SafeName;
            attachments.TryAdd(attachment.Identifier, attachment);
        }
        var index = 0;
        foreach (var attachment in attachments.Values)
        {
            index++;
            progress?.Report($"Attachment {index} of {attachments.Count}: {attachment.SafeName}...");
            try
            {
                if (store.AttachmentInfo(BaseUrl, username, attachment.Identifier) is not null) continue;
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                using var response = await OpenAsync("/api/3/komens/attachment/" +
                    Uri.EscapeDataString(attachment.Identifier), token, cancellation: timeout.Token);
                await using var stream = await response.Content.ReadAsStreamAsync();
                await store.SaveAttachmentAsync(BaseUrl, username, attachment, stream,
                    response.Content.Headers.ContentLength, response.Content.Headers.ContentType?.ToString() ?? "");
            }
            catch (ObkException error) { throw new ObkException($"Attachment {index} of {attachments.Count}: {error.Message}"); }
            catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException)
            {
                throw new ObkException($"Attachment {index} of {attachments.Count}: download interrupted. Retry sync; completed downloads will be reused.");
            }
        }
        return attachments.Count;
    }

    public void Dispose() => http.Dispose();
}
