# CareCare.lol Desktop

A secure Electron starter for the CareCare.lol parental control system.

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
- `src/preload.js` — narrow, context-isolated renderer bridge
- `src/renderer.js` — browser-only interface behavior
- `src/index.html` and `src/styles.css` — starter dashboard

## Security baseline

The renderer uses context isolation and Chromium sandboxing, without Node.js integration. The app denies permission requests, unexpected navigation, and new windows, and its local page has a restrictive Content Security Policy. Keep Electron current and expose only narrowly scoped, validated IPC methods when adding privileged features.
