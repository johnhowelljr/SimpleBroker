# Windows instructions

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
