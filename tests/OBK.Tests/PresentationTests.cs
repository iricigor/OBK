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
                            SentDate = SchoolYear.Current().Start.ToString("O"), Read = true, Attachments = []
                        });
                        storage.SaveArchive("https://school.example", "test", SchoolYear.Current(), "Test school", folders);
                        foreach (var attachment in attachments)
                        {
                            using var input = new MemoryStream([1, 2, 3]);
                            await storage.SaveAttachmentAsync("https://school.example", "test", attachment, input, expectedSize: 3, contentType: "application/pdf");
                        }
                        return storage;
                    });
                    var theme = new ThemeManager(Path.Combine(directory, "appearance.json"));
                    string? exported = null;
                    var vm = new MainViewModel(store, theme, () => { },
                        (row, action) => { exported = row.Attachment.Identifier + ":" + action; return Task.CompletedTask; },
                        _ => Task.CompletedTask);
                    await vm.InitializeAsync();
                    await vm.ActivateAccountAsync("https://school.example", "test");
                    await vm.FolderReady;
                    Assert.Equal(2, vm.Messages.Count);
                    vm.SelectedMessage = vm.Messages[0];
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
                    Assert.False(vm.Messages[0].Message.Read);
                    Assert.True(vm.Messages[0].Unread);

                    vm.SelectedMessage = vm.Messages[1];
                    vm.SelectedMessage = vm.Messages[0];
                    vm.SelectedMessage = vm.Messages[1];
                    await vm.MessageReady;
                    Assert.Equal("Second", vm.Subject);
                    Assert.Contains("Second content", string.Concat(vm.Body.Select(p => p.Text)));
                    Assert.False(vm.HasAttachments);
                    vm.SelectedFolder = vm.Navigation.Single(f => f.Key == "sent");
                    vm.SelectedFolder = vm.Navigation.Single(f => f.Key == "received");
                    await vm.FolderReady;
                    Assert.Equal("Inbox", vm.FolderTitle);
                    Assert.Equal(2, vm.Messages.Count);
                    Assert.Null(vm.SelectedMessage);

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
