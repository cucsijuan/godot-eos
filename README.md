# GodotEos

Epic Online Services for Godot .NET games: sign-in with a device account, lobbies with their members, and peer-to-peer packets between players. Plain .NET 8 (no Godot API), so any C# game can use it.

- `EosPlatform`: the SDK on a thread of its own (every SDK call runs there; the game gets tasks back), device-account sign-in (`SignInWithDeviceAsync`, making the player the first time), and renewal of the hour-long sign-in before it runs out, retried with backoff.
- `EosLobbies` / `EosLobby`: make a lobby in a bucket with text attributes, search a bucket, join. Joined players are members, and a lobby tracks them as they come and go (`Has`, `TryTakeChange`). No host migration: when the owner leaves, the lobby closes.
- `EosP2P`: packets on one socket, by channel, reliable (ordered) or not; direct, or through Epic's relays. Nobody connects unless `Accepts` says so (a game lets in its lobby's members); packets from anyone else are dropped.
- `EosKeys`: the product's keys, from a JSON file or the environment. They belong to the game and should stay out of public repositories.

## Building

The EOS SDK is not here (Epic's licence). Download the C# SDK from the developer portal and point `EosSdkDir` at its `SDK` folder (with `Source/` and `Bin/`); by default it is `../eos-sdk/SDK` next to this repository. A game that has this repository as a submodule sets `EosSdkDir` in its own `Directory.Build.props`.

`src/EosSdk` compiles the SDK's sources for the platform being built (Windows or Linux, from `GodotTargetPlatform` or the host) and copies its native library next to the assemblies; `EosPlatform` loads it from there, since under Godot the runtime does not look there by itself.

## Using it

```csharp
var platform = new EosPlatform(keys, new EosPlatformOptions { ProductName = "Game", CacheDirectory = ProjectSettings.GlobalizePath("user://eos") });
var p2p = new EosP2P(platform, "GAME");
var lobbies = new EosLobbies(platform);
await platform.SignInWithDeviceAsync("Pilot");

// the host
EosLobby lobby = await lobbies.CreateAsync("game:1", 16, new Dictionary<string, string> { ["HOST"] = "Pilot" });
p2p.Accepts = lobby.Has;

// a guest
var found = await lobbies.SearchAsync("game:1");
EosLobby joined = await lobbies.JoinAsync(found[0]);
p2p.Accepts = peer => peer == joined.Owner;
p2p.Send(joined.Owner, 0, packet, reliable: true);
```

`SignInWithDeviceAsync(name, newDevice: true)` replaces the computer's device account first: for running several players on one computer.
