# Windows hostname allowlist service (protocol 2)

The service evaluates per-domain rules through a local HTTP/HTTPS proxy, with a persistent WFP boundary that permits external TCP ports 80/443 only for the protected service executable. It no longer installs global website-IP permits. Chrome and Edge are the first supported browser targets. **Windows acceptance is pending; do not treat the implementation as release-validated enforcement.**

## Build and installation

Use Windows x64 with .NET 8 SDK, CMake 3.24+, Visual Studio 2022 C++ Build Tools and a Windows SDK. Build before installing the network restrictions:

```powershell
.\windows\build.ps1
dotnet run --project .\windows\tests\CareCare.PolicyTests.csproj
# Elevated PowerShell, preferably in a disposable VM with a local console:
.\windows\install.ps1 -ControllerAccount 'PCNAME\Parent'
```

The published folder includes install/uninstall scripts, the browser-policy helper and a version 2 manifest. Restart Chrome/Edge after installation and check `chrome://policy` or `edge://policy`. Installation writes machine policies for a fixed proxy at `127.0.0.1:17843`, disables implicit loopback bypass and disables QUIC. There is no DIRECT fallback. Firefox and non-proxy applications are unsupported and external connections remain blocked. Existing enterprise policy overrides can prevent browsing; confirm effective policies on the target machine.

Installation enables machine-wide outbound deny, including updates, VPNs and remote administration. Exceptions are service TCP 80/443, TCP to the loopback proxy port, DNS port 53 to configured resolvers from the service or Windows service host, and DHCP from the Windows service host. Other local proxies and direct browser TCP/UDP connections have no permit. Other firewall providers can still block permitted traffic.

## Rules, migration and compatibility

Electron stores `{ "allowlist": { "version": 2, "websites": [{ "domain": "example.com", "allowSubdomains": false }] } }`. `%ProgramData%\CareCare\allowlist.json` stores the inner versioned object. Both components independently migrate legacy string arrays, normalize and deduplicate by domain, and default legacy settings to false. Service replacement is atomic with a flushed temporary file; Electron retains its legacy key as a backup. A present corrupt or unsupported version fails closed instead of falling back to a stale legacy policy. The maximum is 500 submitted user rules, before alias generation or DNS resolution. Duplicate entries use the last setting.

The new pipe is `\\.\pipe\CareCare.Allowlist.v2`. Requests and responses require version 2. Version 1 requests are rejected, and the client never retries with strings. Upgrade the client and service together; an old client cannot contact the new endpoint. The installer requires the version 2 package manifest. SYSTEM, administrators and the configured controller SID can update policy; remote pipe logons are denied. Any process under the controller account has its permissions.

The renderer, HTML and CSS are unchanged. Its existing string API is adapted in the main process: retained domains keep their saved setting; new domains default to false. The main-process IPC also accepts the versioned payload and returns it as `allowlist`, alongside the existing `websites` response. **The existing checkbox remains unwired; per-website editing requires a separate UI change.** Locally persisted changes and service-confirmed enforcement remain distinct.

## Enforcement and live updates

For `example.com`, the main hostname and `www.example.com` always match. Enabling subdomains also matches any depth below `.example.com`, including names not previously resolved. `notexample.com` and `example.com.other.com` do not match. An explicit child rule does not allow its parent or siblings. Rules are additive; one matching rule is enough. Case, IDNs, one trailing dot and the saved `www` alias are normalized.

HTTP absolute-form requests on port 80 receive a hostname decision before forwarding. Each connection forwards exactly one request with `Connection: close`; pipelined/reused requests cannot ride an earlier decision. CONNECT on port 443 receives a decision before an opaque, end-to-end TLS tunnel opens. Host headers must agree with the target. DNS resolution occurs per accepted connection and connections use the resolved address, avoiding a second DNS lookup. Private, loopback, link-local and non-global IPv6 destinations are rejected.

Policy publication and connection registration share a lock. Updates replace an immutable snapshot and cancel active connections whose destination no longer matches, including pending connects and HTTPS tunnels. Another matching rule retains the connection. Desired state is persisted before publication. Persistence failure leaves active policy unchanged; publication failure attempts an empty policy and reports failure. Service startup installs the WFP boundary before loading any saved state. Proxy/boundary failures terminate the service for SCM recovery; persistent filters remain while the proxy is unavailable. This is not a boot-time driver and does not cover the interval before BFE loads filters.

The proxy bounds connections (256), headers (32 KiB), header/connect time (15 seconds total), request bodies (64 MiB) and connection lifetime (30 minutes). HTTP chunked uploads, `Expect`, HTTP upgrades/WebSockets, nonstandard ports, IP-literal targets and private-network sites are unsupported. Response bodies stream without buffering. DNS uses the OS resolver; DNS-over-HTTPS configurations need Windows acceptance testing, since no general external resolver HTTPS permit is installed. Adapter resolver changes update the WFP boundary.

CONNECT checks the declared tunnel hostname, not encrypted requests or TLS identities inside the tunnel. Shared-IP HTTP destinations are isolated by the proxy's destination/Host validation, but intentional CONNECT misuse and encrypted connection coalescing require separate acceptance testing; this is not protection against every deliberate tunnel. DNS/DHCP infrastructure exceptions are not a general anti-exfiltration boundary. Administrators can remove the controls. Redirects and external assets need their own matching rules.

References: [Chromium proxy behavior](https://chromium.googlesource.com/chromium/src/+/HEAD/net/docs/proxy.md), [Chrome ProxySettings](https://chromeenterprise.google/policies/proxy-settings/), [Chrome QuicAllowed](https://chromeenterprise.google/policies/quic-allowed/), [WFP application identifiers](https://learn.microsoft.com/en-us/windows/win32/api/fwpmu/nf-fwpmu-fwpmgetappidfromfilename0).

## Recovery and uninstall

```powershell
# Elevated; saved policy and binaries are retained.
.\windows\uninstall.ps1
```

Browser settings are journaled before mutation in the protected state directory. Uninstall restores the old value only if its current value and type still equal CareCare's owned value, preserving subsequent third-party changes. Reinstall refuses conflicts with the ownership journal. The helper is included in the installed folder. If installation is interrupted, use uninstall to restore journaled settings before retrying.

Emergency removal with the installed package:

```powershell
& "$env:ProgramFiles\CareCare\uninstall.ps1"
```

Stopping the service alone intentionally leaves filtering active. The installer restricts executable and state directories to SYSTEM/administrators. Native removal only touches CareCare's provider, sublayer and filters.

## Required Windows acceptance before release

Record OS/browser versions and results on a disposable Windows 10/11 VM. The automated proxy tests exercise loopback sockets with an injected upstream connector; they do not validate native WFP or managed browser integration.

1. Test controlled main, `www`, child and nested names with both distinct and shared public IPv4/IPv6 addresses. Verify the plan's on/off behavior table over HTTP/HTTPS, including newly created subdomains.
2. Disable a parent's setting during downloads and CONNECT sessions; verify revocation, while an explicit child rule retains its connections. Repeat for full removal and an empty policy.
3. Verify HTTP redirects/assets, HTTP connection reuse and pipelining, TLS connection coalescing and deliberately mismatched CONNECT/TLS targets. Record the encrypted-tunnel limitation instead of claiming full inspection.
4. Attempt direct TCP, UDP/QUIC, IPv6, browser proxy overrides, local forwarders and DNS rebinding to private addresses. Verify Chrome/Edge fallback to proxied TCP. Confirm unsupported browsers/apps stay blocked.
5. Stop/kill the proxy/service and occupy the proxy port before service startup. Verify external traffic remains blocked and SCM recovery behaves correctly. Restart/reboot without Electron and verify independent migration and persistence.
6. Exercise pipe authorization, malformed/oversized input, nonboolean settings, version 1/unknown versions, failed persistence, interrupted replies and native transaction failures. Verify failures are reported without losing saved settings or claiming applied success.
7. Verify adapter DNS changes, DHCP renewal and IPv6 routing. Test with existing firewall rules and enterprise browser policies.
8. Install, upgrade, interrupt installation and uninstall with absent/preexisting/externally edited browser policies. Verify restoration and preservation of unrelated registry settings.

On macOS/Linux, `npm run check`, `npm test` and the .NET policy/proxy tests can validate portable logic. Only Windows can establish native enforcement behavior.
