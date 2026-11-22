using OBK.App.Localization;
using OBK.Core;
using OBK.Core.Localization;
using System.Windows.Input;

namespace OBK.App;

public enum UpdateState { Current, Checking, Downloading, Ready, Scheduled, Ignored }

public sealed class UpdateManager : ObservableObject, IDisposable
{
    public static readonly TimeSpan FirstCheckDelay = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(5);
    private readonly UpdateService service;
    private readonly Version installed;
    private Func<bool> canRestart;
    private Action restart;
    private readonly CancellationTokenSource stop = new();
    private UpdateState state = UpdateState.Current;
    private AvailableUpdate? update;
    private string? installer;
    private string? error;
    private bool menuOpen;
    private bool started;
    private bool restartRequested;
    private bool checking;

    public UpdateManager(UpdateService service, Version installed, Func<bool> canRestart, Action restart)
    {
        this.service = service;
        this.installed = installed;
        this.canRestart = canRestart;
        this.restart = restart;
        InstallCommand = new RelayCommand(_ => InstallNow(), _ => state == UpdateState.Ready);
        InstallOnExitCommand = new RelayCommand(_ => Choose(UpdateState.Scheduled), _ => state == UpdateState.Ready);
        IgnoreCommand = new RelayCommand(_ => Choose(UpdateState.Ignored), _ => state == UpdateState.Ready);
        CheckNowCommand = new RelayCommand(_ => _ = CheckOnceAsync(stop.Token), _ => CanCheckNow);
    }

    public ICommand InstallCommand { get; }
    public void SetHost(Func<bool> canRestart, Action restart)
    {
        this.canRestart = canRestart;
        this.restart = restart;
        NotifyAll();
    }
    public ICommand InstallOnExitCommand { get; }
    public ICommand IgnoreCommand { get; }
    public ICommand CheckNowCommand { get; }
    public bool CanCheckNow => state == UpdateState.Current && !checking;
    public string? CheckTooltip => CanCheckNow ? AppStrings.UpdateCheckTooltip : null;
    public UpdateState State { get => state; private set { if (Set(ref state, value)) NotifyAll(); } }
    public bool IsReady => state == UpdateState.Ready;
    public bool MenuOpen { get => menuOpen; set => Set(ref menuOpen, value); }
    public string Text => checking && state == UpdateState.Current ? AppStrings.UpdateChecking :
        error is not null ? error : state switch
    {
        UpdateState.Downloading => Localized.Format(AppStrings.UpdateDownloading, UpdateService.Display(update!.Version)),
        UpdateState.Ready => Localized.Format(AppStrings.UpdateReady, UpdateService.Display(update!.Version)),
        UpdateState.Scheduled => Localized.Format(AppStrings.UpdateScheduled, UpdateService.Display(update!.Version)),
        UpdateState.Ignored => Localized.Format(AppStrings.UpdateIgnored, UpdateService.Display(update!.Version)),
        _ => Localized.Format(AppStrings.UpdateCurrent, UpdateService.Display(installed))
    };

    private void NotifyAll()
    {
        Notify(nameof(Text));
        Notify(nameof(IsReady));
        Notify(nameof(CanCheckNow));
        Notify(nameof(CheckTooltip));
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
        if (checking || state != UpdateState.Current) return;
        checking = true;
        NotifyAll();
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
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
        finally { checking = false; NotifyAll(); }
    }

    private void SetError(string? message) { error = message; NotifyAll(); }

    private void Choose(UpdateState next) { MenuOpen = false; State = next; }

    private void InstallNow()
    {
        if (!canRestart()) { SetError(AppStrings.UpdateErrorBusy); return; }
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
