# PPG Performance Suite

A performance mod for [People Playground](https://store.steampowered.com/app/1118200/People_Playground/) that makes the game run smoother during big builds and heavy combat — without changing how anything looks, feels, or plays. Damage, range, and speed are all untouched.

## Features

**Auto Optimizer** — the core system. Watches your real FPS and smoothly scales performance adjustments in only when actually needed, then relaxes everything back to normal once things recover. It's a continuous curve, not an on/off switch.

Under sustained strain, it escalates in this order (least to most disruptive):
1. Reduces Bloom
2. Turns off Fancy Effects
3. Gradually lowers Render Scale
4. Turns off Decals
5. As an absolute last resort at very low FPS: disables Bullet Tracers

Lighting is never touched, since it barely affects performance to begin with.

### Other features

- **Bigger effect pools** — fixes tracers/muzzle flashes disappearing during heavy sustained fire
- **Smart bullet collision** — bullets only run expensive collision checks when something's actually nearby, otherwise they use cheap detection. Same range, same damage.
- **Debris & decal cleanup** — automatically caps piled-up rubble and blood/scorch marks, prioritizing removal of whatever's currently off-screen first
- **Corpse limiting** — prevents unbounded pileup of dead bodies over long sessions
- **Off-screen visual culling** — bleeding particle effects and fire mesh generation pause their expensive per-frame work while off-screen, and resume instantly the moment they're visible again
- **Session structure cache** — remembers which spawned item types have none of the above systems at all, skipping redundant checks on every future spawn of that same type
- **Incremental garbage collection** — spreads out GC cleanup work to soften stutter
- **Audio safety net** — a rare last-resort mute for extreme simultaneous sound counts (99.9% of play sessions will never trigger this)

## Installation

Subscribe via the [Steam Workshop page](#) or drop this folder into:
```
People Playground/Mods/PPGPerformanceSuite/
```

## Notes

- Disabling this mod in the in-game menu takes effect after a full game restart — this is a People Playground limitation, not specific to this mod.
- The mod will show a one-time popup explaining Auto Optimizer's behavior on first load, with a "Never Show Again" option.
- A small notification appears in the corner whenever Auto Optimizer actually kicks in or relaxes back to normal.

## License

See [LICENSE](LICENSE). Modding/personal use is welcome — redistribution of altered versions requires permission from the author.

## Requirements

None!
