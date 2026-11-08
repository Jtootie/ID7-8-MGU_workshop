using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace MguSetup;

internal static class StoreInstaller
{
    private const string Release = "https://github.com/robertnisipeanu/mgu-store/releases/download/installer/";

    internal sealed record Result(int ExitCode, bool RestartRequested, string? RestartError = null);

    private const string RestartRequired = "Restart the head unit to finish setup.";
    // Match the upstream installer's deploy-lock check and normal restart.
    private const string RestartCommand =
        "L=/var/opt/mgu-deploy/lock; if [ -e $L ] && [ $(cut -d ' ' -f 2 $L) = $(cat /proc/sys/kernel/random/boot_id) ] && " +
        "kill -0 $(cut -d ' ' -f 1 $L) 2>/dev/null; then exit 3; fi; /usr/bin/nsm_control --requestRestart 7 --normal 2>&1";

    internal static async Task<Result> InstallAsync(string host, string identity, Action<string> log)
    {
        string architecture = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            _ => throw new IOException("The Store installer supports Windows x64 and Arm64.")
        };
        string asset = $"mgu-install-windows-{architecture}.exe.gz";
        var staging = Directory.CreateTempSubdirectory("mgu-store-");
        string compressed = Path.Combine(staging.FullName, asset);
        string executable = Path.Combine(staging.FullName, "mgu-install.exe");
        try
        {
            using var download = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            using var http = new HttpClient();
            log("Downloading the official MGU Store installer.");
            string sums = await http.GetStringAsync(Release + "SHA256SUMS", download.Token);
            using (var response = await http.GetAsync(Release + asset, HttpCompletionOption.ResponseHeadersRead, download.Token))
            {
                response.EnsureSuccessStatusCode();
                await using var output = File.Create(compressed);
                await response.Content.CopyToAsync(output, download.Token);
            }
            await UnpackVerifiedAsync(compressed, executable, sums, download.Token);
            log("Installer checksum verified. Starting Store setup.");
            var info = new ProcessStartInfo(executable)
            {
                WorkingDirectory = staging.FullName,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            foreach (string arg in new[] { "first-install", "--host", host, "--identity", identity,
                         "--ssh-opt", "BatchMode=yes", "--no-restart-prompt" })
                info.ArgumentList.Add(arg);

            using var process = new Process { StartInfo = info };
            string? lastOutputLine = null;
            process.OutputDataReceived += (_, e) =>
            {
                if (string.IsNullOrWhiteSpace(e.Data)) return;
                lastOutputLine = e.Data.Trim();
                log(e.Data);
            };
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) log(e.Data); };
            process.Start();
            process.StandardInput.Close();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync();
            process.WaitForExit();
            if (process.ExitCode != 0 || lastOutputLine != RestartRequired)
                return new Result(process.ExitCode, false);

            try
            {
                log("Store setup finished. Requesting a normal head-unit restart.");
                await RestartAsync(host, identity, log);
                return new Result(0, true);
            }
            catch (Exception ex)
            {
                log("Automatic restart could not be confirmed. " + ex.Message);
                return new Result(0, false, ex.Message);
            }
        }
        finally
        {
            foreach (string path in new[] { compressed, executable })
            {
                try { File.Delete(path); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            try { staging.Delete(); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static async Task RestartAsync(string host, string identity, Action<string> log)
    {
        var info = new ProcessStartInfo("ssh")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string arg in new[] { "-n", "-T", "-i", identity, "-o", "BatchMode=yes",
                     "-o", "ConnectTimeout=8", "-o", "IdentitiesOnly=yes",
                     "-o", "StrictHostKeyChecking=no", "-o", "UserKnownHostsFile=/dev/null",
                     "-o", "LogLevel=ERROR", "-o", "HostKeyAlgorithms=+ssh-rsa",
                     "-o", "PubkeyAcceptedKeyTypes=+ssh-rsa", $"root@{host}", RestartCommand })
            info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new IOException("Could not start OpenSSH.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill();
            throw new IOException("The restart request timed out. Check the head unit before restarting manually.");
        }
        string output = (await outputTask).Trim();
        string error = (await errorTask).Trim();
        if (output.Length > 0) log(output);
        if (error.Length > 0) log(error);
        if (process.ExitCode == 3)
            throw new IOException("Another Store deployment is running. Restart after it finishes.");
        if (process.ExitCode != 0 || output.Contains("[Error.", StringComparison.OrdinalIgnoreCase))
            throw new IOException("The restart request failed or the connection dropped. Check the head unit.");
        log("Head unit accepted the restart request.");
    }

    internal static async Task UnpackVerifiedAsync(string compressed, string executable, string sums, CancellationToken ct)
    {
        string name = Path.GetFileName(compressed);
        var hashes = sums.Split('\n')
            .Select(line => line.Trim().Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries))
            .Where(fields => fields.Length == 2 && fields[1].Trim().TrimStart('*') == name)
            .Select(fields => fields[0])
            .ToArray();
        if (hashes.Length != 1 || hashes[0].Length != 64 || !hashes[0].All(Uri.IsHexDigit))
            throw new IOException("The release does not contain a valid checksum for this installer.");
        await using var input = File.OpenRead(compressed);
        string actual = Convert.ToHexString(await SHA256.HashDataAsync(input, ct));
        if (!actual.Equals(hashes[0], StringComparison.OrdinalIgnoreCase))
            throw new IOException("The Store installer checksum failed. Run the download again.");
        input.Position = 0;
        await using var gzip = new GZipStream(input, CompressionMode.Decompress, leaveOpen: true);
        await using var output = File.Create(executable);
        await gzip.CopyToAsync(output, ct);
    }
}

