using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace MguSetup;

internal static class EnetDiscovery
{
    private const int Port = 6811;
    private static readonly byte[] Probe = [0, 0, 0, 0, 0, 0x11];

    internal static async Task<string> FindGatewayAsync()
    {
        using var socket = new UdpClient(new IPEndPoint(IPAddress.Any, 0)) { EnableBroadcast = true };
        var targets = new HashSet<IPAddress> { IPAddress.Broadcast, IPAddress.Parse("169.254.255.255") };
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up || adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                continue;
            foreach (var address in adapter.GetIPProperties().UnicastAddresses)
            {
                if (address.Address.AddressFamily != AddressFamily.InterNetwork || address.PrefixLength >= 31)
                    continue;
                var bytes = address.Address.GetAddressBytes();
                var mask = address.IPv4Mask.GetAddressBytes();
                for (int i = 0; i < bytes.Length; i++)
                    bytes[i] |= (byte)~mask[i];
                targets.Add(new IPAddress(bytes));
            }
        }
        foreach (var target in targets)
        {
            try { await socket.SendAsync(Probe, new IPEndPoint(target, Port)); }
            catch (SocketException) { /* Other interfaces may still reach ENET. */ }
        }

        var gateways = new HashSet<IPAddress>();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!deadline.IsCancellationRequested)
        {
            UdpReceiveResult reply;
            try { reply = await socket.ReceiveAsync(deadline.Token); }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested) { break; }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset) { continue; }
            string data = Encoding.ASCII.GetString(reply.Buffer).Replace("\0", "");
            if (data.Contains("BMWVIN", StringComparison.Ordinal))
                gateways.Add(reply.RemoteEndPoint.Address);
        }

        if (gateways.Count == 1)
            return gateways.Single().ToString();
        if (gateways.Count > 1)
            throw new IOException($"Multiple ENET gateways responded ({string.Join(", ", gateways)}). Enter the intended gateway address.");
        throw new IOException("No ENET gateway responded. Check the cable and wake the car, or enter the gateway address manually.");
    }
}
