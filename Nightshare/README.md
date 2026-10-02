![Nightshare](../docs/nightshare.png)

Co-op multiplayer for **Nivalis Nights**.

> ## Not Playable Yet
>
> **There is no download, and nothing here is ready to play.** This is early development
> on the networking framework: getting two copies of a single player game to agree on a
> world at all. It is published so the work is visible and so anyone who wants to pick
> through it can.
>
> Expect it to break. Expect saves to be at risk. **Back up your saves before running
> anything from this folder.** Features, polish and the systems that make it actually fun
> come after the framework holds together.

## What It Is Meant To Be

**You visit someone's city and build a life in it with them.**

The host's save is the world: its story, its people, its businesses. A guest arrives as
their own character and can do anything a person can do there, including working in the
host's businesses.

Two decisions shape everything else, and both were learned the hard way rather than
designed up front:

**The story belongs to the host.** An earlier design gave each player their own quest
progress. It cannot work. The game's narrative variables are not a progress log, they are
what the world is made of: who is standing where, which shops exist, who runs them. Two
sets of those against one city means a guest holding a quest marker for something the city
has already done.

**Nothing travels home.** A guest's character, inventory and progress live permanently in
the host's save. This sounds restrictive and is the opposite. If progress travelled, two
people who play together and then separately end up at different points in their own
saves, and the next time they want to play together one of them has to give up progress to
do it. One shared place that only moves when both are in it is the entire point.

## Where It Actually Is

Honest status, since the above describes an intention and not a finished thing. Six phases,
two of them done.

![Phase 1: Foundations, complete](../docs/phase1.png)

![Phase 2: Two Players, One City, complete](../docs/phase2.png)

### What Works Today

Both players load their own save, press a key, and the host shows up in a list on the
guest's screen by name. No addresses, no config files. Joining hands the guest the host's
world and puts them next to the host, on the same clock, in the same room.

From there you can see each other walking around, time runs once for both of you, and the
day only turns when you have both gone to bed.

### Phase 3, Next

**Shared crowds are the next big piece.** Each side currently simulates its own people, so
you and your host do not see the same characters in the street. Everything you can interact
with lives there too: shops, doors, and anything where two people touching the same thing
has to resolve sensibly.

### Phases 4 To 6

Your own character and the look of it; **working at the host's businesses**, which is the
part the mod is actually for; then Steam invites and the polish that makes it releasable.

Steam invites are deliberately last. Two copies of the game on one machine share a Steam
account, so that path cannot be tested by one person, and everything else can.

## Building It

You need the game and
[BepInEx 6 (IL2CPP, win-x64)](https://builds.bepinex.dev/projects/bepinex_be) installed,
and the game must have been launched once with BepInEx so its interop assemblies exist.

```
dotnet build src/Nightshare.Plugin/Nightshare.Plugin.csproj
dotnet test  tests/Nightshare.Tests/Nightshare.Tests.csproj
```

If your game is not on the default path:

```
dotnet build src/Nightshare.Plugin/Nightshare.Plugin.csproj /p:NivalisDir="D:\Games\Nivalis Nights"
```

The build drops the plugin straight into the game's `BepInEx\plugins\Nightshare\`.

## How It Is Put Together

`Nightshare.Core` is plain .NET with **no Unity or BepInEx dependency**: the wire protocol,
the transport and the session live there, so the protocol can be tested and iterated
without launching a 36 GB game. `Nightshare.Plugin` is everything that touches the game.

Two things worth knowing if you read the source:

**The host owns the world, each client owns its own character.** Remote players are
ordinary character rigs driven by received positions, not second player objects. The game
has exactly one player and the camera, input and HUD all assume it.

**The game's own serialiser is the wire format.** Joining sends the host's actual save
file, and the guest loads it through the game's own load path. An earlier version
reassembled the world out of individual manager packets and applied them to a running
game; it failed quietly in several places and was replaced by something far smaller.

## Contributing

Issues and pull requests are welcome, with the caveat that the design is still moving and
large changes may collide with work in progress. Small fixes, and anything where you have
found a case that was not thought of, are the most useful kind.

There is no download to test against yet, so contributing means building it yourself.

## Licence

MIT. See [LICENSE](../LICENSE).

Not affiliated with ION LANDS or 505 Games.
