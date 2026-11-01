# Wheel Friction Fix

A [Pulsar](https://github.com/SpaceGT/Pulsar) plugin for **Space Engineers 1** that makes wheels behave on blocks the same way they do on voxels. Example: https://youtu.be/gjTuvnly680

## What it fixes

**Different friction on blocks.** In vanilla, a wheel on a suspension gets only 7 % of its friction on armor blocks, but 100 % on functional blocks such as conveyors. With this plugin the contact friction on any block equals the wheel friction, exactly as on voxels.

**Tank turns that stick on blocks.** Skid-steer scripts turn a vehicle by changing the Friction of individual wheels. On a static grid Havok keeps the existing contact points of a standing wheel together with the friction they had when they were created, so the new friction is ignored until the wheel rolls. A vehicle that turns fine on voxels sticks and cannot turn in place on blocks. When the Friction setting of a wheel changes noticeably while the wheel is on a grid, the plugin recreates the wheel contacts so the change applies immediately (at most 4 times per second per wheel). This is needed both at rest and while driving. Terrain and braking do not trigger it.

Note: with very low Friction on all wheels (a few percent) a vehicle has less grip on armor than in vanilla, because the setting now really applies there.

## Usage

Every wheel suspension gets a **Voxel-like friction on blocks** checkbox in its terminal. It is **on by default**. Turn it off to get vanilla behaviour for that suspension. The state is stored in the block and saved with the world (and in blueprints).

Works with modded wheels that use the vanilla wheel and suspension logic.

## Multiplayer

- Install the plugin on the **lobby host**: that is where the physics is simulated. Dedicated servers cannot get it from a plugin hub yet.
- Tested: a client without the plugin driving on a plugin host turned normally, with no visible rubber-banding. Such a client does not see the checkbox.
- Clients with the plugin can toggle the checkbox; the server checks block access and syncs the state to everyone.
- A client with the plugin on a server without it behaves like vanilla (the checkbox stays inactive), so it never disagrees with the server physics.

## License

MIT
