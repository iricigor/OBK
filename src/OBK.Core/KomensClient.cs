using OBK.Core.Localization;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace OBK.Core;

public sealed class KomensClient : IDisposable
{
    public const string DefaultUrl = "https://zsbitovska.bakalari.cz/";
    public static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(10);
    private const int MaxAttempts = 2;
    private const int MaxResponseBytes = 2 * 1024 * 1024;
    private readonly HttpClient http;
    private readonly TimeSpan requestTimeout;
    public string BaseUrl { get; }

    public KomensClient(string url, HttpMessageHandler? handler = null, TimeSpan? requestTimeout = null)
    {
        BaseUrl = NormalizeUrl(url);
        this.requestTimeout = requestTimeout ?? DefaultRequestTimeout;
        http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    public static string NormalizeUrl(string value)
    {
        var text = value.Trim();
        if (text.Contains('\\') || text.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)) ||
            !Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            string.IsNullOrEmpty(uri.Host) || uri.UserInfo.Length != 0 ||
            text.Contains('?') || text.Contains('#') || uri.Port is < 1 or > 65535)
            throw new ObkException(SharedStrings.ErrorUrlNotHttps);
        if (uri.AbsolutePath.TrimEnd('/').EndsWith("/login", StringComparison.OrdinalIgnoreCase))
            throw new ObkException(SharedStrings.ErrorUrlIsLoginPage);
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
        var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
        if (response.IsSuccessStatusCode) return response;
        var status = (int)response.StatusCode;
        response.Dispose();
        if (status is >= 300 and < 400)
            throw new ObkException(SharedStrings.ErrorRedirectBlocked);
        if (path == "/api/login" && status is 400 or 401 or 403)
            throw new ObkException(Localized.Format(SharedStrings.ErrorSignInRejected, status));
        if (status is 401 or 403)
            throw new ObkException(Localized.Format(SharedStrings.ErrorApiAccessRejected, status));
        throw new ObkException(Localized.Format(SharedStrings.ErrorApiRequestFailed, status));
    }

    // Network failures and timeouts get one fresh attempt; HTTP errors and invalid data fail immediately.
    private async Task<T> RetryAsync<T>(Func<CancellationToken, Task<T>> attempt)
    {
        for (var tries = 1; ; tries++)
        {
            using var timeout = new CancellationTokenSource(requestTimeout);
            try { return await attempt(timeout.Token); }
            catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException)
            {
                if (tries < MaxAttempts) continue;
                throw new ObkException(error is OperationCanceledException
                    ? Localized.Format(SharedStrings.ErrorTimeout, requestTimeout.TotalSeconds)
                    : SharedStrings.ErrorConnectionFailed);
            }
        }
    }

    private Task<JsonDocument> JsonAsync(string path, string? token = null, Func<HttpContent>? body = null) =>
        RetryAsync(async cancellation =>
        {
            using var response = await OpenAsync(path, token, body?.Invoke(), cancellation);
            await using var source = await response.Content.ReadAsStreamAsync(cancellation);
            using var output = new MemoryStream();
            var buffer = new byte[65536];
            int count;
            while ((count = await source.ReadAsync(buffer, cancellation)) != 0)
            {
                if (output.Length + count > MaxResponseBytes)
                    throw new ObkException(SharedStrings.ErrorResponseTooLarge);
                output.Write(buffer, 0, count);
            }
            try { return JsonDocument.Parse(output.ToArray()); }
            catch (JsonException)
            {
                throw new ObkException(SharedStrings.ErrorInvalidJson);
            }
        });

    public async Task<string> SignInAsync(string username, string password)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
            throw new ObkException(SharedStrings.ErrorMissingCredentials);
        using var result = await JsonAsync("/api/login", body: () => new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["client_id"] = "ANDR", ["grant_type"] = "password",
                ["username"] = username.Trim(), ["password"] = password
            }));
        if (result.RootElement.ValueKind != JsonValueKind.Object ||
            !result.RootElement.TryGetProperty("access_token", out var field) ||
            field.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(field.GetString()) ||
            field.GetString()!.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
            throw new ObkException(SharedStrings.ErrorNoAccessToken);
        return field.GetString()!;
    }

    public async Task<string> SchoolNameAsync(string token)
    {
        using var result = await JsonAsync("/api/3/user", token);
        if (result.RootElement.ValueKind != JsonValueKind.Object ||
            !result.RootElement.TryGetProperty("SchoolOrganizationName", out var field) ||
            field.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(field.GetString()))
            throw new ObkException(SharedStrings.ErrorNoSchoolName);
        return field.GetString()!.Trim();
    }

    public async Task<List<Message>> FetchFolderAsync(
        string token, string folder, SchoolYear year, List<Message>? cached = null, IProgress<string>? progress = null)
    {
        if (!Folders.Keys.Contains(folder)) throw new ObkException(SharedStrings.ErrorUnknownFolder);
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
                () => new StringContent(JsonSerializer.Serialize(new
                {
                    DateFrom = since.ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture),
                    DateTo = year.End.AddMilliseconds(-1).ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture),
                    IncludeHidden = false,
                    Pagination = new { PageNumber = page, PageSize = 50 }
                }), Encoding.UTF8, "application/json"));
            if (result.RootElement.ValueKind != JsonValueKind.Object ||
                !result.RootElement.TryGetProperty("Messages", out var list) || list.ValueKind != JsonValueKind.Array)
                throw new ObkException(SharedStrings.ErrorUnrecognizedMessageList);
            List<Message> batch;
            try { batch = list.Deserialize<List<Message>>()!; }
            catch (JsonException) { throw new ObkException(SharedStrings.ErrorUnrecognizedMessage); }
            if (batch.Count == 0)
                return messages.Values.OrderByDescending(m => m.Date.DateTime).ToList();
            var newIds = 0;
            foreach (var message in batch)
            {
                if (message is null) throw new ObkException(SharedStrings.ErrorUnrecognizedMessage);
                message.Validate();
                if (seen.Add(message.Id)) newIds++;
                if (message.Date.Date >= year.Start && message.Date.Date < year.End)
                    messages[message.Id] = message;
            }
            if (newIds == 0)
                throw new ObkException(SharedStrings.ErrorRepeatedPage);
            progress?.Report(Localized.Format(SharedStrings.ProgressFolderPage, Folders.Title(folder), page, messages.Count));
        }
        throw new ObkException(SharedStrings.ErrorPaginationLimit);
    }

    public async Task<Dictionary<string, List<Message>>> FetchYearAsync(
        string token, SchoolYear year, Dictionary<string, List<Message>>? cached = null, IProgress<string>? progress = null)
    {
        var result = Folders.Empty();
        foreach (var folder in Folders.Keys)
        {
            try { result[folder] = await FetchFolderAsync(token, folder, year, cached?[folder], progress); }
            catch (ObkException error) { throw new ObkException(Localized.Format(SharedStrings.ErrorInContext, Folders.Title(folder), error.Message)); }
        }
        return result;
    }

    public async Task<int> DownloadAttachmentsAsync(string token, Dictionary<string, List<Message>> folders,
        LocalStore store, string username, IProgress<string>? progress = null,
        IProgress<AttachmentDownloadProgress>? attachmentProgress = null)
    {
        var attachments = new Dictionary<string, Attachment>();
        foreach (var attachment in folders.Values.SelectMany(m => m).SelectMany(m => m.Attachments))
        {
            _ = attachment.SafeName;
            attachments.TryAdd(attachment.Identifier, attachment);
        }
        var completed = attachmentProgress is null ? 0 :
            attachments.Values.Count(attachment => store.AttachmentInfo(BaseUrl, username, attachment.Identifier) is not null);
        attachmentProgress?.Report(new(completed, attachments.Count));
        var index = 0;
        foreach (var attachment in attachments.Values)
        {
            index++;
            progress?.Report(Localized.Format(SharedStrings.ProgressAttachment, index, attachments.Count, attachment.SafeName));
            try
            {
                if (store.AttachmentInfo(BaseUrl, username, attachment.Identifier) is not null) continue;
                attachmentProgress?.Report(new(completed, attachments.Count, attachment.SafeName));
                await RetryAsync(async cancellation =>
                {
                    using var response = await OpenAsync("/api/3/komens/attachment/" +
                        Uri.EscapeDataString(attachment.Identifier), token, cancellation: cancellation);
                    await using var stream = await response.Content.ReadAsStreamAsync();
                    return await store.SaveAttachmentAsync(BaseUrl, username, attachment, stream,
                        response.Content.Headers.ContentLength, response.Content.Headers.ContentType?.ToString() ?? "",
                        idleTimeout: requestTimeout);
                });
                completed++;
                attachmentProgress?.Report(new(completed, attachments.Count));
            }
            catch (ObkException error) { throw new ObkException(Localized.Format(SharedStrings.ErrorInAttachment, index, attachments.Count, error.Message)); }
        }
        return attachments.Count;
    }

    public void Dispose() => http.Dispose();
}
