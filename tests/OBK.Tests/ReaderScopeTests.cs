using OBK.App;
using OBK.App.Localization;
using OBK.Core;
using OBK.Core.Localization;
using System.Globalization;
using System.Text.Json;
using System.Windows.Threading;
using Xunit;

namespace OBK.Tests;

/// <summary>All children scope (F6) and new-message state (F7) of the Windows reader, with fictional archives only.</summary>
[Collection("Language")]
public sealed class ReaderScopeTests
{
    private const string School = "https://school.example";
    private static readonly SchoolYear Year = SchoolYear.Current();

    private static Message Make(string id, string title, int day, string sender = "Jana Malá", string? recipient = null,
        string? type = null, string? file = null, bool read = true) => new()
    {
        Id = id, Title = title, Text = $"<p>{title} text</p>", RelevantName = recipient, Read = read,
        SentDate = Year.Start.AddDays(day).ToString("O"),
        Sender = JsonSerializer.SerializeToElement(new { Name = sender }),
        Attachments = file is null ? [] : [new Attachment { Id = file, Name = file + ".pdf" }],
        Extra = type is null ? null : new() { ["Type"] = JsonSerializer.SerializeToElement(type) }
    };

    private static Dictionary<string, List<Message>> Archive(IEnumerable<Message>? received = null,
        IEnumerable<Message>? noticeboard = null, IEnumerable<Message>? sent = null)
    {
        var folders = Folders.Empty();
        folders["received"].AddRange(received ?? []);
        folders["noticeboard"].AddRange(noticeboard ?? []);
        folders["sent"].AddRange(sent ?? []);
        return folders;
    }

    private static MainViewModel Reader(LocalStore store, string directory, Action<bool, Login?>? openLogin = null,
        Action<AttachmentRow, string>? export = null) =>
        new(store, new ThemeManager(Path.Combine(directory, "appearance.json")), openLogin ?? ((_, _) => { }),
            (row, action) => { export?.Invoke(row, action); return Task.CompletedTask; }, _ => Task.CompletedTask);

    private static async Task SettleAsync(MainViewModel vm)
    {
        await vm.FolderReady;
        await vm.MessageReady;
    }

    private static MessageRow Row(MainViewModel vm, string username, string id) =>
        vm.Messages.Single(row => row.Owner.Username == username && row.Message.Id == id);

    private static FolderRow Folder(MainViewModel vm, string key) => vm.Navigation.Single(folder => folder.Key == key);

    private static AccountRow Child(MainViewModel vm, string username) => vm.Accounts.Single(row => row.Login.Username == username);

    private static bool? SavedScope(string directory) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "appearance.json"))).RootElement.GetProperty("AllChildren").GetBoolean();

    /// <summary>Runs on an STA dispatcher thread with English strings; the view model needs no WPF Application.</summary>
    private static async Task RunAsync(Func<LocalStore, string, Task> body)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            _ = dispatcher.BeginInvoke(new Action(async () =>
            {
                var directory = Path.Combine(Path.GetTempPath(), "obk-scope-" + Guid.NewGuid().ToString("N"));
                var originalDefault = CultureInfo.DefaultThreadCurrentCulture;
                var originalDefaultUi = CultureInfo.DefaultThreadCurrentUICulture;
                try
                {
                    LanguageSettings.Apply("en");
                    AppStrings.Culture = LanguageSettings.Culture;
                    var store = await Task.Run(() => new LocalStore(Path.Combine(directory, "test.sqlite3")));
                    await body(store, directory);
                    completion.SetResult();
                }
                catch (Exception error) { completion.SetException(error); }
                finally
                {
                    SharedStrings.Culture = null;
                    AppStrings.Culture = null;
                    CultureInfo.DefaultThreadCurrentCulture = originalDefault;
                    CultureInfo.DefaultThreadCurrentUICulture = originalDefaultUi;
                    if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                }
            }));
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
        thread.Join();
    }

    [Fact]
    public Task AllChildrenMergesArchivesCountsNewMessagesAndOnlyExplicitSelectionOpensThem() => RunAsync(async (store, directory) =>
    {
        await Task.Run(() =>
        {
            store.SaveAccount(School, "tomas", "Tomáš", "Sample School");
            store.SaveAccount(School, "anna", "Anna", "Sample School");
            store.SaveArchive(School, "anna", Year, "Sample School", Archive(
                received: [Make("a1", "Alpha notice", 1), Make("a2", "Beta homework", 3)],
                noticeboard: [Make("an1", "Board one", 2), Make("an2", "Board two", 6)],
                sent: [Make("as1", "Gamma question", 2, sender: "Parent", recipient: "Jana Malá")]));
            store.SaveArchive(School, "tomas", Year, "Sample School", Archive(
                received: [Make("t1", "Aardvark trip", 4, sender: "Petr Velký")],
                sent: [Make("tx", "omluvenka", 5, sender: "Parent", recipient: "Petr Velký", type: "OMLUVENKA")]));
            // Fixed baselines: everything else is new, but Sent and Excuses never count.
            store.OpenedMessages(School, "anna", ["a1", "an1", "as1"]);
            store.OpenedMessages(School, "tomas", []);
        });
        var vm = Reader(store, directory);
        await vm.ActivateAccountAsync(School, "anna");
        await SettleAsync(vm);

        Assert.True(vm.ShowingAllChildren);
        Assert.True(vm.AllChildrenEntry!.IsCurrent);
        Assert.Equal(["anna", "tomas"], vm.Accounts.Select(row => row.Login.Username));
        Assert.DoesNotContain(vm.Accounts, row => row.IsCurrent);
        Assert.Equal(2, vm.AllChildrenEntry!.Avatars.Count);
        Assert.Equal(["A", "T"], vm.AllChildrenEntry!.Avatars.Select(part => part.Initial));
        Assert.Equal([2, 1], vm.Accounts.Select(row => row.NewCount));
        Assert.Equal(3, vm.AllChildrenEntry!.NewCount);
        Assert.Equal("All children, new: 3", vm.AllChildrenEntry!.AccessibleName);
        Assert.Equal([1, 2, 0, 0], vm.Navigation.Select(folder => folder.NewCount));
        Assert.Equal("Inbox, new: 2", Folder(vm, "received").AccessibleName);
        Assert.Equal("New: 2", Folder(vm, "received").NewCountName);
        Assert.Equal("Sent", Folder(vm, "sent").AccessibleName);
        Assert.Contains(vm.GroupOptions, option => option.Value == ListGrouping.Child);
        Assert.Equal("OBK - Offline Komens - All children", vm.WindowTitle);
        Assert.StartsWith("Offline archive ready • 2 children • Last synchronized: ", vm.Status);

        Assert.Equal(["t1", "a2", "a1"], vm.Messages.Select(row => row.Message.Id));
        Assert.Equal("3 messages · 2 new · All children", vm.FolderCaption);
        var trip = vm.SelectedMessage!;
        Assert.Equal("t1", trip.Message.Id);
        Assert.True(trip.IsNew, "Opening a folder selects its newest message without marking it opened.");
        Assert.True(trip.ShowOwner);
        Assert.Equal("Tomáš · Petr Velký", trip.PersonLine);
        Assert.Equal("T", trip.OwnerInitials);
        Assert.Equal(AccountRow.ColorKeyFor(Child(vm, "tomas").Login), trip.OwnerColorKey);
        Assert.StartsWith("New message: Aardvark trip, Tomáš · Petr Velký, ", trip.AccessibleName);
        Assert.EndsWith(" • Folder: Inbox • Child: Tomáš", vm.Metadata);

        // Clicking the automatically selected message opens it; selecting another one in the list does too.
        vm.MarkOpened(trip);
        Assert.False(trip.IsNew);
        Assert.Equal([2, 0], vm.Accounts.Select(row => row.NewCount));
        Assert.Equal("3 messages · 1 new · All children", vm.FolderCaption);
        vm.SelectedMessage = Row(vm, "anna", "a2");
        Assert.False(Row(vm, "anna", "a2").IsNew);
        Assert.Equal([1, 0], vm.Accounts.Select(row => row.NewCount));
        Assert.Equal(1, vm.AllChildrenEntry!.NewCount);
        Assert.Equal(0, Folder(vm, "received").NewCount);
        Assert.Equal("3 messages · All children", vm.FolderCaption);
        await vm.OpenedSaved;
        Assert.Contains("a2", store.OpenedMessages(School, "anna", []));
        Assert.Contains("t1", store.OpenedMessages(School, "tomas", []));

        // Opening a folder or searching selects automatically, which never opens a message.
        vm.SelectedFolder = Folder(vm, "noticeboard");
        await SettleAsync(vm);
        Assert.Equal("an2", vm.SelectedMessage!.Message.Id);
        Assert.True(vm.SelectedMessage.IsNew);
        vm.SearchText = "board one";
        await SettleAsync(vm);
        Assert.Equal("an1", Assert.Single(vm.Messages).Message.Id);
        Assert.Equal("1 of 2 messages match · All children", vm.FolderCaption);
        vm.SearchText = "";
        await SettleAsync(vm);
        Assert.True(Row(vm, "anna", "an2").IsNew);
        vm.SelectedFolder = Folder(vm, "excuses");
        await SettleAsync(vm);
        Assert.False(Assert.Single(vm.Messages).IsNew);
        Assert.EndsWith(" · All children", vm.FolderCaption);
        Assert.DoesNotContain("new", vm.FolderCaption);

        // Child groups follow the sidebar order and the chosen sort applies within each child.
        vm.SelectedFolder = Folder(vm, "received");
        await SettleAsync(vm);
        vm.Sort = MessageSort.Title;
        vm.Grouping = ListGrouping.Child;
        await SettleAsync(vm);
        Assert.Equal(["a1", "a2", "t1"], vm.Messages.Select(row => row.Message.Id));
        Assert.Equal(["Anna", "Anna", "Tomáš"], vm.Messages.Select(row => row.Group));
        vm.Grouping = ListGrouping.None;
        await SettleAsync(vm);
        Assert.Equal(["t1", "a1", "a2"], vm.Messages.Select(row => row.Message.Id));
        vm.Grouping = ListGrouping.Child;
        await SettleAsync(vm);

        // Choosing one child filters to it, drops Child grouping and is remembered.
        await vm.SelectAccountAsync(Child(vm, "anna"));
        await SettleAsync(vm);
        Assert.False(vm.ShowingAllChildren);
        Assert.False(SavedScope(directory));
        Assert.Equal(ListGrouping.None, vm.Grouping);
        Assert.DoesNotContain(vm.GroupOptions, option => option.Value == ListGrouping.Child);
        Assert.True(Child(vm, "anna").IsCurrent);
        Assert.False(vm.AllChildrenEntry!.IsCurrent);
        Assert.Equal(["a1", "a2"], vm.Messages.Select(row => row.Message.Id));
        Assert.All(vm.Messages, row => Assert.False(row.ShowOwner));
        Assert.Equal("Jana Malá", Row(vm, "anna", "a1").PersonLine);
        Assert.Equal("2 messages", vm.FolderCaption);
        Assert.DoesNotContain("Child:", vm.Metadata);
        Assert.Equal([1, 0, 0, 0], vm.Navigation.Select(folder => folder.NewCount));
        Assert.Equal([1, 0], vm.Accounts.Select(row => row.NewCount));
        Assert.Equal("OBK - Offline Komens - Anna", vm.WindowTitle);
        Assert.StartsWith("Offline archive ready • Synced (5 messages, 0 attachments)", vm.Status);

        await vm.SelectAllChildrenAsync();
        await SettleAsync(vm);
        Assert.True(vm.ShowingAllChildren);
        Assert.True(SavedScope(directory));
        Assert.Contains(vm.GroupOptions, option => option.Value == ListGrouping.Child);
        Assert.Equal(ListGrouping.None, vm.Grouping);
        Assert.Equal(1, Folder(vm, "noticeboard").NewCount);

        // Refresh without saved passwords contacts no school, keeps the scope, and prompts once.
        var prompts = new List<Login?>();
        var refreshing = Reader(store, directory, (_, login) => prompts.Add(login));
        await refreshing.ActivateAccountAsync(School, "anna");
        await refreshing.RefreshAllAsync();
        Assert.True(refreshing.ShowingAllChildren);
        Assert.Single(prompts);
        await vm.OpenedSaved;
    });

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public Task RefreshReportsMissingPasswordsOnlyForItsScope(bool allChildren, bool alwaysRefreshAll) =>
        RunAsync(async (store, directory) =>
    {
        await Task.Run(() =>
        {
            store.SaveAccount(School, "anna", "Anna");
            store.SaveAccount(School, "tomas", "Tomáš");
            store.SaveArchive(School, "anna", Year, "Sample School", Archive(received: [Make("a1", "Anna notice", 1)]));
            store.SaveArchive(School, "tomas", Year, "Sample School", Archive(received: [Make("t1", "Tomas notice", 2)]));
        });
        var prompts = new List<Login?>();
        var vm = Reader(store, directory, (_, login) => prompts.Add(login));
        await vm.ActivateAccountAsync(School, "tomas");
        await vm.SelectAccountAsync(Child(vm, "tomas"));
        if (allChildren) await vm.SelectAllChildrenAsync();
        await vm.Theme.SetAlwaysRefreshAllChildrenAsync(alwaysRefreshAll);

        await vm.RefreshAllAsync();
        await SettleAsync(vm);

        Assert.Equal(allChildren, vm.ShowingAllChildren);
        Assert.Contains("Tomáš", vm.Status);
        if (allChildren || alwaysRefreshAll) Assert.Contains("Anna", vm.Status);
        else Assert.DoesNotContain("Anna", vm.Status);
        Assert.False(vm.GoodStatus);
        Assert.Equal(allChildren ? "anna" : "tomas", Assert.Single(prompts)!.Username);
        Assert.Equal(allChildren ? 2 : 1, vm.Messages.Count);
        Assert.Equal("tomas", vm.Account!.Username);
    });

    [Fact]
    public Task MessagesKeepTheirOwningChildForSelectionAttachmentsAndRelatedLinks() => RunAsync(async (store, directory) =>
    {
        await Task.Run(async () =>
        {
            store.SaveAccount(School, "tomas", "Tomáš");
            store.SaveAccount(School, "anna", "Anna");
            store.SaveArchive(School, "anna", Year, "Sample School", Archive(
                received:
                [
                    Make("grade-1", "Hodnocení matematika", 1, sender: "Jan Novák"),
                    Make("shared", "School concert", 2, sender: "Office", file: "concert-file"),
                    Make("grade-2", "Re: Re: Hodnocení matematika", 4, sender: "Jan Novák")
                ],
                sent: [Make("grade-reply", "Re: Hodnocení matematika", 3, sender: "Parent", recipient: "Jan Novák")]));
            store.SaveArchive(School, "tomas", Year, "Sample School", Archive(received:
            [
                Make("shared", "School concert", 2, sender: "Office", file: "concert-file"),
                Make("tomas-grade", "Hodnocení matematika", 5, sender: "Jan Novák")
            ]));
            store.OpenedMessages(School, "anna", ["grade-1", "grade-reply"]);
            store.OpenedMessages(School, "tomas", ["tomas-grade"]);
            using var file = new MemoryStream([1, 2, 3]);
            await store.SaveAttachmentAsync(School, "anna", new Attachment { Id = "concert-file", Name = "concert-file.pdf" }, file,
                expectedSize: 3, contentType: "application/pdf");
        });
        AttachmentRow? exported = null;
        var vm = Reader(store, directory, export: (row, _) => exported = row);
        await vm.ActivateAccountAsync(School, "tomas");
        await SettleAsync(vm);
        Assert.True(vm.ShowingAllChildren);
        Assert.Equal(["tomas-grade", "grade-2", "shared", "shared", "grade-1"], vm.Messages.Select(row => row.Message.Id));

        // The same school message received by two children stays separate per child.
        vm.SelectedMessage = Row(vm, "tomas", "shared");
        await vm.MessageReady;
        Assert.False(Row(vm, "tomas", "shared").IsNew);
        Assert.True(Row(vm, "anna", "shared").IsNew);
        Assert.Equal([2, 0], vm.Accounts.Select(row => row.NewCount));
        var tomasFile = Assert.Single(vm.VisibleAttachments);
        Assert.Equal("tomas", tomasFile.Account.Username);
        Assert.False(tomasFile.Cached);
        vm.SelectedFolder = Folder(vm, "sent");
        await SettleAsync(vm);
        vm.SelectedFolder = Folder(vm, "received");
        await SettleAsync(vm);
        Assert.Equal(new MessageKey(School, "tomas", "shared"), vm.SelectedMessage!.Key);

        vm.SelectedMessage = Row(vm, "anna", "shared");
        await vm.MessageReady;
        var annaFile = Assert.Single(vm.VisibleAttachments);
        Assert.Equal("anna", annaFile.Account.Username);
        Assert.True(annaFile.Cached);
        vm.OpenAttachmentCommand.Execute(annaFile);
        Assert.Same(annaFile, exported);

        // Possibly related messages come only from the owning child's archive.
        vm.SelectedMessage = Row(vm, "tomas", "tomas-grade");
        await vm.MessageReady;
        Assert.False(vm.HasRelated);
        vm.SelectedFolder = Folder(vm, "sent");
        await SettleAsync(vm);
        Assert.Equal("grade-reply", vm.SelectedMessage!.Message.Id);
        Assert.Equal(["grade-1", "grade-2"], vm.Related.Select(row => row.Item.Message.Id));

        // Opening a related link selects it in the owner's folder and marks it opened.
        Assert.True(Folder(vm, "received").HasNew);
        vm.OpenRelatedCommand.Execute(vm.Related.Single(row => row.Item.Message.Id == "grade-2"));
        await SettleAsync(vm);
        Assert.Equal("received", vm.SelectedFolder!.Key);
        Assert.Equal(new MessageKey(School, "anna", "grade-2"), vm.SelectedMessage!.Key);
        Assert.False(vm.SelectedMessage.IsNew);
        Assert.Equal([0, 0], vm.Accounts.Select(row => row.NewCount));
        Assert.False(Folder(vm, "received").HasNew);
        await vm.OpenedSaved;
        Assert.Contains("grade-2", store.OpenedMessages(School, "anna", []));
    });

    [Fact]
    public Task NewMessagesFollowTheLocalBaselineInsteadOfTheServerReadFlag() => RunAsync(async (store, directory) =>
    {
        await Task.Run(() =>
        {
            store.SaveAccount(School, "late", "Late");
            store.SaveAccount(School, "kid", "Kid");
            store.SaveArchive(School, "kid", Year, "Sample School", Archive(
                received: [Make("r1", "Unread on server", 1, read: false), Make("r2", "Read on server", 2)],
                sent: [Make("s1", "Question", 2, sender: "Parent", recipient: "Jana Malá")]));
        });
        var vm = Reader(store, directory);
        await vm.ActivateAccountAsync(School, "kid");
        await SettleAsync(vm);
        Assert.True(vm.ShowingAllChildren);
        Assert.All(vm.Messages, row => Assert.False(row.IsNew));
        Assert.Equal(0, vm.AllChildrenEntry!.NewCount);
        Assert.Equal("2 messages · All children", vm.FolderCaption);

        await Task.Run(() =>
        {
            var archive = store.LoadArchive(School, "kid", Year)!;
            archive.Folders["received"].Add(Make("r3", "Arrived later", 3));
            archive.Folders["sent"].Add(Make("s2", "Answer", 3, sender: "Parent", recipient: "Jana Malá"));
            store.SaveArchive(School, "kid", Year, "Sample School", archive.Folders);
            store.SaveArchive(School, "late", Year, "Sample School", Archive(received: [Make("l1", "First download", 1)]));
        });
        await vm.ActivateAccountAsync(School, "kid");
        await SettleAsync(vm);
        Assert.Equal(["r3"], vm.Messages.Where(row => row.IsNew).Select(row => row.Message.Id));
        Assert.False(Row(vm, "kid", "r1").IsNew);
        Assert.False(Row(vm, "late", "l1").IsNew);
        Assert.Equal([1, 0], vm.Accounts.Select(row => row.NewCount));
        vm.SelectedFolder = Folder(vm, "sent");
        await SettleAsync(vm);
        Assert.All(vm.Messages, row => Assert.False(row.IsNew));
        Assert.Equal(0, Folder(vm, "sent").NewCount);
    });
}
