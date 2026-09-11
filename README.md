# Remember Server Password

A client-side Valheim BepInEx quality-of-life plugin that remembers server passwords and adds a **Quick Connect** button for the last server you successfully joined.

## Features

- Remembers a password only after the server accepts it.
- Automatically submits that password on future joins to the same server.
- Passwords are keyed to the server identity:
  - direct/Dedicated joins: host or IP + port
  - Steam joins: Steam server/user ID
  - PlayFab joins: PlayFab server identity
- Adds **Quick Connect: <last server>** to the Valheim main menu.
- Quick Connect uses Valheim's normal `ServerJoinData` and character-selection flow.
- If an automatically submitted password is rejected, the stale password is removed automatically and the next join prompts normally.
- Client-side only. The server does not need this mod.
- Password values are never written to the BepInEx log.

## How it works

On the first connection to a password-protected server, join normally and type the password into Valheim's standard password dialog. The plugin holds that value only as a pending password until the server completes a successful peer handshake. Only then is it stored.

On later connections, the plugin recognises the server identity during `ZNet.RPC_ClientHandshake`, places the remembered value into Valheim's existing `FejdStartup.ServerPassword` path, and lets Valheim perform its normal password hashing and authentication.

The plugin does **not** bypass or weaken server authentication.

After a successful multiplayer connection, the server identity is also saved as the Quick Connect destination. The next time the main menu is shown, the button connects to that server through Valheim's normal join request flow, including character selection and privilege checks.

## Installation

1. Install BepInExPack for Valheim.
2. Copy `MagiCorp.RememberServerPassword.dll` into:
   `BepInEx/plugins/RememberServerPassword/`
3. Launch Valheim normally.

## Configuration

Generated file:

`BepInEx/config/magicorp.valheim.rememberserverpassword.cfg`

### General

- `Enabled = true`
  Master switch for remembered passwords.
- `AutoSubmit = true`
  Automatically use a stored password when its server requests one.
- `RememberPasswords = true`
  Save newly entered passwords after a successful connection.
- `VerboseLogging = false`
  Log server identity matching details. Password values are never logged.

### Quick Connect

- `ShowQuickConnectButton = true`
  Show the Quick Connect button on the main menu.
- `LastServer`
  Automatically managed identity of the last server successfully joined. It can be cleared manually if desired.

### Maintenance

- `ForgetAllPasswords = false`
  Set to `true` and restart Valheim to erase all saved passwords. It automatically resets to `false`.
- `ForgetLastServer = false`
  Set to `true` and restart Valheim to clear the Quick Connect destination. It automatically resets to `false`.

## Password storage

The password database is stored under the local BepInEx config directory as:

`MagiCorp.RememberServerPassword.dat`

On platforms where .NET protected user storage is available, password values use `ProtectedData` scoped to the current OS user.

If that API is unavailable, the plugin falls back to AES with a random per-install key stored as `MagiCorp.RememberServerPassword.key`. This fallback prevents casual plaintext exposure but is not intended to protect against somebody who already has full read access to the local BepInEx directory.

## Compatibility

Built against and patch-validated with:

- Valheim `l-1.0.12`
- network version `40`
- BepInExPack Valheim `5.4.2350`

The password-handshake patches were runtime validated against 1.0.12. The Quick Connect UI addition is built against the same 1.0.12 assemblies and does not require any server-side component.

## Technical details

Relevant vanilla paths used rather than replaced:

- `ZNet.RPC_ClientHandshake`
- `ZNet.OnPasswordEntered`
- `ZNet.RPC_PeerInfo`
- `ZNet.RPC_Error`
- `FejdStartup.SetupGui`
- `FejdStartup.ProceedJoinRequest`

The plugin intentionally delegates authentication, connection setup, character selection and multiplayer privilege validation back to Valheim.
