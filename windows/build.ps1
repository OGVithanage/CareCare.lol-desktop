param([string]$Output = "$PSScriptRoot\publish")
$ErrorActionPreference = 'Stop'
cmake -S "$PSScriptRoot\native" -B "$PSScriptRoot\native\build" -A x64
if ($LASTEXITCODE -ne 0) { throw 'CMake configure failed' }
cmake --build "$PSScriptRoot\native\build" --config Release
if ($LASTEXITCODE -ne 0) { throw 'Native build failed' }
dotnet publish "$PSScriptRoot\service\CareCare.Service.csproj" -c Release -r win-x64 --self-contained true -o $Output
if ($LASTEXITCODE -ne 0) { throw 'Service publish failed' }
Copy-Item "$PSScriptRoot\native\build\Release\CareCareWfp.dll" $Output -Force

Copy-Item "$PSScriptRoot\install.ps1", "$PSScriptRoot\uninstall.ps1", "$PSScriptRoot\browser-policy.ps1" $Output -Force
@{ protocolVersion = 2; serviceVersion = '0.2.0'; proxyPort = 17843 } | ConvertTo-Json | Set-Content "$Output\service-manifest.json" -Encoding Ascii
