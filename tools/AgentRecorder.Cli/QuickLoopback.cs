using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace AgentRecorder.Cli;

internal static partial class Program
{
    // Each new connection is checked before HTTP can put a key or request on it.
    // The caller keeps the verified process handle open for this client's lifetime.
    internal static HttpClient CreateQuickClient(int port, int pid, Func<bool> processAlive, bool singleConnection = false)
    {
        int connections = 0;
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            UseCookies = false,
            PooledConnectionLifetime = TimeSpan.Zero,
            ConnectCallback = async (context, token) =>
            {
                if (singleConnection && Interlocked.Increment(ref connections) != 1)
                    throw new IOException("Quick submission transport cannot reconnect.");
                if (context.DnsEndPoint.Host != "127.0.0.1" || context.DnsEndPoint.Port != port || !processAlive())
                    throw new IOException("The bound loopback host is unavailable.");
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(IPAddress.Loopback, port, token).ConfigureAwait(false);
                    var local = (IPEndPoint)socket.LocalEndPoint!;
                    if (!PeerOwnedBy(local.Port, port, pid) || !processAlive())
                        throw new IOException("The loopback connection is not owned by the bound host.");
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
        };
        return new HttpClient(handler)
        {
            BaseAddress = new Uri($"http://127.0.0.1:{port}/api/v1/"),
            Timeout = Timeout.InfiniteTimeSpan,
            MaxResponseContentBufferSize = 256 * 1024,
            DefaultRequestVersion = HttpVersion.Version11,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact
        };
    }

    private static bool PeerOwnedBy(int clientPort, int serverPort, int pid)
    {
        const uint insufficientBuffer = 122, ipv4 = 2, ownerPidConnections = 4;
        int size = 0;
        if (GetExtendedTcpTable(null, ref size, false, ipv4, ownerPidConnections, 0) != insufficientBuffer)
            return false;
        for (int attempt = 0; attempt < 3 && size is >= 4 and <= 4 * 1024 * 1024; attempt++)
        {
            var buffer = new byte[size];
            uint result = GetExtendedTcpTable(buffer, ref size, false, ipv4, ownerPidConnections, 0);
            if (result == insufficientBuffer) continue;
            if (result != 0 || size < 4 || size > buffer.Length) return false;
            int rows = BinaryPrimitives.ReadInt32LittleEndian(buffer);
            const int rowBytes = 24;
            if (rows < 0 || rows > (size - 4) / rowBytes) return false;
            for (int i = 0; i < rows; i++)
            {
                var row = buffer.AsSpan(4 + i * rowBytes, rowBytes);
                const uint established = 5, loopback = 0x0100007f;
                if (BinaryPrimitives.ReadUInt32LittleEndian(row) == established &&
                    BinaryPrimitives.ReadUInt32LittleEndian(row[4..]) == loopback &&
                    BinaryPrimitives.ReadUInt16BigEndian(row[8..]) == serverPort &&
                    BinaryPrimitives.ReadUInt32LittleEndian(row[12..]) == loopback &&
                    BinaryPrimitives.ReadUInt16BigEndian(row[16..]) == clientPort &&
                    BinaryPrimitives.ReadInt32LittleEndian(row[20..]) == pid) return true;
            }
            return false;
        }
        return false;
    }

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern uint GetExtendedTcpTable([Out] byte[]? table, ref int size,
        [MarshalAs(UnmanagedType.Bool)] bool sort, uint family, uint tableClass, uint reserved);
}
