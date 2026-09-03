using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BitchatWin.Services;

/// <summary>
/// UDP broadcast discovery for the local relay, so nobody has to read an IP
/// address aloud. The host announces itself a few times a minute; everyone else
/// listens and learns the relay URL.
///
/// Broadcast rather than mDNS on purpose: it needs no service dependency, works
/// on a bare Windows hotspot, and the payload is a fixed short string.
/// </summary>
public sealed class LanBeacon : IAsyncDisposable
{
    private const int DiscoveryPort = 8788;
    private const string Magic = "BITCHATWIN1";
    private static readonly TimeSpan AnnounceInterval = TimeSpan.FromSeconds(3);

    /// <summary>Raised when a relay is heard from. Argument is a ws:// URL.</summary>
    public event Action<string>? RelayDiscovered;

    public event Action<string>? Log;

    private CancellationTokenSource? _announceCts;
    private CancellationTokenSource? _listenCts;
    private Task? _announceTask;
    private Task? _listenTask;

    private string? _lastDiscovered;

    /// <summary>Starts announcing a relay listening on <paramref name="relayPort"/>.</summary>
    public void StartAnnouncing(int relayPort)
    {
        StopAnnouncing();

        var cts = new CancellationTokenSource();
        _announceCts = cts;

        _announceTask = Task.Run(async () =>
        {
            using var udp = new UdpClient { EnableBroadcast = true };
            var endpoint = new IPEndPoint(IPAddress.Broadcast, DiscoveryPort);
            byte[] payload = Encoding.UTF8.GetBytes($"{Magic}|{relayPort}");

            while (!cts.IsCancellationRequested)
            {
                try
                {
                    await udp.SendAsync(payload, payload.Length, endpoint).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log?.Invoke("annuncio non inviato: " + ex.Message);
                }

                try
                {
                    await Task.Delay(AnnounceInterval, cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }, cts.Token);

        Log?.Invoke($"annuncio LAN attivo (porta relay {relayPort})");
    }

    public void StopAnnouncing()
    {
        _announceCts?.Cancel();
        _announceCts?.Dispose();
        _announceCts = null;
        _announceTask = null;
    }

    /// <summary>Listens for relay announcements from other machines.</summary>
    public void StartListening()
    {
        if (_listenCts is not null) return;

        var cts = new CancellationTokenSource();
        _listenCts = cts;

        _listenTask = Task.Run(async () =>
        {
            UdpClient udp;
            try
            {
                udp = new UdpClient(AddressFamily.InterNetwork);
                // Another instance on this machine may already be bound; sharing
                // the port lets host and guest run side by side for testing.
                udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                udp.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));
            }
            catch (Exception ex)
            {
                Log?.Invoke("ascolto LAN non avviato: " + ex.Message);
                return;
            }

            using (udp)
            {
                while (!cts.IsCancellationRequested)
                {
                    UdpReceiveResult result;
                    try
                    {
                        result = await udp.ReceiveAsync(cts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { return; }
                    catch (SocketException) { continue; }

                    string text = Encoding.UTF8.GetString(result.Buffer);
                    string[] parts = text.Split('|');
                    if (parts.Length != 2 || parts[0] != Magic) continue;
                    if (!int.TryParse(parts[1], out int port) || port is < 1 or > 65535) continue;

                    string url = $"ws://{result.RemoteEndPoint.Address}:{port}";
                    if (url == _lastDiscovered) continue;

                    _lastDiscovered = url;
                    Log?.Invoke($"relay locale trovato: {url}");
                    RelayDiscovered?.Invoke(url);
                }
            }
        }, cts.Token);
    }

    public void StopListening()
    {
        _listenCts?.Cancel();
        _listenCts?.Dispose();
        _listenCts = null;
        _listenTask = null;
        _lastDiscovered = null;
    }

    public ValueTask DisposeAsync()
    {
        StopAnnouncing();
        StopListening();
        return ValueTask.CompletedTask;
    }
}
