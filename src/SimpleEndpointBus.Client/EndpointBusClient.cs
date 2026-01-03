using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace SimpleEndpointBus.Client;

/// <summary>
/// URL-free endpoint bus client:
/// - First SendAsync discovers any broker on the LAN via UDP multicast.
/// - Subsequent sends reuse a cached broker for a short TTL.
/// - RegisterAsync hosts a tiny local HTTP callback listener and registers it with the broker.
/// </summary>
public static class EndpointBusClient
{
    // Defaults must match the broker
    private const string DefaultMulticastGroup = "239.255.77.77";
    private const int DefaultMulticastPort = 37777;

    // Cache discovered broker so most calls are just 1 HTTP POST
    private static readonly SemaphoreSlim BrokerLock = new(1, 1);
    private static string? _cachedBrokerBase;
    private static DateTimeOffset _cachedBrokerExpiresAt = DateTimeOffset.MinValue;

    // Shared HttpClient (safe + efficient)
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(2)
    };

    /// <summary>
    /// URL-free send: discovers a broker on first use.
    /// </summary>
    public static async Task SendAsync(string endpointId, string message, double holdSeconds = 15, string? traceId = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(endpointId)) throw new ArgumentException("endpointId required", nameof(endpointId));
        if (message is null) throw new ArgumentNullException(nameof(message));

        var broker = await GetBrokerBaseAsync(ct);

        // Try once; if transport fails, rediscover and try one more time.
        try
        {
            await SendViaBrokerAsync(broker, endpointId.Trim(), message, holdSeconds, traceId, ct);
        }
        catch (HttpRequestException)
        {
            InvalidateBrokerCache();
            var broker2 = await GetBrokerBaseAsync(ct);
            await SendViaBrokerAsync(broker2, endpointId.Trim(), message, holdSeconds, traceId, ct);
        }
    }

    /// <summary>
    /// Register endpoint with a delegate callback (no URL from the app).
    /// Starts a tiny local HTTP listener and registers its callback URL with the broker.
    /// Returns an IDisposable that stops the callback server + refresh loop when disposed.
    /// </summary>
    public static async Task<IDisposable> RegisterAsync(
        string endpointId,
        Func<EndpointMessage, Task> onMessage,
        RegisterOptions? options = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(endpointId)) throw new ArgumentException("endpointId required", nameof(endpointId));
        if (onMessage is null) throw new ArgumentNullException(nameof(onMessage));

        options ??= new RegisterOptions();

        // Start local callback server on an ephemeral port
        var callbackServer = await CallbackServer.StartAsync(onMessage, options, ct);

        // Discover broker and register callback URL
        var broker = await GetBrokerBaseAsync(ct);
        await RegisterWithBrokerAsync(broker, endpointId.Trim(), callbackServer.CallbackUrl, options, ct);

        // Keep registration alive
        var refresher = new RegistrationRefresher(
            brokerBase: broker,
            endpointId: endpointId.Trim(),
            callbackUrl: callbackServer.CallbackUrl,
            options: options);

        refresher.Start();

        return new CompositeDisposable(callbackServer, refresher);
    }

    // ------------------ Internal: HTTP ------------------

    private static async Task SendViaBrokerAsync(string brokerBase, string endpointId, string message, double holdSeconds, string? traceId, CancellationToken ct)
    {
        var req = new
        {
            endpointId,
            message,
            holdSeconds,
            traceId
        };

        using var resp = await Http.PostAsJsonAsync($"{brokerBase}/send", req, ct);
        if (resp.IsSuccessStatusCode) return;

        var body = await SafeReadBodyAsync(resp, ct);
        throw new InvalidOperationException($"Send failed ({(int)resp.StatusCode}): {body}");
    }

    private static async Task RegisterWithBrokerAsync(string brokerBase, string endpointId, string callbackUrl, RegisterOptions options, CancellationToken ct)
    {
        var req = new
        {
            endpointId,
            callbackUrl,
            ttlSeconds = options.TtlSeconds,
            secret = options.Secret
        };

        using var resp = await Http.PostAsJsonAsync($"{brokerBase}/register", req, ct);
        if (resp.IsSuccessStatusCode) return;

        var body = await SafeReadBodyAsync(resp, ct);
        throw new InvalidOperationException($"Register failed ({(int)resp.StatusCode}): {body}");
    }

    private static async Task<string> SafeReadBodyAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try { return await resp.Content.ReadAsStringAsync(ct); }
        catch { return "<no body>"; }
    }

    // ------------------ Internal: broker discovery ------------------

    private static void InvalidateBrokerCache()
    {
        _cachedBrokerBase = null;
        _cachedBrokerExpiresAt = DateTimeOffset.MinValue;
    }

    private static async Task<string> GetBrokerBaseAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        if (_cachedBrokerBase is not null && _cachedBrokerExpiresAt > now)
            return _cachedBrokerBase;

        await BrokerLock.WaitAsync(ct);
        try
        {
            now = DateTimeOffset.UtcNow;
            if (_cachedBrokerBase is not null && _cachedBrokerExpiresAt > now)
                return _cachedBrokerBase;

            // 1) UDP multicast discovery (preferred)
            var discovered = await DiscoverBrokerUdpAsync(ct);
            if (!string.IsNullOrWhiteSpace(discovered))
            {
                _cachedBrokerBase = discovered.TrimEnd('/');
                _cachedBrokerExpiresAt = DateTimeOffset.UtcNow.AddSeconds(30);
                return _cachedBrokerBase;
            }

            // 2) Cross-subnet fallback: SEB_Peers="http://10.1.2.10:8080;http://10.1.3.10:8080"
            var peers = (Environment.GetEnvironmentVariable("SEB_Peers") ?? "")
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            foreach (var p in peers)
            {
                var baseUri = p.TrimEnd('/');
                if (await IsHealthyAsync(baseUri, ct))
                {
                    _cachedBrokerBase = baseUri;
                    _cachedBrokerExpiresAt = DateTimeOffset.UtcNow.AddSeconds(30);
                    return _cachedBrokerBase;
                }
            }

            throw new InvalidOperationException(
                "No broker found. Ensure a broker is running on the LAN, or set SEB_Peers for cross-subnet discovery.");
        }
        finally
        {
            BrokerLock.Release();
        }
    }

    private static async Task<bool> IsHealthyAsync(string brokerBase, CancellationToken ct)
    {
        try
        {
            using var resp = await Http.GetAsync($"{brokerBase}/health", ct);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    private static async Task<string?> DiscoverBrokerUdpAsync(CancellationToken ct)
    {
        var group = Environment.GetEnvironmentVariable("SEB_MulticastGroup") ?? DefaultMulticastGroup;
        var portStr = Environment.GetEnvironmentVariable("SEB_MulticastPort");
        var port = int.TryParse(portStr, out var p) ? p : DefaultMulticastPort;

        // Send a discover request to multicast; brokers reply unicast with hello.
        var discover = new RouteAnnouncement { Type = "discover", Broker = "" };
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(discover));

        using var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.EnableBroadcast = true;

        // Bind to ephemeral port so broker can reply unicast
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));

        var multicastEp = new IPEndPoint(IPAddress.Parse(group), port);

        try
        {
            await udp.SendAsync(bytes, bytes.Length, multicastEp);
        }
        catch
        {
            return null;
        }

        // Wait briefly for responses
        var deadline = DateTime.UtcNow.AddMilliseconds(900);

        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) break;

            try
            {
                var receiveTask = udp.ReceiveAsync(ct).AsTask();
                var completed = await Task.WhenAny(receiveTask, Task.Delay(remaining, ct));
                if (completed != receiveTask) break;

                var res = await receiveTask;
                var json = Encoding.UTF8.GetString(res.Buffer);

                var ann = JsonSerializer.Deserialize<RouteAnnouncement>(json);
                if (ann?.Type == "hello" && !string.IsNullOrWhiteSpace(ann.Broker))
                    return ann.Broker.TrimEnd('/');
            }
            catch
            {
                // ignore and continue until deadline
            }
        }

        return null;
    }

    // ------------------ Models + helpers ------------------

    private sealed class CompositeDisposable : IDisposable
    {
        private readonly IDisposable[] _items;
        private int _disposed;
        public CompositeDisposable(params IDisposable[] items) => _items = items;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            foreach (var i in _items)
            {
                try { i.Dispose(); } catch { }
            }
        }
    }

    private sealed class RegistrationRefresher : IDisposable
    {
        private readonly string _brokerBase;
        private readonly string _endpointId;
        private readonly string _callbackUrl;
        private readonly RegisterOptions _options;

        private CancellationTokenSource? _cts;
        private Task? _loop;

        public RegistrationRefresher(string brokerBase, string endpointId, string callbackUrl, RegisterOptions options)
        {
            _brokerBase = brokerBase;
            _endpointId = endpointId;
            _callbackUrl = callbackUrl;
            _options = options;
        }

        public void Start()
        {
            _cts = new CancellationTokenSource();
            _loop = Task.Run(async () =>
            {
                var ct = _cts.Token;
                var period = TimeSpan.FromSeconds(Math.Max(10, _options.TtlSeconds * 0.6));

                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        await RegisterWithBrokerAsync(_brokerBase, _endpointId, _callbackUrl, _options, ct);
                    }
                    catch
                    {
                        // swallow; add logging hook if needed
                    }

                    try { await Task.Delay(period, ct); } catch { }
                }
            });
        }

        public void Dispose()
        {
            try { _cts?.Cancel(); } catch { }
            try { _cts?.Dispose(); } catch { }
        }
    }

    private sealed class RouteAnnouncement
    {
        public string Type { get; set; } = "hello"; // hello | discover
        public string Broker { get; set; } = "";
        public string? EndpointId { get; set; }
        public long ExpiresAtUnixMs { get; set; }
    }
}

/// <summary>
/// Message delivered to your callback delegate.
/// </summary>
public sealed record EndpointMessage(string EndpointId, string Message, string? TraceId);

/// <summary>
/// Options for registration + callback server.
/// </summary>
public sealed class RegisterOptions
{
    public double TtlSeconds { get; set; } = 120;
    public string? Secret { get; set; } = null;

    /// <summary>
    /// If autodetection picks the wrong interface, set this (e.g. "10.1.1.50")
    /// or set env var SEB_AdvertiseHost.
    /// </summary>
    public string? AdvertiseHost { get; set; } = Environment.GetEnvironmentVariable("SEB_AdvertiseHost");

    /// <summary>
    /// If you want a fixed listen port instead of ephemeral.
    /// </summary>
    public int? ListenPort { get; set; } = null;
}

/// <summary>
/// Tiny local HTTP server used for callbacks.
/// </summary>
internal sealed class CallbackServer : IDisposable
{
    private readonly WebApplication _app;
    private int _disposed;

    public string CallbackUrl { get; }

    private CallbackServer(WebApplication app, string callbackUrl)
    {
        _app = app;
        CallbackUrl = callbackUrl;
    }

    public static async Task<CallbackServer> StartAsync(Func<EndpointMessage, Task> onMessage, RegisterOptions options, CancellationToken ct)
    {
        var builder = WebApplication.CreateBuilder();

        // Choose port
        var port = options.ListenPort ?? GetFreeTcpPort();

        // Listen on all interfaces so broker can reach it
        builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

        var app = builder.Build();

        app.MapPost("/inbox", async (InboxPayload payload) =>
        {
            await onMessage(new EndpointMessage(payload.endpointId, payload.message, payload.traceId));
            return Results.Ok();
        });

        // Run Kestrel in background
        _ = app.StartAsync(ct);

        // What host should we advertise to the broker?
        var host = options.AdvertiseHost;
        if (string.IsNullOrWhiteSpace(host))
            host = NetUtil.GetLocalIPv4() ?? "127.0.0.1";

        var callbackUrl = $"http://{host}:{port}/inbox";

        // Give Kestrel a moment to bind
        await Task.Delay(50, ct);

        return new CallbackServer(app, callbackUrl);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _app.Lifetime.StopApplication(); } catch { }
    }

    private static int GetFreeTcpPort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private sealed record InboxPayload(string endpointId, string message, string? traceId);
}

internal static class NetUtil
{
    public static string? GetLocalIPv4()
    {
        foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;

            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily == AddressFamily.InterNetwork)
                {
                    var ip = ua.Address.ToString();
                    if (!ip.StartsWith("169.254.")) return ip;
                }
            }
        }
        return null;
    }
}
