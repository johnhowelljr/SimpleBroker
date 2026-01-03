Param(
  [string]$Rid = "win-x64"
)

$ErrorActionPreference = "Stop"

$Root = Split-Path -Parent $PSScriptRoot
$Out  = Join-Path $Root "publish"

if (Test-Path $Out) { Remove-Item -Recurse -Force $Out | Out-Null }
New-Item -ItemType Directory -Path $Out | Out-Null

function Publish-One($Proj, $Name) {
  Write-Host "Publishing $Name ($Rid)..."
  dotnet publish (Join-Path $Root $Proj) -c Release -r $Rid --self-contained true `
    -p:PublishSingleFile=true -p:PublishTrimmed=false `
    -o (Join-Path $Out "$Name\$Rid")
}

Publish-One "src\SimpleEndpointBus.Broker\SimpleEndpointBus.Broker.csproj" "broker"
Publish-One "src\SimpleEndpointBus.SampleService\SimpleEndpointBus.SampleService.csproj" "sample-service"
Publish-One "src\SimpleEndpointBus.SampleSender\SimpleEndpointBus.SampleSender.csproj" "sample-sender"

Write-Host ""
Write-Host "Done."
Write-Host "Output: $Out"
