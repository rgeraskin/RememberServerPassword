# Remember Server Password

Client-side Valheim BepInEx plugin that remembers a server password after a successful join and automatically submits it on future connections to the same server.

## Behaviour

- Client-side only. The server does not need this plugin.
- Passwords are keyed by `host:port` when Valheim has an endpoint available.
- Steam ID and PlayFab ID are used as fallbacks for joins where no host endpoint is available.
- The first join behaves normally: enter the password in Valheim's password prompt.
- The password is only persisted after the server accepts it and completes the peer handshake.
- Future joins to that same server auto-submit the remembered password.
- If an automatically submitted password is rejected, that stored entry is deleted and the next join prompts normally.
- Password values are never written to the BepInEx log.

## Storage

The password database is stored under the local BepInEx config directory as `MagiCorp.RememberServerPassword.dat`.

On platforms where .NET protected user storage is available, passwords use `ProtectedData` scoped to the current OS user. On platforms where that API is unavailable, the plugin falls back to AES with a random per-install key stored alongside the database. The fallback avoids plaintext storage but should not be treated as protection against someone who can already read the user's local BepInEx files.

## Configuration

Generated file: `BepInEx/config/magicorp.valheim.rememberserverpassword.cfg`

- `Enabled`: master switch.
- `AutoSubmit`: automatically use stored passwords.
- `RememberPasswords`: save newly entered passwords after successful joins.
- `ForgetAllPasswords`: set to `true` and restart Valheim to erase all remembered passwords. It resets itself to `false`.
- `VerboseLogging`: log server identity matching details. Passwords are never logged.

## Installation

1. Install BepInExPack for Valheim.
2. Copy `MagiCorp.RememberServerPassword.dll` into `BepInEx/plugins/RememberServerPassword/`.
3. Launch Valheim normally.

## Compatibility

Built and patch-validated against:

- Valheim `l-1.0.12`, network version 40
- BepInExPack Valheim `5.4.2350`

## Technical approach

The plugin does not bypass Valheim authentication. It patches the normal client handshake, populates `FejdStartup.ServerPassword` only when a stored entry matches the current server identity, and lets Valheim call its existing `ZNet.OnPasswordEntered` / `SendPeerInfo` flow. New passwords are committed only after the client receives a successful `RPC_PeerInfo` response.
