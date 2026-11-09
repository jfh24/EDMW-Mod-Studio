using System.Net;
using System.Net.Sockets;
using System.Text;

namespace EDMWMapDesigner.Networking;

/// <summary>Opt-in plain text metadata log for EDMW gameplay routes. Packet contents are never written.</summary>
internal static class OnlineTrafficLogger
{
    private const long MaximumLogBytes = 5 * 1024 * 1024;
    private static readonly object Gate = new();
    private static bool _enabled;
    private static string _serverDescription = "unresolved";

    public static string LogPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EDMW Map Designer", "logs", "network-traffic.log");

    public static bool IsEnabled { get { lock (Gate) return _enabled; } }

    /// <returns>An error message if the log could not be configured.</returns>
    public static string? Configure(bool enabled)
    {
        lock (Gate)
        {
            if (!enabled)
            {
                if (_enabled)
                    try { AppendLocked("event=logging_disabled"); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { }
                _enabled = false;
                return null;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                _enabled = true;
                _serverDescription = "unresolved";
                AppendLocked($"event=logging_enabled file=\"{LogPath}\" scope=EDMW_gameplay_metadata packet_contents=excluded");
                return null;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
            {
                _enabled = false;
                return $"Could not create the network traffic log at {LogPath}: {error.Message}";
            }
        }
    }

    public static async Task RecordRelayServerAsync(Uri endpoint, CancellationToken cancellationToken = default)
    {
        if (!IsEnabled) return;
        string description;
        try
        {
            var addresses = IPAddress.TryParse(endpoint.DnsSafeHost, out var literal)
                ? [literal]
                : await Dns.GetHostAddressesAsync(endpoint.DnsSafeHost, cancellationToken).ConfigureAwait(false);
            var serverAddresses = addresses.Where(address => address.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
                .Distinct().ToArray();
            var serverPort = endpoint.IsDefaultPort ? (endpoint.Scheme == "wss" ? 443 : 80) : endpoint.Port;
            var localRoute = serverAddresses.Select(address => FindLocalRouteAddress(address, serverPort))
                .FirstOrDefault(address => address is not null) ?? "unavailable";
            description = $"endpoint={endpoint.Host}:{serverPort} dns_ips={FormatAddresses(serverAddresses)} local_route_ip={localRoute}";
        }
        catch (Exception error) when (error is SocketException or OperationCanceledException or ArgumentException)
        {
            description = $"endpoint={endpoint.Host}:{endpoint.Port} dns_ips=unavailable local_route_ip=unavailable resolution_error=\"{Clean(error.Message)}\"";
        }

        lock (Gate)
        {
            if (!_enabled) return;
            _serverDescription = description;
            try { AppendLocked($"event=relay_server transport=WSS {description}"); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
            { _enabled = false; }
        }
    }

    public static void LogGameplay(string transport, string protocol, string direction, string from, string to,
        int bytes, string? peerId = null, string? iceRoute = null)
    {
        lock (Gate)
        {
            if (!_enabled) return;
            var route = transport == "WSS" ? $" server[{_serverDescription}]" : string.IsNullOrWhiteSpace(iceRoute) ? "" : $" ice[{Clean(iceRoute)}]";
            var peer = string.IsNullOrWhiteSpace(peerId) ? "" : $" peer={Clean(peerId)}";
            try { AppendLocked($"transport={Clean(transport)} protocol={Clean(protocol)} direction={Clean(direction)} from={Clean(from)} to={Clean(to)} bytes={Math.Max(0, bytes)}{peer}{route}"); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
            { _enabled = false; }
        }
    }

    private static string? FindLocalRouteAddress(IPAddress remoteAddress, int remotePort)
    {
        try
        {
            using var socket = new Socket(remoteAddress.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect(new IPEndPoint(remoteAddress, remotePort));
            return (socket.LocalEndPoint as IPEndPoint)?.Address.ToString();
        }
        catch (Exception error) when (error is SocketException or ObjectDisposedException or ArgumentException)
        { return null; }
    }

    private static string FormatAddresses(IEnumerable<IPAddress> addresses)
    {
        var values = addresses.Select(FormatAddress).ToArray();
        return values.Length == 0 ? "unavailable" : string.Join(",", values);
    }

    private static string FormatAddress(IPAddress address) => address.AddressFamily == AddressFamily.InterNetworkV6
        ? $"[{address}]" : address.ToString();

    private static string Clean(string value) => value.Replace('\r', ' ').Replace('\n', ' ').Replace('"', '\'');

    private static void AppendLocked(string message)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
        var line = $"{DateTimeOffset.UtcNow:O} {message}{Environment.NewLine}";
        if (File.Exists(LogPath) && new FileInfo(LogPath).Length + Encoding.UTF8.GetByteCount(line) > MaximumLogBytes)
            File.Move(LogPath, LogPath + ".1", true);
        File.AppendAllText(LogPath, line, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }
}
