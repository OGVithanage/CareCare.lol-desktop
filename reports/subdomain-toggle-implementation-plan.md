# Per-website subdomain toggle implementation plan

Date: 2026-09-12

Status: Proposed implementation; this report does not enable subdomain filtering.

## Objective

Each saved website has its own **Allow subdomains** setting. The main domain and its conventional `www` alias remain allowed over HTTP and HTTPS regardless of the setting. Enabling the setting also allows all descendants of that specific domain, including nested subdomains.

The work has two parts: changing the saved data and UI to represent per-website settings, and changing Windows enforcement to evaluate requested hostnames. Saving a boolean alone will not implement the feature.

## Expected behavior

For a saved `google.com` rule:

| Requested hostname | Toggle off | Toggle on |
| --- | --- | --- |
| `google.com` | Allowed | Allowed |
| `www.google.com` | Allowed | Allowed |
| `mail.google.com` | Blocked | Allowed |
| `a.b.google.com` | Blocked | Allowed |
| `notgoogle.com` | Blocked | Blocked |
| `google.com.other.com` | Blocked | Blocked |

Both HTTP and HTTPS follow the same rule. A blocked result assumes no other saved rule allows the hostname. The allowlist is additive: any matching rule permits the requested hostname.

Normalize case, Unicode domain names and trailing dots before matching. Normalize a conventional leading `www` alias consistently with the existing behavior. Protocols, paths and ports do not create separate website entries.

The matching predicate, after normalization, is:

```js
host === domain ||
host === `www.${domain}` ||
(allowSubdomains && host.endsWith(`.${domain}`))
```

The dot before the suffix is required to avoid matching unrelated names such as `notgoogle.com`. Adding `mail.google.com` must not implicitly allow `google.com` or sibling domains. With its toggle enabled, it allows descendants such as `a.mail.google.com`.

## Saved data structure

The current Electron format contains strings:

```json
{
  "allowedWebsites": ["https://google.com"]
}
```

Replace it with a versioned collection of rule objects:

```json
{
  "allowlist": {
    "version": 2,
    "websites": [
      {
        "domain": "google.com",
        "allowSubdomains": true
      },
      {
        "domain": "example.com",
        "allowSubdomains": false
      }
    ]
  }
}
```

Only the normalized hostname and setting are stored. Do not enumerate or persist subdomains, HTTP/HTTPS variants or generated `www` aliases. Matching requests as they occur allows newly created subdomains without editing the list.

Use the same versioned payload in Windows service state (`allowlist.json`), without the Electron-specific outer `allowlist` key. The 500-entry limit applies to user rules, not aliases or resolved addresses. Deduplicate by normalized domain rather than by object identity.

### Migration

1. Read the new format if present and valid; otherwise migrate the legacy string list.
2. Convert every legacy entry to a normalized domain with `allowSubdomains: false`, preserving the current main-domain/`www` behavior.
3. Merge equivalent legacy entries, including HTTP/HTTPS and `www` variations.
4. Validate the entire migrated collection before replacing existing data. Retain the legacy data until the new write succeeds, and make migration safe to repeat.
5. Migrate Electron storage and Windows service state independently so the service can restart before Electron opens.
6. Reject malformed or unsupported versions without broadening access. Do not silently reset a corrupt policy to an unrestricted state.

## UI and application changes

| File or component | Required change |
| --- | --- |
| `src/renderer.js` | Read the add-form checkbox and attach its boolean to the new rule. Render each saved rule with its own editable toggle. Sort, remove and deduplicate by domain. Disable editing while a save is in progress. |
| `src/index.html` | Explain that the main domain and `www` are always included; the toggle controls additional subdomains. |
| `src/styles.css` | Style the per-row toggle and ensure rows remain usable at narrow window widths. |
| `src/website.js` | Normalize and validate rule objects while retaining reusable hostname normalization. Require an actual boolean for new-format rules. |
| `src/main.js` | Update the Electron Store schema, migrate legacy entries, validate IPC payloads and save versioned rules. |
| `src/preload.cjs` | Review the bridge contract; existing method names can remain if they pass the new payload without string-specific assumptions. |
| `src/windows-service.js` | Send protocol version 2 with rule objects and clearly report incompatible service versions. |
| `windows/service/DomainPolicy.cs` | Independently validate rules and implement the hostname matching predicate. |
| `windows/service/AllowlistWorker.cs` | Migrate and persist versioned rules, apply them to the filtering component and coordinate live updates. |

Adding a domain that already exists should update that row's setting rather than create a duplicate. Changing one row must not modify other rules. Retain the existing distinction between locally saved changes and changes confirmed by the Windows service.

## Service protocol compatibility

Use a version 2 request containing rule objects:

```json
{
  "version": 2,
  "websites": [
    { "domain": "google.com", "allowSubdomains": true }
  ]
}
```

The updated client must not fall back to sending strings and silently lose the toggle setting. An incompatible service must produce a clear upgrade-required result. Decide explicitly whether the new service accepts legacy version 1 requests as rules with `allowSubdomains: false`; test that choice. Coordinate the pipe endpoint and installer versioning with this compatibility policy.

## Recommended Windows enforcement architecture

### Why the current resolver is insufficient

The current service resolves a fixed set of domain names and globally permits their IP addresses through WFP. It cannot enumerate arbitrary future subdomains, and IP permits cannot distinguish allowed and blocked names sharing the same address.

Windows Firewall's built-in FQDN dynamic keyword feature does not support wildcard names, so substituting that feature does not directly implement this requirement. See [Microsoft's Windows Firewall dynamic keyword documentation](https://learn.microsoft.com/en-us/windows/security/operating-system-security/network-security/windows-firewall/dynamic-keywords).

### Introduce a local filtering proxy

Use a service-managed HTTP/HTTPS proxy that checks requested hostnames against the rules before connecting:

1. For HTTP, validate the destination of every request, including requests on reused connections.
2. For HTTPS, validate the `CONNECT` target hostname before opening a tunnel.
3. Resolve permitted destinations on demand instead of pre-enumerating subdomains.
4. Configure supported browsers to use the local proxy through managed settings.
5. Update WFP enforcement to prevent browsers and other untrusted processes from connecting directly around the proxy. Scope external connection permissions to the proxy process or service identity instead of globally permitting website IPs.
6. Cover direct UDP/QUIC bypasses. Verify browser fallback to proxied TCP and document any unsupported protocols or applications.
7. Keep external access blocked when the proxy is unavailable. Do not configure a direct fallback.

HTTPS `CONNECT` supplies the requested hostname while preserving end-to-end TLS, so ordinary browser hostname filtering does not require installing a root certificate. However, a tunnel does not expose encrypted requests inside it: this approach must not be presented as inspection of every encrypted request or protection against every deliberate tunneling technique. See [Chromium's proxy documentation](https://chromium.googlesource.com/chromium/src/+/HEAD/net/docs/proxy.md).

The intended first scope is supported browsers using the managed proxy. Validate shared-IP behavior, encrypted connection reuse and bypass resistance before making stronger enforcement claims.

### Additional components affected

- Add a proxy component and connection tracking to the Windows service, with bounded request parsing, timeouts and cancellation.
- Change `windows/native/wfp.cpp` to enforce the proxy connection boundary and remove global website-IP permits.
- Update `windows/service/Native.cs` if the native policy interface changes.
- Replace or adapt the existing DNS refresh/cache path so address resolution supports proxy connections rather than globally opening destinations.
- Update installation and uninstallation scripts to configure supported browsers and restore settings owned by CareCare without overwriting unrelated user configuration.
- Update service recovery, build packaging and Windows documentation for the new component.

## Applying changes while browsing

Publish a validated rule snapshot atomically to the proxy so requests cannot observe a partially updated list. Track the destination hostname of active connections. Removing a rule or disabling subdomains must close connections that no longer match any rule, including existing HTTPS tunnels. Retain connections permitted by another rule.

Persist the desired policy atomically for restart recovery and report whether live enforcement succeeded. A failed update must not silently permit additional traffic. Start with blocking in place before loading and applying persisted rules.

## Validation

### Automated checks

- Cover the behavior table, nested subdomains, dot-boundary matching and explicit subdomain rules.
- Cover HTTP/HTTPS normalization, case, Unicode, trailing dots and invalid values.
- Verify migration, duplicate normalization, boolean validation, the 500-rule limit and repeat migration.
- Verify per-row edits, duplicate additions, removal and IPC serialization.
- Verify incompatible protocol handling without losing settings.
- Test HTTP and HTTPS proxy decisions, reused connections, rule updates and revocation of active connections.

### Windows acceptance checks

- Use controlled main domains and subdomains with both distinct and shared IP addresses, over IPv4 and IPv6.
- Verify newly requested subdomains work when enabled without adding them to storage.
- Verify disabling the toggle blocks subsequent requests and terminates connections no longer allowed.
- Verify an explicit child-domain rule remains effective after disabling its parent's toggle.
- Verify persistence across app restart, service restart and reboot.
- Verify supported browsers cannot bypass enforcement through direct connections, proxy fallback or UDP/QUIC.
- Verify proxy failure retains blocking and uninstall restores the configuration CareCare owns.
- Test redirects and external assets separately: allowing `google.com` descendants does not automatically allow unrelated domains used by the page.

Native WFP behavior and browser integration require Windows validation; JavaScript unit tests alone cannot establish enforcement.

## Implementation sequence

1. Implement and test the rule model, normalization, storage migrations and version 2 protocol.
2. Wire the add-form checkbox and per-row editing to the rule objects, keeping the feature unavailable until enforcement is supported.
3. Implement the proxy and hostname policy checks with integration tests.
4. Integrate WFP restrictions, browser configuration, live connection revocation and service recovery.
5. Complete Windows acceptance checks, then enable the toggle and document supported behavior and limitations.

The main effort is the Windows enforcement change. The storage and UI work are necessary foundations, but must not ship as a working subdomain feature without that enforcement.
