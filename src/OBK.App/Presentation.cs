using OBK.App.Localization;
using OBK.Core;
using OBK.Core.Localization;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Threading;

namespace OBK.App;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Notify([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Notify(name);
        return true;
    }
}

public sealed class RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null) : ICommand
{
    public bool CanExecute(object? parameter) => canExecute?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) => execute(parameter);
    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }
}

public sealed class AsyncCommand(Func<object?, Task> execute, Action<string> reportError,
    Func<object?, bool>? canExecute = null) : ICommand
{
    private bool running;
    public bool CanExecute(object? parameter) => !running && (canExecute?.Invoke(parameter) ?? true);
    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;
        running = true;
        CommandManager.InvalidateRequerySuggested();
        try { await execute(parameter); }
        catch (ObkException error) { reportError(error.Message); }
        finally { running = false; CommandManager.InvalidateRequerySuggested(); }
    }
    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }
}

public sealed class BooleanVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class AnyTrueConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) => values.Any(v => v is true);
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public static class Palette
{
    public const int PairCount = AvatarPresentation.PaletteCount;

    public static int PairIndex(string? value) => AvatarPresentation.PaletteIndex(value);

    public static string PairKey(string? value) => "Avatar" + PairIndex(value);

    public static string FileKey(string? extension) => (extension ?? "").TrimStart('.').ToLowerInvariant() switch
    {
        "pdf" => "BadgePdf",
        "pptx" => "BadgePptx",
        "docx" => "BadgeDocx",
        "xlsx" => "BadgeXlsx",
        _ => "BadgeFile"
    };
}

// Resolves a themed brush by key; the second binding only re-triggers it when the theme changes.
public sealed class ThemeBrushConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
        values.Length > 0 && values[0] is string key && Application.Current.TryFindResource(key + parameter) is { } brush
            ? brush : DependencyProperty.UnsetValue;
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>A fixed option value paired with its localized display text.</summary>
public sealed record DisplayOption<T>(T Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>Identifies a message across children: two children can receive the same school message.</summary>
public sealed record MessageKey(string Url, string Username, string Id);

/// <summary>A sidebar entry with a badge counting messages not yet opened on this PC.</summary>
public abstract class SidebarRow(string name) : ObservableObject
{
    private int newCount;
    public int NewCount
    {
        get => newCount;
        internal set
        {
            if (!Set(ref newCount, value)) return;
            Notify(nameof(HasNew));
            Notify(nameof(NewCountName));
            Notify(nameof(AccessibleName));
        }
    }
    public bool HasNew => newCount > 0;
    public string NewCountName => Localized.Format(AppStrings.SidebarNewCount, newCount);
    public string AccessibleName => HasNew ? Localized.Format(AppStrings.SidebarItemNewName, name, newCount) : name;
}

public sealed class FolderRow(string key, string title, string icon) : SidebarRow(title)
{
    public string Key { get; } = key;
    public string AccentKey { get; } = key switch { "sent" or "excuses" => "FolderSent", "noticeboard" => "FolderNotice", _ => "FolderInbox" };
    public string Title { get; } = title;
    public string Icon { get; } = icon;
}

public sealed class MessageRow(Message message, string folder, Login owner, bool showOwner = false) : ObservableObject
{
    private string? searchText;
    private bool isAlternate;
    private bool isNew;
    public bool IsAlternate { get => isAlternate; internal set => Set(ref isAlternate, value); }
    public Message Message { get; } = message;
    public string Folder { get; } = folder;
    /// <summary>The child account whose archive holds this message; attachments and related links use it.</summary>
    public Login Owner { get; } = owner;
    public MessageKey Key => new(Owner.Url, Owner.Username, Message.Id);
    public bool ShowOwner { get; } = showOwner;
    public string OwnerLabel => AccountRow.LabelFor(Owner);
    public string OwnerColorKey => AccountRow.ColorKeyFor(Owner);
    public string OwnerInitials => AccountRow.InitialsFor(OwnerLabel);
    /// <summary>Not yet opened on this PC; only Inbox and Noticeboard messages can be new.</summary>
    public bool IsNew
    {
        get => isNew;
        internal set { if (Set(ref isNew, value)) Notify(nameof(AccessibleName)); }
    }
    public string Subject => MainViewModel.OneLine(Message.Title) is { Length: > 0 } title ? title : SharedStrings.NoSubject;
    public string Person => MainViewModel.OneLine(MessageInsights.Counterpart(Message, Folder))
        is { Length: > 0 } name ? name : AppStrings.ReaderNotSupplied;
    public string PersonLine => ShowOwner ? Localized.Format(AppStrings.MessageChildAndPerson, OwnerLabel, Person) : Person;
    public string Sent => Message.Date.ToString(SharedStrings.DateLongFormat, LanguageSettings.Culture);
    public string ShortSent
    {
        get
        {
            var local = Message.Date.LocalDateTime;
            var today = DateTime.Today;
            return local.Date == today ? local.ToString(SharedStrings.DateTimeFormat, LanguageSettings.Culture) :
                local.Year == today.Year ? local.ToString(SharedStrings.DateShortFormat, LanguageSettings.Culture) :
                local.ToString(SharedStrings.DateShortYearFormat, LanguageSettings.Culture);
        }
    }
    public string AccessibleName
    {
        get
        {
            var name = Localized.Format(AppStrings.MessageRowName, Subject, PersonLine, Sent);
            return IsNew ? Localized.Format(AppStrings.MessageRowNameNew, name) : name;
        }
    }
    public bool HasFiles => Message.Attachments.Count > 0;
    public bool IsExcuse { get; } = MessageInsights.IsExcuse(message);
    public string Group { get; set; } = "";
    public string SearchText => searchText ??= MessageInsights.SearchText(Message, Folder);
}

public sealed class AccountRow(Login login, bool current, bool passwordSaved = false) : SidebarRow(LabelFor(login))
{
    public Login Login { get; } = login;
    public bool IsCurrent { get; } = current;
    public bool PasswordSaved { get; } = passwordSaved;
    public bool PasswordMissing => !PasswordSaved;
    public string Label { get; } = LabelFor(login);
    public string SettingsDetail => string.Join(" \u2022 ", new[]
        {
            Login.ChildName is null ? null : Login.SchoolName,
            Login.Username,
            PasswordSaved ? AppStrings.AccountPasswordSaved : AppStrings.AccountPasswordNotSaved
        }.Where(v => !string.IsNullOrEmpty(v)));
    public string Detail => string.Join("\n", new[] { Label, Login.SchoolName, Login.Username }
        .Where(v => !string.IsNullOrEmpty(v)).Distinct());
    public string ColorKey => ColorKeyFor(Login);
    public string Initials => InitialsFor(Label);

    public static string LabelFor(Login login) => login.ChildName ?? login.SchoolName ?? login.Username;
    public static string ColorKeyFor(Login login) => Palette.PairKey(login.Url + "|" + login.Username);
    public static string InitialsFor(string label)
    {
        var words = label.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => char.IsLetterOrDigit(w[0])).ToArray();
        return words.Length == 0 ? "?" : string.Concat(words.Take(2).Select(w => char.ToUpperInvariant(w[0])));
    }
}

/// <summary>One child's circle inside the overlapping All children avatar.</summary>
public sealed record GroupAvatarPart(string Initial, string ColorKey, double Left, double Top, double Size)
{
    public double FontSize => Math.Round(Size * 0.55);
    public CornerRadius Corner => new(Size / 2);
}

/// <summary>The sidebar entry that merges every child's archive; its avatar overlaps up to three children.</summary>
public sealed class AllChildrenRow(bool current, IReadOnlyList<AccountRow> children) : SidebarRow(AppStrings.ScopeAllChildren)
{
    private const double AvatarSize = 28;
    public bool IsCurrent { get; } = current;
    public IReadOnlyList<GroupAvatarPart> Avatars { get; } = children.Take(3).Zip(children.Count == 2
        ? new[] { (X: 0.06, Y: 0.14, Size: 0.55), (X: 0.39, Y: 0.30, Size: 0.55) }
        : [(0.05, 0.11, 0.47), (0.48, 0.11, 0.47), (0.27, 0.48, 0.47)],
        (child, place) => new GroupAvatarPart(child.Initials[..1], child.ColorKey,
            AvatarSize * place.X, AvatarSize * place.Y, Math.Round(AvatarSize * place.Size))).ToArray();
}

public sealed class RelatedRow(MainViewModel owner, RelatedLink link)
{
    public MainViewModel Owner { get; } = owner;
    public MessageRef Item => link.Item;
    public bool IsNext => link.IsNext;
    public string Direction => link.Direction;
    public string Icon => IsNext ? "\uE72A" : "\uE72B";
    public string Subject => MainViewModel.OneLine(Item.Message.Title) is { Length: > 0 } title ? title : SharedStrings.NoSubject;
    public string From => link.FromYou ? SharedStrings.RelatedFromYou :
        MainViewModel.OneLine(Item.Message.SenderName) is { Length: > 0 } name ? name : AppStrings.ReaderNotSupplied;
    public string Label => Localized.Format(SharedStrings.RelatedLabel, link.When, From);
    /// <summary>The chip shows a relative day, so the exact date joins the subject in the tooltip.</summary>
    public string ToolTip => Subject + "\n" + link.FullDate;
    public string AccessibleName => Localized.Format(SharedStrings.RelatedAccessibleName, Direction, Label, link.FullDate);

    public static IReadOnlyList<RelatedRow> Build(MainViewModel owner, RelatedMessages related, DateTimeOffset displayed, DateTime today) =>
        RelatedLinks.Build(related, displayed, today).Select(link => new RelatedRow(owner, link)).ToArray();
}

public static class LinkOpener
{
    public static void Open(string? target, Action<string>? reportError = null)
    {
        if (MessageRenderer.NormalizeLink(target) is not { } link) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(link) { UseShellExecute = true }); }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            reportError?.Invoke(error.Message);
        }
    }
}

public sealed class AttachmentRow(MainViewModel owner, Attachment attachment, AttachmentInfo? info, Login account)
{
    public MainViewModel Owner { get; } = owner;
    /// <summary>The child account whose encrypted cache holds the file.</summary>
    public Login Account { get; } = account;
    public Attachment Attachment { get; } = attachment;
    public string Name => info?.Name ?? Attachment.SafeName;
    public bool Cached => info is not null;
    public bool CanOpen => Cached && FileNames.CanOpen(Name);
    public string BadgeKey => Palette.FileKey(System.IO.Path.GetExtension(Name));
    public string Size => info is null ? AppStrings.AttachmentNotDownloaded : FormatSize(info.Size);
    public string Type => System.IO.Path.GetExtension(Name).TrimStart('.').ToUpperInvariant() is { Length: > 0 } type
        ? type.Length <= 4 ? type : AppStrings.AttachmentTypeFile : AppStrings.AttachmentTypeFile;
    public string Availability => Name + "\n" + (!Cached ? AppStrings.AttachmentAvailabilityNotDownloaded :
        CanOpen ? AppStrings.AttachmentAvailabilityOpenable : AppStrings.AttachmentAvailabilitySaveOnly);
    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => Localized.Format(AppStrings.SizeBytes, bytes),
        < 1024 * 1024 => Localized.Format(AppStrings.SizeKilobytes, bytes / 1024d),
        < 1024L * 1024 * 1024 => Localized.Format(AppStrings.SizeMegabytes, bytes / (1024d * 1024)),
        _ => Localized.Format(AppStrings.SizeGigabytes, bytes / (1024d * 1024 * 1024))
    };
}

public static class MessageDocument
{
    public static readonly DependencyProperty PartsProperty = DependencyProperty.RegisterAttached(
        "Parts", typeof(IReadOnlyList<TextPart>), typeof(MessageDocument), new PropertyMetadata(null, PartsChanged));
    public static void SetParts(DependencyObject target, IReadOnlyList<TextPart> value) => target.SetValue(PartsProperty, value);
    public static IReadOnlyList<TextPart>? GetParts(DependencyObject target) => (IReadOnlyList<TextPart>?)target.GetValue(PartsProperty);

    private static async void PartsChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        if (target is not RichTextBox reader || e.NewValue is not IReadOnlyList<TextPart> parts) return;
        var paragraph = new Paragraph { Margin = new Thickness(0) };
        paragraph.SetResourceReference(Block.LineHeightProperty, "ReaderLineHeight");
        var count = 0;
        foreach (var part in parts)
        {
            var run = new Run(part.Text)
            {
                FontWeight = part.Bold || part.Heading ? FontWeights.SemiBold : FontWeights.Normal,
                FontStyle = part.Italic ? FontStyles.Italic : FontStyles.Normal
            };
            run.SetResourceReference(TextElement.FontSizeProperty, part.Heading ? "ReaderSubjectSize" : "ReaderBodySize");
            if (part.Underline) run.TextDecorations = TextDecorations.Underline;
            if (part.Link is { } link)
            {
                var hyperlink = new Hyperlink(run) { ToolTip = link, Cursor = Cursors.Hand };
                hyperlink.SetResourceReference(TextElement.ForegroundProperty, "Accent");
                hyperlink.Click += (_, _) => LinkOpener.Open(link);
                paragraph.Inlines.Add(hyperlink);
            }
            else paragraph.Inlines.Add(run);
            if (++count % 100 == 0)
            {
                await Dispatcher.Yield(DispatcherPriority.Background);
                if (!ReferenceEquals(GetParts(reader), parts)) return;
            }
        }
        reader.Document = new FlowDocument(paragraph) { PagePadding = new Thickness(0) };
    }
}
