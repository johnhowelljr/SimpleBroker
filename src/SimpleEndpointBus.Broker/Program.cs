using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

// Config
builder.Configuration.AddEnvironmentVariables(prefix: "SEB_");

// Services
builder.Services.AddSingleton<BrokerState>();
builder.Services.AddHttpClient("peers", c =>
{
    c.Timeout = TimeSpan.FromMilliseconds(800);
});
builder.Services.AddHostedService<UdpDiscoveryService>();
builder.Services.AddHostedService<RouteAnnounceService>();
builder.Services.AddHostedService<PeerSyncService>();
builder.Services.AddHostedService<DeliveryWorker>();

var app = builder.Build();

var state = app.Services.GetRequiredService<BrokerState>();
state.Initialize(app.Configuration);

// --- API ---

app.MapGet("/health", () => Results.Ok(new { ok = true, broker = state.SelfBaseUri }));

// Register a local endpoint at THIS broker (this broker becomes the “owner”)
app.MapPost("/register", (RegisterRequest req) =>
{
    if (string.IsNullOrWhiteSpace(req.EndpointId)) return Results.BadRequest("endpointId required");
    if (string.IsNullOrWhiteSpace(req.CallbackUrl)) return Results.BadRequest("callbackUrl required");

    if (!Uri.TryCreate(req.CallbackUrl, UriKind.Absolute, out var cb) ||
        (cb.Scheme != "http" && cb.Scheme != "https"))
        return Results.BadRequest("callbackUrl must be absolute http/https");

    var ttl = TimeSpan.FromSeconds(req.TtlSeconds <= 0 ? 120 : Math.Min(req.TtlSeconds, 3600));
    var expiresAt = DateTimeOffset.UtcNow.Add(ttl);

    var reg = new Registration(req.EndpointId.Trim(), cb.ToString(), expiresAt, req.Secret);

    state.LocalRegistrations[reg.EndpointId] = reg;

    // Update route cache to “this broker owns it”
    state.RouteCache[reg.EndpointId] = new RouteEntry(reg.EndpointId, state.SelfBaseUri, expiresAt);

    // Announce to LAN peers
    state.EnqueueAnnouncement(new RouteAnnouncement
    {
        Type = "route",
        Broker = state.SelfBaseUri,
        EndpointId = reg.EndpointId,
        ExpiresAtUnixMs = expiresAt.ToUnixTimeMilliseconds()
    });

    return Results.Ok(new { ok = true, owner = state.SelfBaseUri, expiresAt });
});

// Resolve route (who owns this endpointId?)
app.MapGet("/resolve/{endpointId}", (string endpointId) =>
{
    endpointId = endpointId.Trim();
    if (state.TryGetValidRoute(endpointId, out var route))
        return Results.Ok(route);

    return Results.NotFound();
});

// Used by peer sync (returns only “routes I own” i.e. local registrations)
app.MapGet("/routes", () =>
{
    var now = DateTimeOffset.UtcNow;
    var owned = state.LocalRegistrations.Values
        .Where(r => r.ExpiresAt > now)
        .Select(r => new RouteEntry(r.EndpointId, state.SelfBaseUri, r.ExpiresAt))
        .ToArray();

    return Results.Ok(owned);
});

// Send message to endpointId (can be any broker; broker resolves and forwards)
app.MapPost("/send", async (SendRequest req, IHttpClientFactory httpClientFactory) =>
{
    if (string.IsNullOrWhiteSpace(req.EndpointId)) return Results.BadRequest("endpointId required");
    if (req.Message is null) return Results.BadRequest("message required");

    var endpointId = req.EndpointId.Trim();
    var hold = TimeSpan.FromSeconds(req.HoldSeconds <= 0 ? 15 : Math.Min(req.HoldSeconds, 300));
    var expiresAt = DateTimeOffset.UtcNow.Add(hold);

    var owner = await state.ResolveOwnerAsync(endpointId, httpClientFactory);
    if (owner is null)
        return Results.NotFound($"endpoint '{endpointId}' not found");

    if (string.Equals(owner, state.SelfBaseUri, StringComparison.OrdinalIgnoreCase))
    {
        state.EnqueueDelivery(new PendingMessage(Guid.NewGuid().ToString("N"), endpointId, req.Message, expiresAt, 0, req.TraceId));
        return Results.Ok(new { ok = true, owner, queued = true });
    }
    else
    {
        // Forward to owning broker
        var client = httpClientFactory.CreateClient("peers");
        var fwd = new SendDirectRequest(endpointId, req.Message, hold.TotalSeconds, req.TraceId ?? Guid.NewGuid().ToString("N"));
        var url = $"{owner.TrimEnd('/')}/send-direct";

        try
        {
            using var resp = await client.PostAsJsonAsync(url, fwd);
            if (resp.IsSuccessStatusCode)
                return Results.Ok(new { ok = true, owner, forwarded = true });

            if (resp.StatusCode == HttpStatusCode.NotFound)
                return Results.NotFound($"endpoint '{endpointId}' not found at owner");

            return Results.StatusCode((int)resp.StatusCode);
        }
        catch (Exception ex)
        {
            return Results.Problem($"forward failed: {ex.Message}");
        }
    }
});

// Accept forwarded send ONLY if endpoint is locally registered here
app.MapPost("/send-direct", (SendDirectRequest req) =>
{
    if (string.IsNullOrWhiteSpace(req.EndpointId)) return Results.BadRequest("endpointId required");
    if (req.Message is null) return Results.BadRequest("message required");

    var endpointId = req.EndpointId.Trim();
    if (!state.TryGetValidLocalRegistration(endpointId, out _))
        return Results.NotFound();

    var hold = TimeSpan.FromSeconds(req.HoldSeconds <= 0 ? 15 : Math.Min(req.HoldSeconds, 300));
    var expiresAt = DateTimeOffset.UtcNow.Add(hold);

    state.EnqueueDelivery(new PendingMessage(Guid.NewGuid().ToString("N"), endpointId, req.Message, expiresAt, 0, req.TraceId));
    return Results.Ok(new { ok = true, queued = true });
});

app.Run();


// ---------------- Models ----------------

record RegisterRequest(string EndpointId, string CallbackUrl, double TtlSeconds = 120, string? Secret = null);
record SendRequest(string EndpointId, string Message, double HoldSeconds = 15, string? TraceId = null);
record SendDirectRequest(string EndpointId, string Message, double HoldSeconds, string TraceId);
record Registration(string EndpointId, string CallbackUrl, DateTimeOffset ExpiresAt, string? Secret);
record RouteEntry(string EndpointId, string Broker, DateTimeOffset ExpiresAt);
record PendingMessage(string Id, string EndpointId, string Message, DateTimeOffset ExpiresAt, int Attempts, string? TraceId = null);

// UDP announcements
class RouteAnnouncement
{
    public string Type { get; set; } = "hello"; // hello | route | discover
    public string Broker { get; set; } = "";
    public string? EndpointId { get; set; }
    public long ExpiresAtUnixMs { get; set; }
}

// ---------------- State ----------------

class BrokerState
{
    public string SelfBaseUri { get; private set; } = "";
    public string MulticastGroup { get; private set; } = "239.255.77.77";
    public int MulticastPort { get; private set; } = 37777;
    public TimeSpan HelloInterval { get; private set; } = TimeSpan.FromSeconds(5);
    public TimeSpan PeerSyncInterval { get; private set; } = TimeSpan.FromSeconds(10);

    public ConcurrentDictionary<string, Registration> LocalRegistrations { get; } = new();
    public ConcurrentDictionary<string, RouteEntry> RouteCache { get; } = new();
    public ConcurrentDictionary<string, DateTimeOffset> Peers { get; } = new(); // brokerUri -> lastSeen

    private readonly ConcurrentQueue<RouteAnnouncement> _announceQueue = new();
    private readonly ConcurrentQueue<PendingMessage> _deliveryQueue = new();

    public void Initialize(IConfiguration cfg)
    {
        MulticastGroup = cfg["MulticastGroup"] ?? MulticastGroup;
        MulticastPort = int.TryParse(cfg["MulticastPort"], out var p) ? p : MulticastPort;

        if (double.TryParse(cfg["HelloIntervalSeconds"], out var his)) HelloInterval = TimeSpan.FromSeconds(his);
        if (double.TryParse(cfg["PeerSyncIntervalSeconds"], out var ps)) PeerSyncInterval = TimeSpan.FromSeconds(ps);

        // Base URI
        SelfBaseUri = cfg["BaseUri"] ?? "";
        if (string.IsNullOrWhiteSpace(SelfBaseUri))
        {
            // Attempt to infer from ASPNETCORE_URLS + local LAN IP
            var urls = (cfg["ASPNETCORE_URLS"] ?? Environment.GetEnvironmentVariable("ASPNETCORE_URLS") ?? "http://0.0.0.0:8080")
                .Split(';', StringSplitOptions.RemoveEmptyEntries);

            var port = new Uri(urls[0].Replace("0.0.0.0", "127.0.0.1")).Port;
            var ip = NetUtil.GetLocalIPv4() ?? "127.0.0.1";
            SelfBaseUri = $"http://{ip}:{port}";
        }

        // Static peers (cross-subnet)
        var peers = (cfg["Peers"] ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var peer in peers)
            Peers.TryAdd(peer.TrimEnd('/'), DateTimeOffset.UtcNow);

        // Add self
        Peers[SelfBaseUri.TrimEnd('/')] = DateTimeOffset.UtcNow;
    }

    public void EnqueueAnnouncement(RouteAnnouncement a) => _announceQueue.Enqueue(a);
    public bool TryDequeueAnnouncement(out RouteAnnouncement a) => _announceQueue.TryDequeue(out a!);

    public void EnqueueDelivery(PendingMessage m) => _deliveryQueue.Enqueue(m);
    public bool TryDequeueDelivery(out PendingMessage m) => _deliveryQueue.TryDequeue(out m!);

    public bool TryGetValidRoute(string endpointId, out RouteEntry route)
    {
        if (RouteCache.TryGetValue(endpointId, out route!))
        {
            if (route.ExpiresAt > DateTimeOffset.UtcNow && !string.IsNullOrWhiteSpace(route.Broker))
                return true;

            RouteCache.TryRemove(endpointId, out _);
        }

        route = default!;
        return false;
    }

    public bool TryGetValidLocalRegistration(string endpointId, out Registration reg)
    {
        if (LocalRegistrations.TryGetValue(endpointId, out reg!))
        {
            if (reg.ExpiresAt > DateTimeOffset.UtcNow) return true;
            LocalRegistrations.TryRemove(endpointId, out _);
        }
        reg = default!;
        return false;
    }

    public async Task<string?> ResolveOwnerAsync(string endpointId, IHttpClientFactory httpClientFactory)
    {
        endpointId = endpointId.Trim();

        // 1) Cache
        if (TryGetValidRoute(endpointId, out var cached))
            return cached.Broker.TrimEnd('/');

        // 2) Ask peers
        var peers = Peers.Keys
            .Where(p => !string.Equals(p, SelfBaseUri, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (peers.Length == 0) return null;

        var client = httpClientFactory.CreateClient("peers");
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(900));

        static async Task<RouteEntry?> TryResolveFromPeerAsync(HttpClient client, string peer, string endpointId, CancellationToken ct)
        {
            try
            {
                var url = $"{peer.TrimEnd('/')}/resolve/{Uri.EscapeDataString(endpointId)}";
                var resp = await client.GetAsync(url, ct);
                if (!resp.IsSuccessStatusCode) return null;

                var route = await resp.Content.ReadFromJsonAsync<RouteEntry>(cancellationToken: ct);
                return route;
            }
            catch
            {
                return null;
            }
        }

        var tasks = peers
            .Select(peer => TryResolveFromPeerAsync(client, peer, endpointId, cts.Token))
            .ToList();

        while (tasks.Count > 0)
        {
            var finished = await Task.WhenAny(tasks);
            tasks.Remove(finished);

            var route = await finished;
            if (route is not null && route.ExpiresAt > DateTimeOffset.UtcNow)
            {
                RouteCache[endpointId] = route;
                return route.Broker.TrimEnd('/');
            }
        }

        return null;
    }
}

// ---------------- Hosted services ----------------

class UdpDiscoveryService : BackgroundService
{
    private readonly BrokerState _state;
    public UdpDiscoveryService(BrokerState state) => _state = state;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, _state.MulticastPort));
        udp.JoinMulticastGroup(IPAddress.Parse(_state.MulticastGroup));

        while (!stoppingToken.IsCancellationRequested)
        {
            UdpReceiveResult res;
            try { res = await udp.ReceiveAsync(stoppingToken); }
            catch { continue; }

            try
            {
                var json = Encoding.UTF8.GetString(res.Buffer);
                var ann = JsonSerializer.Deserialize<RouteAnnouncement>(json);
                if (ann is null) continue;

                // Active client discovery: reply unicast immediately
                if (ann.Type == "discover")
                {
                    var reply = new RouteAnnouncement { Type = "hello", Broker = _state.SelfBaseUri };
                    var jsonReply = JsonSerializer.Serialize(reply);
                    var bytesReply = Encoding.UTF8.GetBytes(jsonReply);

                    try { await udp.SendAsync(bytesReply, bytesReply.Length, res.RemoteEndPoint); }
                    catch { /* ignore */ }

                    continue;
                }

                var broker = ann.Broker?.TrimEnd('/');
                if (string.IsNullOrWhiteSpace(broker)) continue;

                _state.Peers[broker] = DateTimeOffset.UtcNow;

                if (ann.Type == "route" && !string.IsNullOrWhiteSpace(ann.EndpointId))
                {
                    var exp = DateTimeOffset.FromUnixTimeMilliseconds(ann.ExpiresAtUnixMs);
                    if (exp > DateTimeOffset.UtcNow)
                        _state.RouteCache[ann.EndpointId.Trim()] = new RouteEntry(ann.EndpointId.Trim(), broker, exp);
                }
            }
            catch
            {
                // ignore bad packets
            }
        }
    }
}

class RouteAnnounceService : BackgroundService
{
    private readonly BrokerState _state;
    public RouteAnnounceService(BrokerState state) => _state = state;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.MulticastLoopback = true;

        var groupEp = new IPEndPoint(IPAddress.Parse(_state.MulticastGroup), _state.MulticastPort);

        // initial hello
        _state.EnqueueAnnouncement(new RouteAnnouncement { Type = "hello", Broker = _state.SelfBaseUri });

        while (!stoppingToken.IsCancellationRequested)
        {
            // Drain announcements quickly
            while (_state.TryDequeueAnnouncement(out var ann))
            {
                try
                {
                    var json = JsonSerializer.Serialize(ann);
                    var bytes = Encoding.UTF8.GetBytes(json);
                    await udp.SendAsync(bytes, bytes.Length, groupEp);
                }
                catch { }
            }

            // periodic hello
            _state.EnqueueAnnouncement(new RouteAnnouncement { Type = "hello", Broker = _state.SelfBaseUri });
            await Task.Delay(_state.HelloInterval, stoppingToken);
        }
    }
}

class PeerSyncService : BackgroundService
{
    private readonly BrokerState _state;
    private readonly IHttpClientFactory _httpClientFactory;

    public PeerSyncService(BrokerState state, IHttpClientFactory httpClientFactory)
    {
        _state = state;
        _httpClientFactory = httpClientFactory;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var client = _httpClientFactory.CreateClient("peers");

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(_state.PeerSyncInterval, stoppingToken);

            var peers = _state.Peers.Keys
                .Where(p => !string.Equals(p, _state.SelfBaseUri, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            foreach (var peer in peers)
            {
                try
                {
                    var url = $"{peer.TrimEnd('/')}/routes";
                    var resp = await client.GetAsync(url, stoppingToken);
                    if (!resp.IsSuccessStatusCode) continue;

                    var routes = await resp.Content.ReadFromJsonAsync<RouteEntry[]>(cancellationToken: stoppingToken);
                    if (routes is null) continue;

                    var now = DateTimeOffset.UtcNow;
                    foreach (var r in routes)
                    {
                        if (r.ExpiresAt <= now) continue;
                        _state.RouteCache[r.EndpointId] = r;
                    }
                }
                catch
                {
                    // ignore peer errors
                }
            }

            // Cleanup expired cache
            var now2 = DateTimeOffset.UtcNow;
            foreach (var kv in _state.RouteCache)
            {
                if (kv.Value.ExpiresAt <= now2)
                    _state.RouteCache.TryRemove(kv.Key, out _);
            }
        }
    }
}

class DeliveryWorker : BackgroundService
{
    private readonly BrokerState _state;
    private readonly IHttpClientFactory _httpClientFactory;

    public DeliveryWorker(BrokerState state, IHttpClientFactory httpClientFactory)
    {
        _state = state;
        _httpClientFactory = httpClientFactory;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var client = _httpClientFactory.CreateClient("peers");

        while (!stoppingToken.IsCancellationRequested)
        {
            if (!_state.TryDequeueDelivery(out var msg))
            {
                await Task.Delay(50, stoppingToken);
                continue;
            }

            if (msg.ExpiresAt <= DateTimeOffset.UtcNow)
                continue;

            if (!_state.TryGetValidLocalRegistration(msg.EndpointId, out var reg))
                continue; // no longer registered

            var ok = await TryDeliverAsync(client, reg, msg, stoppingToken);
            if (ok) continue;

            // Retry with backoff until expiry
            var nextAttempts = msg.Attempts + 1;
            var delaySeconds = Math.Min(Math.Pow(2, nextAttempts), 10); // 2,4,8,10...
            var next = msg with { Attempts = nextAttempts };

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(delaySeconds), stoppingToken);
                    if (next.ExpiresAt > DateTimeOffset.UtcNow)
                        _state.EnqueueDelivery(next);
                }
                catch { }
            }, stoppingToken);
        }
    }

    private static async Task<bool> TryDeliverAsync(HttpClient client, Registration reg, PendingMessage msg, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, reg.CallbackUrl);
            req.Headers.TryAddWithoutValidation("X-Endpoint-Id", msg.EndpointId);
            if (!string.IsNullOrWhiteSpace(msg.TraceId))
                req.Headers.TryAddWithoutValidation("X-Trace-Id", msg.TraceId);

            if (!string.IsNullOrWhiteSpace(reg.Secret))
                req.Headers.TryAddWithoutValidation("X-Endpoint-Secret", reg.Secret);

            var payload = new { endpointId = msg.EndpointId, message = msg.Message, traceId = msg.TraceId };
            req.Content = JsonContent.Create(payload);

            using var resp = await client.SendAsync(req, ct);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }
}

// ---------------- Utilities ----------------

static class NetUtil
{
    public static string? GetLocalIPv4()
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

            var ipProps = ni.GetIPProperties();
            foreach (var ua in ipProps.UnicastAddresses)
            {
                if (ua.Address.AddressFamily == AddressFamily.InterNetwork)
                {
                    var ip = ua.Address.ToString();
                    if (!ip.StartsWith("169.254.")) // skip APIPA
                        return ip;
                }
            }
        }
        return null;
    }
}
