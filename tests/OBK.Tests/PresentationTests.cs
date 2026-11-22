using OBK.App;
using OBK.Core;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using Xunit;

namespace OBK.Tests;

public sealed class PresentationTests
{
    [Fact]
    public void PaletteAssignmentsAreStableAndFileKindsAreCaseInsensitive()
    {
        Assert.Equal(Palette.PairIndex("Jana Nováková"), Palette.PairIndex("  jana nováková "));
        Assert.Equal(1, Palette.PairIndex("Test School Office"));
        Assert.All(new[] { "", "a", "Marko", "Teacher" }, v => Assert.InRange(Palette.PairIndex(v), 0, Palette.PairCount - 1));
        Assert.Equal("BadgePdf", Palette.FileKey(".PDF"));
        Assert.Equal("BadgePptx", Palette.FileKey("pptx"));
        Assert.Equal("BadgeFile", Palette.FileKey(""));
        Assert.Equal("BadgeFile", Palette.FileKey("zip"));
    }

    [Fact]
    public void RelatedRowsAreChronologicalNumberedOutwardAndShowTheSender()
    {
        Message Make(string id, int day, string sender) => new()
        {
            Id = id, Title = "Hodnocení", Text = "", Attachments = [],
            SentDate = new DateTimeOffset(2026, 9, day, 10, 0, 0, TimeSpan.FromHours(2)).ToString("O"),
            Sender = System.Text.Json.JsonSerializer.SerializeToElement(new { Name = sender })
        };
        var related = new RelatedMessages(
            [new(new(Make("p1", 20, "Jan Novák"), "received"), 1), new(new(Make("p2", 10, "Parent"), "sent"), 1)],
            [new(new(Make("n1", 25, "Jan Novák"), "received"), 1)]);
        var rows = RelatedRow.Build(null!, related);
        Assert.Equal(["p2", "p1", "n1"], rows.Select(r => r.Item.Message.Id));
        Assert.Equal(["Previous (2)", "Previous (1)", "Next"], rows.Select(r => r.Direction));
        Assert.Equal("you", rows[0].From);
        Assert.Equal($"{new DateTime(2026, 9, 20, 10, 0, 0):dd.MMM HH:mm} \u2022 From: Jan Novák", rows[1].Label);
    }

    [Fact]
    public async Task ThemesAndAsyncSelectionsStayReactiveWithoutChangingArchiveReadState()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            _ = application.Dispatcher.BeginInvoke(new Action(async () =>
            {
                var directory = Path.Combine(Path.GetTempPath(), "obk-presentation-" + Guid.NewGuid().ToString("N"));
                try
                {
                    var store = await Task.Run(async () =>
                    {
                        var storage = new LocalStore(Path.Combine(directory, "test.sqlite3"));
                        var folders = Folders.Empty();
                        var attachments = Enumerable.Range(0, 6).Select(i => new Attachment { Id = $"file-{i}", Name = $"file-{i}.pdf" }).ToList();
                        folders["received"].Add(new Message
                        {
                            Id = "first", Title = "First", Text = "<p>First content</p><script>not rendered</script>",
                            SentDate = SchoolYear.Current().Start.ToString("O"), Read = false, Attachments = attachments
                        });
                        folders["received"].Add(new Message
                        {
                            Id = "second", Title = "Second", Text = "<p>Second content</p>",
                            SentDate = SchoolYear.Current().Start.AddDays(2).ToString("O"), Read = true, Attachments = []
                        });
                        folders["sent"].Add(new Message
                        {
                            Id = "sent-old", Title = "Older sent message", Text = "Older sent content",
                            SentDate = SchoolYear.Current().Start.ToString("O"), Read = true, Attachments = []
                        });
                        folders["sent"].Add(new Message
                        {
                            Id = "sent-new", Title = "Newest sent message", Text = "Newest sent content",
                            SentDate = SchoolYear.Current().Start.AddDays(3).ToString("O"), Read = true, Attachments = []
                        });
                        folders["sent"].Add(new Message
                        {
                            Id = "sent-excuse", Title = "omluvenka", Text = "Excuse", Read = true, Attachments = [],
                            SentDate = SchoolYear.Current().Start.AddDays(1).ToString("O"),
                            Extra = new() { ["Type"] = System.Text.Json.JsonSerializer.SerializeToElement("OMLUVENKA") }
                        });
                        storage.SaveArchive("https://school.example", "test", SchoolYear.Current(), "Test school", folders);
                        storage.SaveArchive("https://school.example", "other", SchoolYear.Current(), "Other account", folders);
                        foreach (var attachment in attachments)
                        {
                            using var input = new MemoryStream([1, 2, 3]);
                            await storage.SaveAttachmentAsync("https://school.example", "test", attachment, input, expectedSize: 3, contentType: "application/pdf");
                        }
                        return storage;
                    });
                    var theme = new ThemeManager(Path.Combine(directory, "appearance.json"));
                    string? exported = null;
                    var vm = new MainViewModel(store, theme, (_, _) => { },
                        (row, action) => { exported = row.Attachment.Identifier + ":" + action; return Task.CompletedTask; },
                        _ => Task.CompletedTask);
                    MessageRow M(string id) => vm.Messages.Single(m => m.Message.Id == id);
                    async Task WaitForFolderAsync()
                    {
                        await vm.FolderReady;
                        await vm.MessageReady;
                    }
                    await vm.InitializeAsync();
                    await vm.ActivateAccountAsync("https://school.example", "test");
                    await WaitForFolderAsync();
                    Assert.Equal(2, vm.Messages.Count);
                    Assert.Equal("second", vm.SelectedMessage?.Message.Id);
                    await vm.ActivateAccountAsync("https://school.example", "test",
                        childName: "Anežka", schoolName: "Selected school");
                    await WaitForFolderAsync();
                    Assert.Equal("Anežka", vm.AccountLabel);
                    Assert.Contains("Anežka", vm.WindowTitle);
                    await vm.ActivateAccountAsync("https://school.example", "test");
                    await WaitForFolderAsync();
                    Assert.Equal("Test school", vm.AccountLabel);
                    vm.SelectedMessage = M("first");
                    await vm.MessageReady;
                    Assert.Equal(3, vm.VisibleAttachments.Count);
                    Assert.Equal("+3 more", vm.MoreAttachmentsLabel);
                    Assert.True(vm.SaveAllCommand.CanExecute(null));
                    var firstFile = vm.VisibleAttachments[0];
                    vm.OpenAttachmentCommand.Execute(firstFile);
                    Assert.Equal("file-0:open", exported);
                    vm.IsExporting = true;
                    Assert.False(vm.SaveAttachmentCommand.CanExecute(firstFile));
                    vm.IsExporting = false;
                    vm.ExpandAttachmentsCommand.Execute(null);
                    Assert.Equal(6, vm.VisibleAttachments.Count);
                    Assert.False(M("first").Message.Read);
                    Assert.True(M("first").Unread);

                    vm.SelectedFolder = vm.Navigation.Single(f => f.Key == "sent");
                    await WaitForFolderAsync();
                    Assert.Equal("sent-new", vm.SelectedMessage?.Message.Id);
                    vm.SelectedMessage = vm.Messages.Single(m => m.Message.Id == "sent-old");
                    vm.SelectedFolder = vm.Navigation.Single(f => f.Key == "received");
                    await WaitForFolderAsync();
                    Assert.Equal("first", vm.SelectedMessage?.Message.Id);
                    vm.SelectedFolder = vm.Navigation.Single(f => f.Key == "sent");
                    await WaitForFolderAsync();
                    Assert.Equal("sent-old", vm.SelectedMessage?.Message.Id);
                    Assert.DoesNotContain(vm.Messages, m => m.Message.Id == "sent-excuse");
                    vm.SelectedFolder = vm.Navigation.Single(f => f.Key == "excuses");
                    await WaitForFolderAsync();
                    Assert.Equal("sent-excuse", Assert.Single(vm.Messages).Message.Id);
                    Assert.Equal(["noticeboard", "received", "sent", "excuses"], vm.Navigation.Select(f => f.Key));
                    vm.SelectedFolder = vm.Navigation.Single(f => f.Key == "noticeboard");
                    await WaitForFolderAsync();
                    Assert.Null(vm.SelectedMessage);
                    Assert.Empty(vm.Body);
                    vm.SelectedFolder = vm.Navigation.Single(f => f.Key == "received");
                    await WaitForFolderAsync();
                    Assert.Equal("first", vm.SelectedMessage?.Message.Id);
                    await vm.ActivateAccountAsync("https://school.example", "test");
                    await WaitForFolderAsync();
                    Assert.Equal("first", vm.SelectedMessage?.Message.Id);

                    vm.SelectedMessage = M("second");
                    vm.SelectedMessage = M("first");
                    vm.SelectedMessage = M("second");
                    await vm.MessageReady;
                    Assert.Equal("Second", vm.Subject);
                    Assert.Contains("Second content", string.Concat(vm.Body.Select(p => p.Text)));
                    Assert.False(vm.HasAttachments);
                    vm.SelectedFolder = vm.Navigation.Single(f => f.Key == "sent");
                    vm.SelectedFolder = vm.Navigation.Single(f => f.Key == "received");
                    await WaitForFolderAsync();
                    Assert.Equal("Inbox", vm.FolderTitle);
                    Assert.Equal(2, vm.Messages.Count);
                    Assert.Equal("noticeboard", vm.Navigation[0].Key);
                    Assert.Equal(["second", "first"], vm.Messages.Select(m => m.Message.Id));
                    vm.SearchText = "SECOND conténť";
                    await vm.FolderReady;
                    Assert.Equal("second", Assert.Single(vm.Messages).Message.Id);
                    Assert.Equal("1 of 2 messages match", vm.FolderCaption);
                    vm.SearchText = "";
                    await vm.FolderReady;
                    Assert.Equal(2, vm.Messages.Count);
                    Assert.Equal("second", vm.SelectedMessage?.Message.Id);
                    Assert.Contains("Second content", string.Concat(vm.Body.Select(p => p.Text)));

                    vm.SelectedMessage = M("first");
                    await vm.ActivateAccountAsync("https://school.example", "other");
                    await WaitForFolderAsync();
                    Assert.Equal("second", vm.SelectedMessage?.Message.Id);
                    vm.SelectedMessage = M("first");
                    await vm.ActivateAccountAsync("https://school.example", "other", new SchoolYear(SchoolYear.Current().StartYear + 1));
                    await WaitForFolderAsync();
                    Assert.Null(vm.SelectedMessage);
                    await vm.ActivateAccountAsync("https://school.example", "other", SchoolYear.Current());
                    await WaitForFolderAsync();
                    Assert.Equal("second", vm.SelectedMessage?.Message.Id);
                    await vm.ActivateAccountAsync("https://school.example", "test");
                    await WaitForFolderAsync();
                    vm.SelectedMessage = M("first");
                    await Task.Run(() =>
                    {
                        var archive = store.LoadArchive("https://school.example", "test", SchoolYear.Current()) ??
                            throw new InvalidOperationException("Test archive is missing.");
                        archive.Folders["received"].RemoveAll(m => m.Id == "first");
                        store.SaveArchive("https://school.example", "test", SchoolYear.Current(), "Test school", archive.Folders);
                    });
                    await vm.ActivateAccountAsync("https://school.example", "test");
                    await WaitForFolderAsync();
                    Assert.Equal("second", vm.SelectedMessage?.Message.Id);

                    await Task.Run(() => store.SaveAccount("https://school.example", "other"));
                    await vm.ActivateAccountAsync("https://school.example", "other");
                    var accountOrder = vm.Accounts.Select(a => a.Login.Username).ToArray();
                    await Task.Run(() => store.SaveAccount("https://school.example", "test"));
                    await vm.ActivateAccountAsync("https://school.example", "test");
                    await WaitForFolderAsync();
                    Assert.Equal(accountOrder, vm.Accounts.Select(a => a.Login.Username));
                    Assert.Equal("test", vm.Accounts.Single(a => a.IsCurrent).Login.Username);

                    var surface = new Border();
                    var text = new TextBlock();
                    surface.SetResourceReference(Border.BackgroundProperty, "Surface");
                    text.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
                    surface.Child = text;
                    var reader = new RichTextBox();
                    var panel = new StackPanel();
                    panel.Children.Add(surface);
                    panel.Children.Add(reader);
                    var window = new Window { Content = panel, Width = 240, Height = 160 };
                    window.Show();
                    MessageDocument.SetParts(reader, Enumerable.Repeat(new TextPart("stale", false, false, false, false), 200).ToArray());
                    MessageDocument.SetParts(reader, [new("current", false, false, false, false)]);
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    Assert.Equal("current\r\n", new TextRange(reader.Document.ContentStart, reader.Document.ContentEnd).Text);
                    Assert.Equal(Colors.White, ((SolidColorBrush)surface.Background).Color);
                    await theme.ToggleAsync();
                    Assert.Equal(Color.FromRgb(43, 43, 43), ((SolidColorBrush)surface.Background).Color);
                    Assert.Equal(Colors.White, ((SolidColorBrush)text.Foreground).Color);
                    theme.Apply("Light");
                    await theme.InitializeAsync();
                    Assert.True(theme.IsDark);
                    await theme.ToggleAsync();
                    Assert.Equal(Colors.White, ((SolidColorBrush)surface.Background).Color);
                    await File.WriteAllTextAsync(Path.Combine(directory, "appearance.json"), "{\"Theme\":\"invalid\"}");
                    await Assert.ThrowsAsync<ObkException>(() => theme.InitializeAsync());
                    window.Close();
                    completion.SetResult();
                }
                catch (Exception error) { completion.SetException(error); }
                finally
                {
                    if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
                    application.Shutdown();
                }
            }));
            application.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
        thread.Join();
    }
}
