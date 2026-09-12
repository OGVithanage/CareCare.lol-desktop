# CareCare.lol Desktop

A secure Electron app for managing the CareCare.lol website allowlist.

## Requirements

- Node.js 22 or newer
- npm

## Run locally

```sh
npm install
npm start
```

Run the JavaScript syntax checks with:

```sh
npm run check
```

## Structure

- `src/main.js` — Electron main process and window lifecycle
- `src/preload.cjs` — narrow, context-isolated renderer bridge
- `src/renderer.js` — browser-only interface behavior
- `src/index.html` and `src/styles.css` — starter dashboard

## Security baseline

The renderer uses context isolation and Chromium sandboxing, without Node.js integration. The app denies permission requests, unexpected navigation, and new windows, and its local page has a restrictive Content Security Policy. IPC senders and stored values are validated in the main process.

The website allowlist is stored atomically by `electron-store` in Electron's per-user application data directory. Versioned domain rules store normalized hostnames and an `allowSubdomains` boolean; legacy origin lists migrate automatically. The existing UI remains unchanged and does not yet edit that setting.

## Windows filtering

Add/remove operations save automatically and synchronize the complete list with a local Windows service. The UI distinguishes local saves from confirmed policy updates. Install the service separately using the [Windows build, installation, recovery, and acceptance-test guide](windows/README.md).

The version 2 service combines an HTTP/HTTPS hostname proxy with machine-wide IPv4/IPv6 WFP restrictions. It persists rules across restarts and revokes connections when their destination loses permission. Chrome/Edge are the initial managed browser targets. Windows acceptance testing remains required; encrypted CONNECT tunnels are not inspected internally. macOS/Linux retain local list management only. See the Windows guide for supported protocols and limitations.

Run transport and validation tests with `npm test`. Windows native builds and C# policy checks are configured in `.github/workflows/check.yml`.
