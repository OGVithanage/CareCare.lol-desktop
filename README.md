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

The website allowlist is stored atomically by `electron-store` in Electron's per-user application data directory. Only normalized HTTP and HTTPS origins are saved.

## Windows filtering

Add/remove operations save automatically and synchronize the complete list with a local Windows service. The UI distinguishes local saves from confirmed policy updates. Install the service separately using the [Windows build, installation, recovery, and acceptance-test guide](windows/README.md).

The service applies machine-wide IPv4/IPv6 WFP filtering, persists domains across restarts, and refreshes DNS addresses. It requires Windows x64; macOS/Linux retain local list management only. Domain-to-IP filtering also permits unrelated domains sharing an allowed IP.

Run transport and validation tests with `npm test`. Windows native builds and C# policy checks are configured in `.github/workflows/check.yml`.
