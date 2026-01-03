# SimpleEndpointBus

A minimal “endpointId + message” bus for LANs (and multi-subnet with optional peers), written in .NET 8.

## What you get

- **Broker** (`SimpleEndpointBus.Broker`): stores `endpointId -> callbackUrl`, resolves routes, forwards messages, retries until hold-time expires.
- **Client** (`SimpleEndpointBus.Client`): URL-free static API:
  - `EndpointBusClient.SendAsync(endpointId, message)`
  - `EndpointBusClient.RegisterAsync(endpointId, onMessageDelegate)`
- **SampleService**: registers an endpoint and prints received messages.
- **SampleSender**: sends a single message to an endpoint.

## Requirements

- .NET SDK 8.x

## Quick start (single subnet)

### 1) Run a broker
```bash
dotnet run --project src/SimpleEndpointBus.Broker --urls http://0.0.0.0:8080
```

**Recommended**: set the broker’s advertised URI so other machines can reach it:
```bash
SEB_BaseUri=http://10.1.1.50:8080 dotnet run --project src/SimpleEndpointBus.Broker --urls http://0.0.0.0:8080
```

### 2) Run the sample service (registers an endpoint)
```bash
ENDPOINT_ID=test.endpoint dotnet run --project src/SimpleEndpointBus.SampleService
```

### 3) Send a message (URL-free)
```bash
dotnet run --project src/SimpleEndpointBus.SampleSender -- test.endpoint "hello from sender"
```

## Multi-subnet (routers)

UDP multicast discovery typically **does not cross routers**.

For multi-subnet, run **one broker per subnet** and configure brokers to know each other:

On broker A:
```bash
SEB_BaseUri=http://10.1.1.50:8080 SEB_Peers=http://10.1.2.50:8080 dotnet run --project src/SimpleEndpointBus.Broker --urls http://0.0.0.0:8080
```

On broker B:
```bash
SEB_BaseUri=http://10.1.2.50:8080 SEB_Peers=http://10.1.1.50:8080 dotnet run --project src/SimpleEndpointBus.Broker --urls http://0.0.0.0:8080
```

Clients stay URL-free; if a client can’t reach multicast discovery on its network, you can set a fallback peer list:
```bash
SEB_Peers=http://10.1.1.50:8080;http://10.1.2.50:8080
```

## Publish (single-file executables)

Linux/macOS (bash):
```bash
bash scripts/publish-linux.sh linux-x64
```

Windows (PowerShell):
```powershell
.\scripts\publish-windows.ps1 -Rid win-x64
```

Outputs to `publish/`.

## Notes / Caveats

- The callback uses HTTP POST (REST).
- Registration is a TTL lease; the client auto-refreshes.
- Delivery retries with exponential backoff until `holdSeconds` expires.
- No durable storage, no auth by default (you can pass a simple `Secret` header per endpoint).


## Proxmox LXC (systemd)

See `ops/PROXMOX_LXC_SYSTEMD.md` and the templates in `ops/`.

## Windows usage (CMD and PowerShell)

All commands below assume you're running from the repo root.

### Run broker

**PowerShell**
```powershell
$env:SEB_BaseUri="http://10.1.1.50:8080"
dotnet run --project src\SimpleEndpointBus.Broker --urls http://0.0.0.0:8080
```

**CMD**
```bat
set SEB_BaseUri=http://10.1.1.50:8080
dotnet run --project src\SimpleEndpointBus.Broker --urls http://0.0.0.0:8080
```

### Run sample service (register endpoint)

**PowerShell**
```powershell
$env:ENDPOINT_ID="test.endpoint"
dotnet run --project src\SimpleEndpointBus.SampleService
```

**CMD**
```bat
set ENDPOINT_ID=test.endpoint
dotnet run --project src\SimpleEndpointBus.SampleService
```

### Send a message

**PowerShell**
```powershell
dotnet run --project src\SimpleEndpointBus.SampleSender -- test.endpoint "hello from windows"
```

**CMD**
```bat
dotnet run --project src\SimpleEndpointBus.SampleSender -- test.endpoint "hello from windows"
```

### Multi-subnet fallback on Windows (when multicast discovery doesn't work)

Set `SEB_Peers` (semicolon-separated list) so the client can find a broker without UDP multicast:

**PowerShell**
```powershell
$env:SEB_Peers="http://10.1.1.50:8080;http://10.1.2.50:8080"
dotnet run --project src\SimpleEndpointBus.SampleSender -- test.endpoint "hello"
```

**CMD**
```bat
set SEB_Peers=http://10.1.1.50:8080;http://10.1.2.50:8080
dotnet run --project src\SimpleEndpointBus.SampleSender -- test.endpoint "hello"
```

### Publish single-file executables (Windows)

**PowerShell**
```powershell
.\scripts\publish-windows.ps1 -Rid win-x64
```

Outputs:
- `publish\broker\win-x64\`
- `publish\sample-service\win-x64\`
- `publish\sample-sender\win-x64\`

Run the broker EXE:

**PowerShell**
```powershell
$env:SEB_BaseUri="http://10.1.1.50:8080"
.\publish\broker\win-x64\SimpleEndpointBus.Broker.exe
```

**CMD**
```bat
set SEB_BaseUri=http://10.1.1.50:8080
publish\broker\win-x64\SimpleEndpointBus.Broker.exe
```

### Windows firewall note

If UDP discovery doesn't work, Windows Firewall is the usual cause. Allow inbound UDP **37777**, or just use `SEB_Peers`.

## Proxmox: one-command LXC creation from GitHub artifacts

See `ops/PROXMOX_CREATE_LXC_FROM_GITHUB.md` and `ops/create-seb-broker-lxc-from-github.sh`.
