using OBK.Core;
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
    public const int PairCount = 6;

    // FNV-1a keeps assignments stable across launches, unlike string.GetHashCode.
    public static int PairIndex(string? value)
    {
        var hash = 2166136261u;
        foreach (var c in (value ?? "").Trim().ToLowerInvariant())
            hash = (hash ^ c) * 16777619u;
        return (int)(hash % PairCount);
    }

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

public sealed class FolderRow(string key, string title, string icon, int count)
{
    public string Key { get; } = key;
    public string AccentKey { get; } = key switch { "sent" or "excuses" => "FolderSent", "noticeboard" => "FolderNotice", _ => "FolderInbox" };
    public string Title { get; } = title;
    public string Icon { get; } = icon;
    public int Count { get; } = count;
}

public sealed class MessageRow(Message message, string folder)
{
    private string? searchText;
    public Message Message { get; } = message;
    public string Folder { get; } = folder;
    public bool Unread => Folder != "sent" && Message.Read == false;
    public string Subject => MainViewModel.OneLine(Message.Title) is { Length: > 0 } title ? title : "(No subject)";
    public string Person => MainViewModel.OneLine(MessageInsights.Counterpart(Message, Folder))
        is { Length: > 0 } name ? name : "(Not supplied)";
    public string Sent => Message.Date.ToString("dd MMM yyyy HH:mm");
    public string ShortSent
    {
        get
        {
            var local = Message.Date.LocalDateTime;
            var today = DateTime.Today;
            return local.Date == today ? local.ToString("HH:mm") :
                local.Year == today.Year ? local.ToString("d MMM") : local.ToString("d MMM yyyy");
        }
    }
    public bool HasFiles => Message.Attachments.Count > 0;
    public bool IsExcuse { get; } = MessageInsights.IsExcuse(message);
    public string Group { get; set; } = "";
    public string SearchText => searchText ??= MessageInsights.SearchText(Message, Folder);
}

public sealed class AccountRow(Login login, bool current, bool passwordSaved = false)
{
    public Login Login { get; } = login;
    public bool IsCurrent { get; } = current;
    public bool PasswordSaved { get; } = passwordSaved;
    public bool PasswordMissing => !PasswordSaved;
    public string Label { get; } = login.ChildName ?? login.SchoolName ?? login.Username;
    public string SettingsDetail => string.Join(" \u2022 ", new[]
        {
            Login.ChildName is null ? null : Login.SchoolName,
            Login.Username,
            PasswordSaved ? "Password saved" : "Password not saved"
        }.Where(v => !string.IsNullOrEmpty(v)));
    public string Detail => string.Join("\n", new[] { Label, Login.SchoolName, Login.Username }
        .Where(v => !string.IsNullOrEmpty(v)).Distinct());
    public string ColorKey => Palette.PairKey(Login.Url + "|" + Login.Username);
    public string Initials
    {
        get
        {
            var words = Label.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => char.IsLetterOrDigit(w[0])).ToArray();
            return words.Length == 0 ? "?" : string.Concat(words.Take(2).Select(w => char.ToUpperInvariant(w[0])));
        }
    }
}

public sealed class RelatedRow(MainViewModel owner, MessageRef item, bool next, int? number = null)
{
    public MainViewModel Owner { get; } = owner;
    public MessageRef Item { get; } = item;
    public bool IsNext { get; } = next;
    public string Direction => (IsNext ? "Next" : "Previous") + (number is { } n ? $" ({n})" : "");
    public string Icon => IsNext ? "\uE72A" : "\uE72B";
    public string Subject => MainViewModel.OneLine(Item.Message.Title) is { Length: > 0 } title ? title : "(No subject)";
    public string From => Item.Folder == "sent" ? "you" :
        MainViewModel.OneLine(Item.Message.SenderName) is { Length: > 0 } name ? name : "(Not supplied)";
    public string Label => $"{Item.Message.Date.ToString("dd.MMM HH:mm", System.Globalization.CultureInfo.CurrentCulture)} \u2022 From: {From}";

    /// <summary>Chronological rows; numbers count outward from the current message when a side has several.</summary>
    public static IReadOnlyList<RelatedRow> Build(MainViewModel owner, RelatedMessages related)
    {
        var previous = related.Previous.Select((r, i) =>
            new RelatedRow(owner, r.Item, false, related.Previous.Count > 1 ? i + 1 : null)).Reverse();
        var following = related.Responses.Select((r, i) =>
            new RelatedRow(owner, r.Item, true, related.Responses.Count > 1 ? i + 1 : null));
        return previous.Concat(following).ToArray();
    }
}

public static class LinkOpener
{
    public static void Open(string? target)
    {
        if (MessageRenderer.NormalizeLink(target) is not { } link) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(link) { UseShellExecute = true }); }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }
}

public sealed class AttachmentRow(MainViewModel owner, Attachment attachment, AttachmentInfo? info)
{
    public MainViewModel Owner { get; } = owner;
    public Attachment Attachment { get; } = attachment;
    public string Name => info?.Name ?? Attachment.SafeName;
    public bool Cached => info is not null;
    public bool CanOpen => Cached && FileNames.CanOpen(Name);
    public string BadgeKey => Palette.FileKey(System.IO.Path.GetExtension(Name));
    public string Size => info is null ? "Not downloaded" : FormatSize(info.Size);
    public string Type => System.IO.Path.GetExtension(Name).TrimStart('.').ToUpperInvariant() is { Length: > 0 } type
        ? type.Length <= 4 ? type : "FILE" : "FILE";
    public string Availability => Name + "\n" + (!Cached ? "Not downloaded. Refresh to cache this file." :
        CanOpen ? "Cached offline. Open a decrypted copy in your default viewer." :
        "Cached offline. This file type can be saved, but not opened directly.");
    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes:N0} B",
        < 1024 * 1024 => $"{bytes / 1024d:N0} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024d * 1024):0.#} MB",
        _ => $"{bytes / (1024d * 1024 * 1024):0.#} GB"
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
        var paragraph = new Paragraph { Margin = new Thickness(0), LineHeight = 21 };
        var count = 0;
        foreach (var part in parts)
        {
            var run = new Run(part.Text)
            {
                FontWeight = part.Bold || part.Heading ? FontWeights.SemiBold : FontWeights.Normal,
                FontStyle = part.Italic ? FontStyles.Italic : FontStyles.Normal,
                FontSize = part.Heading ? 18 : 14
            };
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
