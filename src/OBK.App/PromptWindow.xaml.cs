using OBK.Core;
using System.ComponentModel;
using System.Windows;

namespace OBK.App;

/// <summary>Asks for one value; the submit action runs while the dialog stays open so failures can be retried.</summary>
public partial class PromptWindow : Window
{
    private readonly Func<string, Task> submit;
    private readonly bool password;
    private bool busy;

    public PromptWindow(Window owner, string heading, string description, string label, string action,
        Func<string, Task> submit, bool password = false, string initial = "")
    {
        InitializeComponent();
        Owner = owner;
        Title = heading;
        Heading.Text = heading;
        Description.Text = description;
        FieldLabel.Text = label;
        SubmitButton.Content = action;
        this.submit = submit;
        this.password = password;
        ValueText.Text = initial;
        ValueText.Visibility = password ? Visibility.Collapsed : Visibility.Visible;
        PasswordInput.Visibility = password ? Visibility.Visible : Visibility.Collapsed;
        System.Windows.Automation.AutomationProperties.SetName(ValueText, label);
        System.Windows.Automation.AutomationProperties.SetName(PasswordInput, label);
        Loaded += (_, _) =>
        {
            if (password) PasswordInput.Focus();
            else { ValueText.Focus(); ValueText.SelectAll(); }
        };
    }

    public string Value => password ? PasswordInput.Password : ValueText.Text;

    private async void SubmitClick(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        SetBusy(true);
        try
        {
            await submit(Value);
            busy = false;
            DialogResult = true;
        }
        catch (ObkException error)
        {
            StatusText.Text = error.Message;
            StatusText.Visibility = Visibility.Visible;
            SetBusy(false);
            if (password) { PasswordInput.Focus(); PasswordInput.SelectAll(); }
            else ValueText.Focus();
        }
    }

    private void SetBusy(bool value)
    {
        busy = value;
        ValueText.IsEnabled = PasswordInput.IsEnabled = SubmitButton.IsEnabled = CancelButton.IsEnabled = CloseButton.IsEnabled = !value;
        if (value && password)
        {
            StatusText.Text = "Signing in to verify the password...";
            StatusText.Visibility = Visibility.Visible;
        }
    }

    private void CancelClick(object sender, RoutedEventArgs e) { if (!busy) DialogResult = false; }

    private void WindowClosing(object? sender, CancelEventArgs e) => e.Cancel = busy;
}
