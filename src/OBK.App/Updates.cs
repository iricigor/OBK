using OBK.Core;
using System.Windows.Input;

namespace OBK.App;

public enum UpdateState { Current, Checking, Downloading, Ready, Scheduled, Ignored }

public sealed class UpdateManager : ObservableObject, IDisposable
{
    public static readonly TimeSpan FirstCheckDelay = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(5);
    private readonly UpdateService service;
    private readonly Version installed;
    private readonly Func<bool> canRestart;
    private readonly Action restart;
    private readonly CancellationTokenSource stop = new();
    private UpdateState state = UpdateState.Current;
    private AvailableUpdate? update;
    private string? installer;
    private string? error;
    private bool menuOpen;
    private bool started;
    private bool restartRequested;

    public UpdateManager(UpdateService service, Version installed, Func<bool> canRestart, Action restart)
    {
        this.service = service;
        this.installed = installed;
        this.canRestart = canRestart;
        this.restart = restart;
        InstallCommand = new RelayCommand(_ => InstallNow(), _ => state == UpdateState.Ready);
        InstallOnExitCommand = new RelayCommand(_ => Choose(UpdateState.Scheduled), _ => state == UpdateState.Ready);
        IgnoreCommand = new RelayCommand(_ => Choose(UpdateState.Ignored), _ => state == UpdateState.Ready);
    }

    public ICommand InstallCommand { get; }
    public ICommand InstallOnExitCommand { get; }
    public ICommand IgnoreCommand { get; }
    public UpdateState State { get => state; private set { if (Set(ref state, value)) NotifyAll(); } }
    public bool IsReady => state == UpdateState.Ready;
    public bool MenuOpen { get => menuOpen; set => Set(ref menuOpen, value); }
    public string Text => error is not null ? error : state switch
    {
        UpdateState.Downloading => $"Downloading {UpdateService.Display(update!.Version)}...",
        UpdateState.Ready => $"{UpdateService.Display(update!.Version)} is ready to install",
        UpdateState.Scheduled => $"{UpdateService.Display(update!.Version)} will install when you exit",
        UpdateState.Ignored => $"Update {UpdateService.Display(update!.Version)} ignored this session",
        _ => $"You're on the latest version {UpdateService.Display(installed)}"
    };

    private void NotifyAll()
    {
        Notify(nameof(Text));
        Notify(nameof(IsReady));
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>Begins periodic checks; safe to call repeatedly.</summary>
    public void Start()
    {
        if (started) return;
        started = true;
        _ = RunAsync(stop.Token);
    }

    private async Task RunAsync(CancellationToken cancel)
    {
        try
        {
            await Task.Delay(FirstCheckDelay, cancel);
            while (state is UpdateState.Current)
            {
                await CheckOnceAsync(cancel);
                if (state is not UpdateState.Current) break;
                await Task.Delay(CheckInterval, cancel);
            }
        }
        catch (OperationCanceledException) { }
    }

    public async Task CheckOnceAsync(CancellationToken cancel = default)
    {
        try
        {
            var found = await service.CheckAsync(installed, cancel);
            if (found is null) { SetError(null); return; }
            update = found;
            State = UpdateState.Downloading;
            installer = await service.DownloadAsync(found, cancel);
            SetError(null);
            State = UpdateState.Ready;
        }
        catch (ObkException problem)
        {
            if (update is not null) State = UpdateState.Current;
            update = null;
            SetError(problem.Message);
        }
    }

    private void SetError(string? message) { error = message; NotifyAll(); }

    private void Choose(UpdateState next) { MenuOpen = false; State = next; }

    private void InstallNow()
    {
        if (!canRestart()) { SetError("Wait for synchronization or attachment export to finish before updating."); return; }
        restartRequested = true;
        restart();
    }

    /// <summary>Called when a requested close was refused so a stale restart is not applied later.</summary>
    public void CancelRestart() => restartRequested = false;

    /// <summary>Starts the installer after the window has closed, when an install was requested or scheduled.</summary>
    public void OnExit()
    {
        stop.Cancel();
        if (installer is null || (!restartRequested && state != UpdateState.Scheduled)) return;
        try { UpdateService.StartInstaller(installer, restartRequested); }
        catch (Exception problem) when (problem is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }

    public void Dispose() { stop.Cancel(); service.Dispose(); stop.Dispose(); }
}
