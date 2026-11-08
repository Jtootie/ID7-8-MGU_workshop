using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;
namespace MguSetup;
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Variant.SelectedIndex = 0;
        LocateHelper();
    }
    private bool LocateHelper()
    {
        var candidates = new[] {
            Path.Combine(AppContext.BaseDirectory, "tools", "mgu-ssh-cli"),
            Path.Combine(AppContext.BaseDirectory, "mgu-ssh-cli"),
            AppContext.BaseDirectory
        };
        folder = candidates.FirstOrDefault(p => File.Exists(Path.Combine(p, "mgu-ssh-cli.exe"))
            && Directory.Exists(Path.Combine(p, "_internal"))
            && Directory.Exists(Path.Combine(p, "data", "assets")));
        FolderLabel.Text = folder != null ? "SSH helper ready. Leave the address blank for automatic detection." : "SSH helper missing. Keep the complete tools folder beside MguSetup.exe.";
        return folder != null;
    }
    private string? folder;
    private async void CheckStatus(object sender, RoutedEventArgs e)
    {
        if ((folder == null || !File.Exists(Path.Combine(folder, "mgu-ssh-cli.exe"))) && !LocateHelper())
        { State.Text = "Bundled SSH helper is missing. Restore the bundled tools folder beside the app."; return; }
        SetBusy(true);
        try
        {
            var info = CreateSshHelperStartInfo();

            string gateway = await ResolveGatewayAsync();
            info.ArgumentList.Add("status");
            info.ArgumentList.Add("--gateway");
            info.ArgumentList.Add(gateway);
            using var process = new Process { StartInfo = info };
            process.OutputDataReceived += (_, args) => Append(args.Data);
            process.ErrorDataReceived += (_, args) => Append(args.Data);
            State.Text = "Checking connection…";
            process.Start(); process.BeginOutputReadLine(); process.BeginErrorReadLine();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            try { await process.WaitForExitAsync(timeout.Token); process.WaitForExit(); }
            catch (OperationCanceledException) { process.Kill(true); await process.WaitForExitAsync(); throw new TimeoutException("Status check timed out."); }
            State.Text = process.ExitCode == 0 ? "Status check completed. Review output." : $"Status check failed (exit {process.ExitCode}).";
        }
        catch (Exception ex) { State.Text = ex.Message; }
        finally { SetBusy(false); }
    }
    private bool busy;
    private bool preventClose;

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (preventClose) e.Cancel = true;
        base.OnClosing(e);
    }

    private void UpdateSshAvailability(object sender, RoutedEventArgs e)
    {
        if (SshButton != null)
            SshButton.IsEnabled = !busy && AcceptRisk.IsChecked == true && Variant.SelectedItem is System.Windows.Controls.ComboBoxItem { Tag: string };
    }
    private async void InstallSsh(object sender, RoutedEventArgs e)
    {
        if (busy || AcceptRisk.IsChecked != true) return;
        if (Variant.SelectedItem is not System.Windows.Controls.ComboBoxItem { Tag: string arch })
        { State.Text = "Select the exact head-unit variant first."; return; }
        if (!LocateHelper()) return;
        using var account = System.Security.Principal.WindowsIdentity.GetCurrent();
        if (!new System.Security.Principal.WindowsPrincipal(account).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator))
        { State.Text = "Run MGU Setup as Administrator for SSH setup."; return; }
        SetBusy(true, preventClose: true);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            string gateway = await ResolveGatewayAsync();
            State.Text = "Checking vehicle identity…";
            string vin = await MguDiagnostics.CheckBeforeSshSetup(gateway, timeout.Token);
            if (MessageBox.Show(this, $"Flash and enable persistent root SSH on {arch}, VIN ending {vin[^7..]}? First-time setup only. Flashing clears coding and Digital Key pairings. Keep the car parked, connected, and on stable power.", "Confirm first-time SSH setup", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            { State.Text = "SSH setup canceled."; return; }
            if (await MguDiagnostics.CheckBeforeSshSetup(gateway, timeout.Token) != vin) throw new IOException("Vehicle changed. Flash not started.");
            var info = CreateSshHelperStartInfo();
            foreach (string arg in new[] { "flash", "--gateway", gateway, "--vin", vin, "--arch", arch, "--persist", "--yes" }) info.ArgumentList.Add(arg);
            using var process = new Process { StartInfo = info };
            process.OutputDataReceived += (_, args) => Append(args.Data);
            process.ErrorDataReceived += (_, args) => Append(args.Data);
            State.Text = "SSH setup in progress. Keep ENET connected and power stable.";
            process.Start(); process.BeginOutputReadLine(); process.BeginErrorReadLine();
            await process.WaitForExitAsync();
            process.WaitForExit();
            State.Text = process.ExitCode == 0 ? "SSH tool finished. Review its root login result and restore coding as needed." : $"SSH tool stopped (exit {process.ExitCode}). Review the log before taking further action.";
        }
        catch (Exception ex) { State.Text = "SSH setup stopped: " + ex.Message; }
        finally { AcceptRisk.IsChecked = false; SetBusy(false); }
    }
    private async void InstallStore(object sender, RoutedEventArgs e)
    {
        SetBusy(true, preventClose: true);
        try
        {
            string gateway = await ResolveGatewayAsync();
            State.Text = "Reading the MGU address and vehicle identity…";
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var target = await MguDiagnostics.FindStoreTarget(gateway, timeout.Token);
            var appFolder = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
            var priorFolder = Path.Combine(Path.GetDirectoryName(appFolder) ?? appFolder, "MguSetup-fixed");
            string? identity = new[] { appFolder, priorFolder }
                .SelectMany(directory => new[] { "id_rsa.pkcs1.pem", "id_rsa" }
                    .Select(name => Path.Combine(directory, "tools", "mgu-ssh-cli", "data", "enrollments", target.Vin, name)))
                .FirstOrDefault(File.Exists);
            if (identity == null)
            {
                var picker = new OpenFileDialog { Title = "Select the root SSH private key for this MGU" };
                if (picker.ShowDialog(this) != true) { State.Text = "Store installation canceled."; return; }
                identity = picker.FileName;
            }
            if (MessageBox.Show(this, $"Install MGU Store on the head unit for VIN ending {target.Vin[^7..]}? Confirm that vehicle coding is restored and the parked car has stable power. Keep ENET and PC internet connected until setup finishes. The head unit will restart automatically if the installer requires it.",
                "Install MGU Store", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            { State.Text = "Store installation canceled."; return; }

            State.Text = "Installing MGU Store. Follow progress in the log.";
            var result = await StoreInstaller.InstallAsync(target.Host, identity, Append);
            if (result.ExitCode != 0)
                State.Text = $"Store installer stopped (exit {result.ExitCode}). Review the log.";
            else if (result.RestartError != null)
                State.Text = "Store installed. Automatic restart could not be confirmed. " + result.RestartError;
            else
                State.Text = result.RestartRequested
                    ? "Store installed. The head unit accepted the restart request."
                    : "Store installation complete.";
        }
        catch (Exception ex) { State.Text = "Store installation stopped. " + ex.Message; }
        finally { SetBusy(false); }
    }

    private void SetBusy(bool active, bool preventClose = false)
    {
        busy = active;
        this.preventClose = active && preventClose;
        StoreButton.IsEnabled = CheckButton.IsEnabled = AcceptRisk.IsEnabled =
            GatewayAddress.IsEnabled = Variant.IsEnabled = !active;
        SshButton.IsEnabled = !active && AcceptRisk.IsChecked == true && Variant.SelectedItem is System.Windows.Controls.ComboBoxItem { Tag: string };
    }
    private ProcessStartInfo CreateSshHelperStartInfo()
    {
        var info = new ProcessStartInfo(Path.Combine(folder!, "mgu-ssh-cli.exe"))
        {
            WorkingDirectory = folder,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8
        };
        return info;
    }

    private async Task<string> ResolveGatewayAsync()
    {
        string address = GatewayAddress.Text.Trim();
        if (address.Length != 0)
        {
            if (!System.Net.IPAddress.TryParse(address, out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                throw new IOException("Enter a valid ENET gateway IPv4 address or leave the field blank.");
            return ip.ToString();
        }

        State.Text = "Looking for the ENET gateway…";
        string gateway = await EnetDiscovery.FindGatewayAsync();
        Append($"Found ENET gateway at {gateway}.");
        return gateway;
    }

    private void Append(string? line)
    {
        if (line != null) Dispatcher.Invoke(() => { Log.AppendText(line + Environment.NewLine); Log.ScrollToEnd(); });
    }
}

