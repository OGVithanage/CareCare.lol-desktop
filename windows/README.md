# Windows allowlist service

Implements [the report](../reports/windows-website-allowlist-plan.md) with a .NET 8 Windows worker and a small Windows SDK C++ DLL. Electron never runs elevated commands. The service runs as LocalSystem, receives full lists through `\\.\pipe\CareCare.Allowlist.v1`, validates domains independently, and commits WFP updates transactionally.

## Build and install (Windows x64)

Prerequisites: .NET 8 SDK, CMake 3.24+, and Visual Studio 2022 Build Tools with Desktop development with C++ and a Windows SDK. Build before enabling filtering, while dependency downloads still work:

```powershell
.\windows\build.ps1
dotnet run --project .\windows\tests\CareCare.PolicyTests.csproj
```

Use an elevated PowerShell for installation. Specify the Windows account that should control the list (for example `PCNAME\Parent`). Do not select a child's account if it should not be able to change policy.

```powershell
.\windows\install.ps1 -ControllerAccount 'PCNAME\Parent'
```

Installation immediately enables machine-wide outbound deny by default. Start with a disposable Windows VM and a local console. An empty list blocks outbound network connections except DNS to configured resolvers, loopback, and DHCP. This includes remote administration, VPNs, updates, and other non-browser apps. No blanket LAN exception is installed. Existing Windows Firewall policy can still block allowed addresses.

Open Electron as the configured controller account. Every addition/removal saves to `electron-store` and sends the full list; the UI reports the service result. The retry button resends the list after a connection failure. On non-Windows systems, the app explicitly reports local-only persistence. A successful policy update can include unresolved domains: these remain blocked and are listed in the status message.

## Persistence and recovery

The installer restricts binaries under `%ProgramFiles%\CareCare` and service state under `%ProgramData%\CareCare` to SYSTEM and administrators. `service.json` contains the authorized controller SID; `allowlist.json` contains the last requested normalized domains. The pipe denies network logons and permits only SYSTEM, administrators, and the controller SID. Any process running as that account can update policy; there is no separate parent authentication flow.

The service starts automatically with Windows after BFE. Persistent provider, sublayer, and filters survive service stop/crash and reload with BFE. At service startup, the worker installs an empty baseline before reading saved domains and resolving fresh IPs. Corrupt state leaves that baseline in place and logs an error. SCM restarts the service after failures. This is not a boot-time driver: it does not protect the interval before BFE loads persistent filters.

Stopping the service intentionally does **not** unblock the machine. To remove CareCare policy and unregister the service, run from an elevated local PowerShell:

```powershell
.\windows\uninstall.ps1
```

Emergency policy removal, if the source scripts are unavailable:

```powershell
Stop-Service CareCareAllowlist
& "$env:ProgramFiles\CareCare\CareCare.Service.exe" --remove-policy
```

Only CareCare's two ALE layers, provider, and sublayer are removed. The uninstall script retains files and saved domains. Restore missing executable/DLL files from the published build before emergency removal. Administrative users can disable this control; it is not tamper-proof.

## Filtering and DNS details

- A dedicated sublayer contains persistent default-deny filters at `ALE_AUTH_CONNECT_V4` and `V6`. Address permits have higher weight in that same sublayer. Both TCP and UDP (including QUIC) are covered, along with other outbound protocols classified at ALE.
- An update deletes old filters and adds replacement filters in one WFP transaction. WFP filter additions/removals cause existing flows to reauthorize on their next packet, including traffic arriving over an established connection. No TCP-only socket reset workaround is used. See [Microsoft ALE reauthorization](https://learn.microsoft.com/en-us/windows/win32/fwp/ale-re-authorization) and [filter arbitration](https://learn.microsoft.com/en-us/windows/win32/fwp/filter-arbitration).
- DNS uses the Windows system resolver through `DnsQuery_W`, querying A and AAAA records and using the minimum answer/CNAME TTL. A one-second scheduler removes expired entries before resolving replacements; lookup failures do not retain expired IPs. Zero TTLs use a one-second minimum. Refreshes and user updates are serialized, so long DNS batches can delay subsequent refreshes. TTL refresh is best effort, not a hard real-time expiry guarantee.
- DNS server exceptions follow active adapters and allow only TCP/UDP destination port 53. Adapter changes are detected by the refresh loop. Windows DNS-over-HTTPS-only configurations may require system DNS changes: arbitrary HTTPS resolver exceptions are not installed. See [Microsoft DnsQuery documentation](https://learn.microsoft.com/en-us/windows/win32/api/windns/nf-windns-dnsquery_w).
- A failed WFP transaction retains the previous committed rules. The UI reports failure; the service's saved desired state and Electron's desired list can temporarily differ from active enforcement. Retry after fixing the service error. Background refresh failures restart the service. While the service is stopped, existing persistent address permits do not expire.
- Each entry resolves both the entered domain and its conventional `www` alias. HTTP/HTTPS, paths, ports, case and a trailing dot share one rule; entering `www.example.com` is equivalent to `example.com`. Existing saved rules are normalized when loaded. Expanded aliases are not stored as extra user entries, so removal and the 500-entry limit apply to the whole website.
- The report's shared-IP limitation applies: allowing an IP permits every hostname and port on that IP. Other subdomains are not discovered automatically, and the UI's subdomain checkbox is not yet implemented. Redirects, CDNs and page assets may need additional entries. Loopback proxies can forward traffic to otherwise allowed endpoints; this first build is not exact hostname enforcement or a defense against every tunneling technique.

## Windows acceptance checks

The CI workflow compiles/publishes the native DLL and worker and runs domain validation checks. It does not install network filtering on a CI runner. Run the following on a disposable Windows 10/11 x64 VM before release, recording OS/browser versions and outcomes:

1. Install with an empty list. Verify Chrome, Edge, Firefox, `curl.exe -4`, and `curl.exe -6` cannot reach unrelated sites. Test on a network with working IPv6; an IPv6-unavailable network is not a passing IPv6 test.
2. Add a controlled domain with A and AAAA records and a `www` alias on different addresses. Verify HTTP and HTTPS work for both names and both address families; verify other destination IPs remain blocked. Repeat by entering the `www` name, then restart the service and repeat. Remove the entry and verify both names are blocked. Add any explicit redirect/asset domains needed by browsers.
3. Start a long download, a persistent TCP connection, and a QUIC/UDP stream to that host. Remove it while traffic is active. Verify existing traffic stops on its next packet, and reconnection fails. Use hosts with distinct IPs to avoid shared-IP ambiguity.
4. Add two domains sharing one IP. Remove one and confirm that the shared address stays allowed until both domains are removed. This is the documented limitation.
5. Change A/AAAA records with a short TTL. Verify old addresses are revoked and new ones work after refresh. Simulate DNS failure and verify expired addresses disappear rather than persisting. Restore DNS and verify retries recover.
6. Restart the service and reboot without opening Electron. Verify saved domains reload. Stop/kill the service and confirm default deny remains; confirm SCM recovery after a crash.
7. Try pipe connections from an unauthorized local account and remote host. Verify denial. Send malformed JSON, unsupported versions, huge payloads, wildcards, credentials and oversized lists from the authorized account; verify failure without policy corruption.
8. Disconnect the service during an Electron update. Verify local data survives, no applied-success message appears, and retry synchronizes it. Inject a native apply error (for example invalid address in a test harness) and verify transaction rollback.
9. Renew DHCP, change DNS adapters, and verify localhost communication. Confirm no blanket LAN bypass. Run uninstall and verify normal connectivity and unrelated firewall policy remain intact.

Local development on macOS can run `npm run check` and `npm test`; it cannot validate native WFP enforcement.
