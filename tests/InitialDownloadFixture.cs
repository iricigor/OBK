using OBK.Core;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace OBK.Verification;

internal sealed class InitialDownloadFixture(bool automatic = false) : HttpMessageHandler
{
    internal const string Url = "https://login-verification.example";
    private readonly TaskCompletionSource messages = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource one = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource three = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource allowMessages = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource allowOne = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource allowThree = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Task MessagesRequested => messages.Task;
    internal Task OneAttachmentReady => one.Task;
    internal Task ThreeAttachmentsReady => three.Task;
    internal void ContinueMessages() => allowMessages.TrySetResult();
    internal void ContinueAfterOne() => allowOne.TrySetResult();
    internal void ContinueAfterThree() => allowThree.TrySetResult();
    internal void ContinueAll() { ContinueMessages(); ContinueAfterOne(); ContinueAfterThree(); }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        if (path == "/api/login") return Json(new { access_token = "fictional-verification-token" });
        if (path == "/api/3/user") return Json(new { SchoolOrganizationName = "Example School" });
        if (path.StartsWith("/api/3/komens/messages/", StringComparison.Ordinal))
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var first = body.RootElement.GetProperty("Pagination").GetProperty("PageNumber").GetInt32() == 1;
            if (path.EndsWith("/received", StringComparison.Ordinal) && first)
            {
                messages.TrySetResult();
                await PauseAsync(allowMessages, TimeSpan.FromSeconds(10), cancellationToken);
                var year = SchoolYear.Current();
                Message Make(string id, string title, int day, List<Attachment>? files = null) => new()
                {
                    Id = id, Title = title, Text = "<p>Fictional message for login/download verification. No school is contacted.</p>",
                    SentDate = year.Start.AddDays(day).ToString("O"),
                    Sender = JsonSerializer.SerializeToElement(new { Name = "Eva Svobodova" }), Attachments = files ?? []
                };
                return Json(new
                {
                    Messages = new[]
                    {
                        Make("trip", "School trip to Brno", 4, Enumerable.Range(1, 17).Select(index => new Attachment
                        {
                            Id = $"file-{index}",
                            Name = index == 1 ? "welcome.txt" : index == 2 ? "trip-details.txt" : $"trip-note-{index:00}.txt"
                        }).ToList()),
                        Make("lunch", "Change to the lunch menu", 3),
                        Make("evaluation", "Mathematics evaluation", 2),
                        Make("meeting", "Parent meeting next Thursday", 1)
                    }
                });
            }
            return Json(new { Messages = Array.Empty<Message>() });
        }
        if (path.StartsWith("/api/3/komens/attachment/file-", StringComparison.Ordinal))
        {
            var index = int.Parse(path[(path.LastIndexOf('-') + 1)..], System.Globalization.CultureInfo.InvariantCulture);
            if (index == 2)
            {
                one.TrySetResult();
                await PauseAsync(allowOne, TimeSpan.FromSeconds(60), cancellationToken);
            }
            if (index == 4)
            {
                three.TrySetResult();
                await PauseAsync(allowThree, TimeSpan.FromSeconds(60), cancellationToken);
            }
            return new(HttpStatusCode.OK) { Content = new StringContent("Fictional attachment content.", Encoding.UTF8, "text/plain") };
        }
        throw new ObkException("Unexpected request in the isolated login verification fixture.");
    }

    private async Task PauseAsync(TaskCompletionSource gate, TimeSpan duration, CancellationToken cancellation)
    {
        if (!automatic) { await gate.Task.WaitAsync(cancellation); return; }
        using var delayCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        await Task.WhenAny(gate.Task, Task.Delay(duration, delayCancellation.Token)).WaitAsync(cancellation);
        await delayCancellation.CancelAsync();
    }

    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json")
    };
}
