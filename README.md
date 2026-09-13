# Three Sheets to the Wind (Drunk Mod)

Adds drunk effects and alcohol blackouts to Sailwind. Drink too much too fast and you pass out.

## Features

- Alcohol takes time to hit. What you drink sits in your stomach and reaches your blood over the next
  minute or so, so drinking fast keeps hitting you after you have stopped.
- Drunk effects: blurred instruments (the horizon stays in focus, things in your hands do not), color
  fringing, film grain, a slow wander on your view, extra lurch when walking, and the horizon rocking.
- Tunnel vision closes in when you are close to passing out. Stop drinking and it opens back up.
- Water, coffee and tea clear alcohol that is still in your stomach. They do nothing for what has
  already reached your blood.
- Sleeping sobers you up 3x faster than staying awake, so a night in bed clears a heavy session.
- Rest never goes down while you are in bed, however drunk you are.
- Blackout: you fall over where you stand. The fall uses physics, so you land on the deck and slide if
  the boat is heeling.
- In single player, time runs at 16x while you are out, the same as sleeping. You wake once you have
  sobered up enough, between 2 and 8 game hours later, still a bit drunk and thirsty. You get some rest
  while out, less than real sleep. Your boat keeps sailing the whole time.
- You are not moved to port and nothing is taken from you.
- Every effect can be turned off and every number changed.

## Installation

Requires BepInEx 5 (x64).

Download the .zip from the latest [release](https://github.com/DiamondMiner99/sailwind-threesheets/releases)
and extract it into your Sailwind folder. The folder structure should look like:

```
BepInEx\
  plugins\
    ThreeSheets\
      ThreeSheets.dll
```

Built against Sailwind 0.38.1.

## Sailwind Co-op

Works with or without [Sailwind Co-op](https://github.com/DiamondMiner99/sailwind-coop). It is
client-side, so only the players who want it need to install it. When another player is connected the
time warp is skipped and the blackout lasts a fixed number of real seconds instead (20 by default).
Other players do not see you fall yet.

## Other mods

NANDTweaks has a Drunken Sleep option that drains rest while you sleep drunk. With Three Sheets
installed, rest does not go down in bed whether that option is on or off. Nothing else in NANDTweaks is
affected.

## Config

Options are in `BepInEx/config/com.diamondminer99.threesheets.cfg` or the F1 menu if you have the
[BepInEx Configuration Manager](https://github.com/BepInEx/BepInEx.ConfigurationManager). Changes apply
without a restart.

- **General**: master switch. `FreeCursorInConfigMenu` stops the view turning while the F1 menu is open.
- **Drinking**: how fast a drink reaches you (`AbsorbRate`), how fast you sober up (`DecayRate`), how
  much a gulp of water clears from your stomach (`WaterClearsStomach`), and how much faster you sober
  up asleep (`SoberingAsleep`).
- **Blackout**: the blood alcohol that puts you down (`Threshold`, default 150; a sip of rum is 18, wine
  12, beer 6), where the tunnel vision warning starts, the time warp, how long you stay out, how well you
  rest, and the hangover.
- **Drunk Effects**: each effect has its own switch and strength.
- **Falling**: physics or scripted fall, and how hard you go over. The scripted fall is also used below
  decks where there is no room to fall.
- **Testing**: `HoldDrunkAt` keeps you at least that drunk from spawn without drinking, for tuning the
  effects. `BlackoutKey` blacks you out when pressed. Both are off by default.

Notes:

- F1 is also Sailwind's sailing-hint key, so opening the config menu shows a hint as well.
- Set `DecayRate` to 0 to hold your drunkenness steady while tuning the effects.
- Settings marked advanced are safety limits for the fall and the hangover. Configuration Manager hides
  them unless "Show advanced" is ticked.

## Building

```
dotnet build "src\ThreeSheets\ThreeSheets.csproj" -c Release -p:GameDir="C:\Program Files (x86)\Steam\steamapps\common\Sailwind"
```

The `MSB3245` warning about `UnityEngine.InputLegacyModule` is expected.

## License

MIT. `src/ThreeSheets/ConfigurationManagerAttributes.cs` is from the BepInEx Configuration Manager
project and stays under its license, see [LICENSE](LICENSE).
