using FUPlayer.ServerHost;
using System.Diagnostics;
using System.Text.Json;
using FUPlayer.Core.Localization;

namespace FUPlayer.ServerController;

/// <summary>Starts only its own listener, waits for a successful bind, and bounds shutdown time.</summary>
internal sealed class ServerProcess(string executable) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1);
    private Process? _process;
    private ServerProcessJob? _job;
    private Task _output = Task.CompletedTask, _error = Task.CompletedTask;
    private TaskCompletionSource<ControllerStatus>? _ready;
    private CancellationTokenSource? _openingStop;
    private volatile bool _running;
    private volatile bool _stopping;

    internal bool IsRunning => _running;
    internal event Action<string>? Message;
    internal event Action<ControllerStatus?>? StatusChanged;

    internal async Task StartAsync(ServerSettings settings, CancellationToken cancellation = default)
    {
        string[] arguments = settings.Arguments();
        await _gate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            if (_running) throw new InvalidOperationException(Loc.T("The DSP server is already running."));
            using var opening = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            Volatile.Write(ref _openingStop, opening);
            try
            {
                await StopCoreAsync().ConfigureAwait(false);
                _stopping = false;
                _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
                var start = NewProcess();
                foreach (string argument in arguments) start.ArgumentList.Add(argument);
                var process = new Process { StartInfo = start };
                try
                {
                    if (!process.Start()) throw new IOException(Loc.T("The DSP server could not be started."));
                }
                catch { process.Dispose(); throw; }
                _process = process;
                _job = new ServerProcessJob(process);
                _output = ReadOutput(process, error: false);
                _error = ReadOutput(process, error: true);
                _ = ObserveExit(process, _ready);
                // The listener waits here until its process tree belongs to the controller's job.
                await process.StandardInput.WriteLineAsync("start").ConfigureAwait(false);
                await process.StandardInput.FlushAsync().ConfigureAwait(false);
                await _ready.Task.WaitAsync(TimeSpan.FromSeconds(15), opening.Token).ConfigureAwait(false);
                if (process.HasExited) throw new IOException(Loc.T("The DSP server exited before it was ready."));
                _running = true;
            }
            catch
            {
                await StopCoreAsync().ConfigureAwait(false);
                throw;
            }
            finally { Volatile.Write(ref _openingStop, null); }
        }
        finally { _gate.Release(); }
    }

    internal async Task StopAsync()
    {
        try { Volatile.Read(ref _openingStop)?.Cancel(); }
        catch (ObjectDisposedException) { }
        await _gate.WaitAsync().ConfigureAwait(false);
        try { await StopCoreAsync().ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private async Task StopCoreAsync()
    {
        Process? process = _process;
        if (process is null) return;
        _stopping = true;
        try
        {
            if (!process.HasExited)
            {
                try
                {
                    await process.StandardInput.WriteLineAsync("stop").ConfigureAwait(false);
                    await process.StandardInput.FlushAsync().ConfigureAwait(false);
                }
                catch (IOException) { }
                try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); }
                catch (TimeoutException)
                {
                    Message?.Invoke(Loc.T("The server did not stop in time; closing its worker processes."));
                    _job?.Dispose();
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                }
            }
            // Kill remaining descendants before waiting for their inherited pipe handles to close.
            _job?.Dispose(); _job = null;
            try { await Task.WhenAll(_output, _error).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch (TimeoutException) { process.StandardOutput.Close(); process.StandardError.Close(); }
        }
        finally
        {
            _job?.Dispose(); _job = null;
            _process = null; _running = false;
            process.Dispose();
            StatusChanged?.Invoke(null);
        }
    }

    private async Task ReadOutput(Process process, bool error)
    {
        try
        {
            var reader = error ? process.StandardError : process.StandardOutput;
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (!error && ControllerStatus.Read(line) is { } status)
                {
                    _ready?.TrySetResult(status);
                    StatusChanged?.Invoke(status);
                }
                else Message?.Invoke(line);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }

    private async Task ObserveExit(Process process, TaskCompletionSource<ControllerStatus> ready)
    {
        try
        {
            await process.WaitForExitAsync().ConfigureAwait(false);
            ready.TrySetException(new IOException(Loc.T("The DSP server exited before it was ready.")));
            if (ReferenceEquals(_process, process))
            {
                _running = false;
                StatusChanged?.Invoke(null);
                if (!_stopping) Message?.Invoke(Loc.F("DSP server exited with code {0}.", process.ExitCode));
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { }
    }

    internal async Task<ControllerDevices> ListDevicesAsync()
    {
        string text = await RunUtilityAsync("--devices-json").ConfigureAwait(false);
        return JsonSerializer.Deserialize<ControllerDevices>(text) ?? throw new IOException(Loc.T("The GPU device list could not be read."));
    }

    internal async Task ExtractLicensesAsync(string directory) =>
        _ = await RunUtilityAsync("--extract-licenses", directory).ConfigureAwait(false);

    private async Task<string> RunUtilityAsync(params string[] arguments)
    {
        var start = NewProcess();
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        if (!process.Start()) throw new IOException(Loc.T("The DSP server could not be started."));
        ServerProcessJob? job = null;
        try
        {
            job = new ServerProcessJob(process);
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Task<string> error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            string text = await output.ConfigureAwait(false), errors = await error.ConfigureAwait(false);
            if (process.ExitCode != 0) throw new IOException(errors);
            return text;
        }
        finally
        {
            job?.Dispose();
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
        }
    }

    private ProcessStartInfo NewProcess() => new(executable)
    {
        UseShellExecute = false, CreateNoWindow = true,
        RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        WorkingDirectory = AppContext.BaseDirectory,
    };

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
