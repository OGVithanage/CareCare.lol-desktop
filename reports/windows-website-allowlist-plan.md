# Simple Windows Website Allowlist

## Required behavior

- Block website traffic by default.
- When a website is added in the Electron app, allow it immediately.
- When a website is removed, block it again immediately.
- Restore the saved allowlist when Windows starts.

## Components

### Electron app

Keep the existing website list and `electron-store` persistence. After every add or remove operation, send the complete updated list to a local Windows service. The UI should show whether the service successfully applied the update.

### Windows service

Create a small C# worker service that:

1. Runs with the privileges required to manage Windows Filtering Platform (WFP).
2. Accepts the website list from Electron through a local named pipe.
3. Validates and normalizes every domain.
4. Resolves allowed domains to IPv4 and IPv6 addresses.
5. Replaces the active WFP rules in one transaction.
6. Refreshes resolved addresses when their DNS records expire.

Electron should never execute elevated network commands directly.

### WFP rules

Create a dedicated WFP provider and sublayer for CareCare. Add outbound block-by-default filters at the IPv4 and IPv6 ALE connection layers, followed by higher-priority permit rules for:

- IP addresses currently resolved from allowed domains.
- DNS access to the resolver used by the service.
- Localhost and any essential local-network traffic the app requires.

WFP is Windows' supported network filtering platform and can permit or block connections at the operating-system level ([Microsoft overview](https://learn.microsoft.com/en-us/windows/win32/fwp/about-windows-filtering-platform)).

## Update flow

```text
Add or remove website
        ↓
Electron saves the new list
        ↓
Electron sends the complete list to the service
        ↓
Service resolves domains and replaces WFP rules
        ↓
Existing affected connections are closed or reauthorized
        ↓
Service returns success or failure to the UI
```

Removing a website must also terminate or reauthorize its existing connections; WFP otherwise allows an established ALE flow to continue after it has been authorized ([Microsoft ALE documentation](https://learn.microsoft.com/en-us/windows/win32/fwp/ale-stateful-filtering)).

## Implementation order

1. Build the C# Windows service and named-pipe API.
2. Create, update, and remove CareCare WFP rules.
3. Connect the Electron save operation to the service.
4. Refresh DNS results periodically.
5. Start the service automatically with Windows.
6. Test Chrome, Edge, Firefox, command-line tools, IPv4, and IPv6.

## Important limitation

Simple domain-to-IP rules can allow unrelated websites hosted on the same shared IP address. This is acceptable for a minimal first build, but exact hostname enforcement would require a more advanced WFP callout driver or filtering proxy.
