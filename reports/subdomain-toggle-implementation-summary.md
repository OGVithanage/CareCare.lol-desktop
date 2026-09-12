# Subdomain policy implementation handoff

Date: 2026-09-12
Branch: `feature/per-website-subdomain-policy`

## Implemented

- Version 2 per-domain rules, strict boolean validation, normalization, domain deduplication (last setting wins), 500-rule limit, and independent Electron/service migrations.
- Atomic service-state replacement; Electron retains the legacy key. Present invalid/unknown policies fail instead of falling back to stale legacy data.
- Version 2 pipe endpoint and responses; version 1 requests are explicitly rejected with no client downgrade. Package manifest and service version updated.
- Local HTTP/CONNECT proxy with bounded parsing/concurrency/timeouts, on-demand DNS, public destination checks, atomic policy publication, and revocation of disallowed HTTP sessions, HTTPS tunnels and pending connections.
- WFP external TCP 80/443 permits scoped to the service executable, replacing global website-IP permits. Direct browser TCP/UDP/QUIC traffic has no external permit. DNS/DHCP exceptions are restricted to infrastructure applications and destinations/ports.
- Chrome/Edge managed fixed proxy and QUIC policies, ownership journal, conditional restoration on uninstall, packaged scripts and Windows CI registry tests.
- Updated Windows support, recovery, compatibility, limitations and acceptance documentation.

## UI constraint

`src/renderer.js`, `src/index.html`, `src/styles.css`, `src/preload.cjs` and the renderer's existing normalization helper are unchanged. Main-process compatibility adapts string-based UI saves into versioned rules while preserving booleans on retained domains. The versioned payload can be passed through the existing save bridge; responses include both the legacy display list and the versioned allowlist.

The existing checkbox remains unwired, so users cannot change per-domain settings through this UI yet. Suggested follow-up, after Windows acceptance:

1. Read the add-form checkbox when creating or updating a domain.
2. Add an editable toggle per saved website; changing one must preserve all other rules.
3. Explain that the main hostname and conventional `www` alias always match.
4. Disable checkbox/row editing during saves, and keep existing local-save/service-confirmation status behavior.

## Validation actually performed

- `npm run check`: passed.
- `npm test`: all 19 tests passed.
- .NET 8 service build: passed, zero compiler errors. NuGet emitted NU1900 because vulnerability metadata was unavailable; that audit did not complete.
- .NET policy/proxy test project build: passed, zero warnings/errors.
- Policy/proxy executable: passed normalization, behavior-table matching, protocol rejection, independent/repeat migration, failed persistence, HTTP/CONNECT decisions, malformed/bounded parsing, shared-endpoint isolation, pipelining rejection, additive rule retention, active HTTP/HTTPS revocation, and pending-connect cancellation.
- `git diff --check`: passed. UI-file diff: empty.

## Pending Windows validation

Native C++ compilation, real WFP behavior, installer/browser registry tests and managed Chrome/Edge integration were not executed on this macOS host. The Windows CI job builds the native/service package and runs the added registry tests; it has not been run from this local task. The acceptance checklist in `windows/README.md` remains required before release, including real IPv4/IPv6, shared IPs, reboot/recovery, bypass attempts and uninstall restoration.

The initial proxy supports HTTP port 80 and CONNECT port 443. It closes HTTP connections after one request and rejects chunked uploads, Expect, WebSocket upgrades, nonstandard ports and private-network destinations. CONNECT does not inspect encrypted requests or prevent every deliberate tunneling technique. These are documented support limits, not completed Windows acceptance claims.
