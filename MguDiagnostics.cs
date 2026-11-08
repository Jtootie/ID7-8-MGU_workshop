using System.Buffers.Binary;
using System.IO;
using System.Net.Sockets;
using System.Text;

namespace MguSetup;

internal static class MguDiagnostics
{
    private sealed class Link : IDisposable
    {
        private readonly TcpClient tcp = new();
        internal Task Connect(string gateway, CancellationToken ct) => tcp.ConnectAsync(gateway, 6801, ct).AsTask();
        private async Task<byte[]> ReadDid(byte target, ushort did, CancellationToken ct)
        {
            byte[] uds = [0x22, (byte)(did >> 8), (byte)did];
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(12));
            var token = deadline.Token;
            var stream = tcp.GetStream();
            byte[] frame = new byte[uds.Length + 8];
            BinaryPrimitives.WriteInt32BigEndian(frame, uds.Length + 2);
            frame[5] = 1; frame[6] = 0xf4; frame[7] = target; uds.CopyTo(frame, 8);
            await stream.WriteAsync(frame, token);
            while (true)
            {
                byte[] header = new byte[6]; await stream.ReadExactlyAsync(header, token);
                int n = BinaryPrimitives.ReadInt32BigEndian(header);
                if (n < 2 || n > 65536) throw new IOException("Invalid ENET frame.");
                byte[] body = new byte[n]; await stream.ReadExactlyAsync(body, token);
                if (header[4] != 0 || header[5] != 1 || body[0] != target || body[1] != 0xf4) continue;
                byte[] reply = body[2..];
                if (reply.Length == 3 && reply[0] == 0x7f && reply[1] == uds[0] && reply[2] == 0x78) continue;
                if (reply.Length == 0 || reply[0] != uds[0] + 0x40) throw new IOException($"MGU rejected service {uds[0]:X2}: {Convert.ToHexString(reply.AsSpan(0, Math.Min(3, reply.Length)))}.");
                return reply;
            }
        }
        internal async Task<byte[]> Read(byte target, ushort did, CancellationToken ct)
        {
            var reply = await ReadDid(target, did, ct);
            if (reply.Length < 4 || reply[1] != (byte)(did >> 8) || reply[2] != (byte)did) throw new IOException($"Unexpected response for {did:X4}.");
            return reply[3..];
        }
        public void Dispose() => tcp.Dispose();
    }

    private static void RequireCafd(byte[] identity)
    {
        // SVK entries are eight bytes and end at the end of the DID payload.
        for (int offset = identity.Length % 8; offset + 8 <= identity.Length; offset += 8)
        {
            var entry = identity.AsSpan(offset, 8);
            if (entry[0] == 0x05 && BinaryPrimitives.ReadUInt32BigEndian(entry[1..5]) == uint.MaxValue)
                throw new IOException("The MGU reports a missing CAFD (FFFFFFFF). Restore the CAFD and coding with your external coding tool before continuing.");
        }
    }

    internal static async Task<(string Vin, string Host)> FindStoreTarget(string gateway, CancellationToken ct)
    {
        using var link = new Link();
        await link.Connect(gateway, ct);
        string vin = Encoding.ASCII.GetString(await link.Read(0x10, 0xf190, ct));
        var identity = await link.Read(0x63, 0xf101, ct);
        if (vin.Length != 17 || !vin.All(char.IsAsciiLetterOrDigit) || identity.AsSpan().IndexOf(Convert.FromHexString("0600003F36")) < 0)
            throw new IOException("Could not confirm the vehicle VIN and MGU identity.");
        RequireCafd(identity);
        var address = await link.Read(0x63, 0x172a, ct);
        if (address.Length < 13 || address[0] != 0 || address[1] != 169 || address[2] != 254)
            throw new IOException("Could not read the link-local MGU address from the ENET gateway.");
        return (vin, new System.Net.IPAddress(address.AsSpan(1, 4)).ToString());
    }

    internal static async Task<string> CheckBeforeSshSetup(string gateway, CancellationToken ct)
    {
        using var link = new Link();
        await link.Connect(gateway, ct);
        string vin = Encoding.ASCII.GetString(await link.Read(0x10, 0xf190, ct));
        var identity = await link.Read(0x63, 0xf101, ct);
        if (vin.Length != 17 || !vin.All(char.IsAsciiLetterOrDigit) || identity.AsSpan().IndexOf(Convert.FromHexString("0600003F36")) < 0)
            throw new IOException("Could not verify vehicle VIN and supported MGU bootloader.");
        var addresses = await link.Read(0x63, 0x172a, ct);
        if (addresses.Length >= 13 && addresses[0] == 0)
        {
            var enet = Enumerable.Range(0, 3).Select(i => addresses.AsSpan(1 + i * 4, 4).ToArray()).FirstOrDefault(ip => ip[0] == 169 && ip[1] == 254);
            if (enet != null)
            {
                using var probe = new TcpClient();
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
                limit.CancelAfter(TimeSpan.FromSeconds(3));
                try { await probe.ConnectAsync(new System.Net.IPAddress(enet), 22, limit.Token); }
                catch (SocketException) { return vin; }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return vin; }
                throw new IOException("The MGU SSH port is already open. First-time flashing was skipped.");
            }
        }
        return vin;
    }
}
