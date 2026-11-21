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

public sealed class FolderRow(string key, string title, string icon, int count)
{
    public string Key { get; } = key;
    public string Title { get; } = title;
    public string Icon { get; } = icon;
    public int Count { get; } = count;
}

public sealed class MessageRow(Message message, string folder)
{
    public Message Message { get; } = message;
    public bool Unread => folder != "sent" && Message.Read == false;
    public string Subject => MainViewModel.OneLine(Message.Title) is { Length: > 0 } title ? title : "(No subject)";
    public string Person => MainViewModel.OneLine(folder == "sent" ? Message.RelevantName ?? Message.SenderName : Message.SenderName)
        is { Length: > 0 } name ? name : "(Not supplied)";
    public string Sent => Message.Date.ToString("dd MMM yyyy HH:mm");
    public bool HasFiles => Message.Attachments.Count > 0;
}

public sealed class AttachmentRow(MainViewModel owner, Attachment attachment, AttachmentInfo? info)
{
    public MainViewModel Owner { get; } = owner;
    public Attachment Attachment { get; } = attachment;
    public string Name => info?.Name ?? Attachment.SafeName;
    public bool Cached => info is not null;
    public bool CanOpen => Cached && FileNames.CanOpen(Name);
    public string Size => info is null ? "Not downloaded" : FormatSize(info.Size);
    public string Type => System.IO.Path.GetExtension(Name).TrimStart('.').ToUpperInvariant() is { Length: > 0 } type
        ? type.Length <= 4 ? type : "FILE" : "FILE";
    public string Availability => Name + "\n" + (!Cached ? "Not downloaded. Sign in / Sync to cache this file." :
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
        var paragraph = new Paragraph { Margin = new Thickness(0), LineHeight = 21.7 };
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
            paragraph.Inlines.Add(run);
            if (++count % 100 == 0)
            {
                await Dispatcher.Yield(DispatcherPriority.Background);
                if (!ReferenceEquals(GetParts(reader), parts)) return;
            }
        }
        reader.Document = new FlowDocument(paragraph) { PagePadding = new Thickness(0) };
    }
}
