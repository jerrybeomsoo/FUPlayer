using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using FUPlayer.Update;

internal static class Program
{
    [STAThread]
    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--inspect-package")
        {
            try { UpdateArchive.Inspect(args[1]); Console.WriteLine("Portable package validated."); return 0; }
            catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
        }
        if (args.Length != 2 || args[0] != "--apply") return 2;
        string ticket = Path.GetFullPath(args[1]), work = Path.GetDirectoryName(ticket)!;
        UpdateRequest? request = null;
        UpdateTransaction? transaction = null;
        bool ready = false, applied = false, parentExited = false;
        try
        {
            using var busy = new FileStream(Path.Combine(work, "busy"), FileMode.Create, FileAccess.ReadWrite, FileShare.None);
            request = UpdateRequest.Read(ticket);
            if (!Path.GetFullPath(Environment.ProcessPath!).Equals(Path.Combine(work, UpdateRequest.HelperName), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The update helper must run from its staging folder.");
            using Process parent = Process.GetProcessById(request.ParentId);
            string player = Path.Combine(request.TargetDirectory, "FUPlayer.exe");
            if (parent.StartTime.ToUniversalTime().Ticks != request.ParentStartTicks
                || !string.Equals(parent.MainModule?.FileName, player, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The running player does not match this update request.");
            string archive = Path.Combine(work, "package.zip");
            if (!UpdateTransaction.Hash(archive).Equals(request.ArchiveHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The downloaded update changed after verification.");
            UpdateArchive.RejectLinks(request.TargetDirectory, request.TargetDirectory);
            string probe = Path.Combine(request.TargetDirectory, ".fuplayer-update-" + Guid.NewGuid().ToString("N"));
            try { using (File.Create(probe)) { } }
            finally { if (File.Exists(probe)) File.Delete(probe); }
            string payload = Path.Combine(work, "payload");
            IReadOnlyList<UpdateFile> files = UpdateArchive.Extract(archive, payload);
            transaction = UpdateTransaction.Prepare(payload, request.TargetDirectory, Path.Combine(work, "backup"), files);
            if (File.Exists(Path.Combine(work, "cancel"))) throw new OperationCanceledException("Update preparation was cancelled.");
            Reply(work, new { ready = true });
            var acknowledgement = Stopwatch.StartNew();
            while (!File.Exists(Path.Combine(work, "install")))
            {
                if (File.Exists(Path.Combine(work, "cancel")) || acknowledgement.Elapsed.TotalSeconds > 15)
                    throw new OperationCanceledException("No install acknowledgement was received.");
                await Task.Delay(100);
            }
            ready = true;
            // The app shuts down normally only after this readiness acknowledgement.
            await parent.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(120));
            parentExited = true;
            var replacing = Stopwatch.StartNew();
            while (true)
            {
                try { transaction.Apply(); break; }
                catch (Exception locked) when (locked is IOException or UnauthorizedAccessException && replacing.Elapsed.TotalSeconds < 12)
                {
                    // Firefox's native-messaging child can briefly retain the player's DLLs after its UI exits.
                    // Each failed attempt has rolled back; never kill another process to release a file.
                    await Task.Delay(300);
                }
            }
            applied = true;
            using Process restarted = StartPlayer(player, request.Arguments);
            await Task.Delay(3000);
            if (restarted.HasExited && restarted.ExitCode != 0) throw new IOException("The updated player exited immediately.");
            File.WriteAllText(Path.Combine(work, "result.log"), "Update installed and player restarted.");
            try { UpdateRequest.CleanupPayload(work); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            return 0;
        }
        catch (Exception ex)
        {
            bool rollbackFailed = ex is AggregateException;
            if (applied && transaction is not null)
            {
                try { transaction.Rollback(); }
                catch (Exception restore) { rollbackFailed = true; ex = new AggregateException(ex, restore); }
            }
            if (parentExited && !rollbackFailed && request is not null)
                try { StartPlayer(Path.Combine(request.TargetDirectory, "FUPlayer.exe"), request.Arguments).Dispose(); }
                catch (Exception start) { ex = new AggregateException(ex, start); }
            try
            {
                File.WriteAllText(Path.Combine(work, "result.log"), ex.ToString());
                if (!ready) Reply(work, new { ready = false, error = ex.Message });
            }
            catch (IOException) { }
            if (ready)
            {
                bool korean = request?.Language == "ko";
                string text = korean
                    ? "FUPlayer를 업데이트하지 못했습니다. " + (rollbackFailed ? "백업 파일을 보관하고 로그를 확인하세요." : "기존 설치 파일을 유지하거나 복원했습니다.") + "\n\n" + Path.Combine(work, "result.log")
                    : "FUPlayer could not be updated. " + (rollbackFailed ? "Keep the backup files and inspect the log." : "The previous installation was kept or restored.") + "\n\n" + Path.Combine(work, "result.log");
                MessageBox(0, text, korean ? "FUPlayer 업데이트" : "FUPlayer update", 0x10);
            }
            return 1;
        }
    }

    private static Process StartPlayer(string path, string[] args)
    {
        var start = new ProcessStartInfo(path) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(path)! };
        foreach (string argument in args) start.ArgumentList.Add(argument);
        return Process.Start(start) ?? throw new IOException("FUPlayer could not be restarted.");
    }

    private static void Reply(string work, object reply)
    {
        string temporary = Path.Combine(work, "reply-" + Guid.NewGuid().ToString("N") + ".tmp");
        File.WriteAllText(temporary, JsonSerializer.Serialize(reply));
        File.Move(temporary, Path.Combine(work, "ready.json"), true);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "MessageBoxW")]
    private static extern int MessageBox(nint window, string text, string caption, uint flags);
}
