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
