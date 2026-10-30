# Wheel Friction Fix

A [Pulsar](https://github.com/SpaceGT/Pulsar) plugin for **Space Engineers 1** that makes wheels behave on blocks the same way they do on voxels.

## What it fixes

**Different friction on blocks.** In vanilla, a wheel on a suspension gets only 7 % of its friction on armor blocks, but 100 % on functional blocks such as conveyors. With this plugin the contact friction on any block equals the wheel friction, exactly as on voxels.

**Tank turns that stick on blocks.** Skid-steer scripts turn a vehicle by changing the Friction of individual wheels. On a static grid Havok keeps the existing contact points of a standing wheel together with the friction they had when they were created, so the new friction is ignored until the wheel rolls. A vehicle that turns fine on voxels sticks and cannot turn in place on blocks. The plugin recreates the wheel contacts whenever the wheel friction changes noticeably, so the change applies immediately.

## Usage

Every wheel suspension gets a **Voxel-like friction on blocks** checkbox in its terminal. It is **on by default**. Turn it off to get vanilla behaviour for that suspension. The state is stored in the block and saved with the world (and in blueprints).

Works with modded wheels that use the vanilla wheel and suspension logic.

## Multiplayer

- The plugin is required on the **host or server**: that is where the physics is simulated.
- Clients without the plugin still get the fixed behaviour from the server; they just do not see the checkbox.
- Clients with the plugin can toggle the checkbox; the server checks block access and syncs the state to everyone.

## Building locally

Requirements: .NET SDK and Space Engineers installed through Steam.

```
dotnet build -c Release
```

The game folder is detected automatically. To set it manually:

```
dotnet build -c Release -p:GamePath="D:\Games\SpaceEngineers\Bin64"
```

After a build the DLL is copied to `%AppData%\Pulsar\Legacy\Local\WheelFrictionFix\plugin.dll`. `build.bat` does a full rebuild.

## License

MIT
