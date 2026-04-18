# EquipManager

This repo now includes a working AOSharp plugin project for equipping single items and saved gear presets.

## Included project

- `AOSharpPluginTemplate/AOSharpPluginTemplate.csproj`
- `AOSharpPluginTemplate/AOSharpPluginTemplate.cs`
- `AOSharpPluginTemplate/app.config`

## What the plugin does

- `/equip nanoskills`, `/equip maxar`, and `/equip maxnr` are built-in preset commands you can bind to hotkeys
- `/equip preset <name>` equips a whole named preset
- `/equip preset save <name> <slot> <item name>` saves an item into a preset
- `/equip savecurrent <name>` snapshots your currently equipped setup into a preset
- `/equip scanindex` attempts to run Mali's fast inventory/bank indexing scan
- `/equip find <item name>` checks inventory, open bank bags, and then ItemFinder's index
- `/equip <item name>` queues a single item and equips it to its first supported slot
- `/equip <slot> <item name>` equips a specific slot
- `/equip presets`, `/equip show <name>`, `/equip remove <name> <slot>`, `/equip delete <name>`, `/equip clear`, and `/equip slots` manage presets and the queue

## GUI

Open the in-game window with `/equip ui` or `/equipmanager`.

The window includes:

- quick buttons for `nanoskills`, `maxar`, and `maxnr`
- a `Save Current` button that snapshots the gear you are wearing right now into the selected preset name
- preset editing fields for slot and item name
- an item search field for the first bank/index lookup attempt

## Build notes

The sample managers you shared use:

- `AOSharpSDK` version `1.0.74`
- `Newtonsoft.Json` version `13.0.3`

This starter uses the same package versions, but the project file is SDK-style so `dotnet restore` and `dotnet build` work on this machine.

## Expected output

After building in `Release`, the important files are:

- `AOSharpPluginTemplate/bin/Release/net48/EquipManager.dll`
- `AOSharpPluginTemplate/bin/Release/net48/EquipManager.dll.config`

Based on the plugins you showed me, those files should live alongside `AOSharp.exe` and the other plugin DLLs.

The plugin saves its preset data in `EquipManagerConfig.json` in the plugin directory after you use the save commands.

## Example preset setup

- `/equip preset save nanoskills hud1 Infused Ancient Nano Enhancer`
- `/equip preset save nanoskills hud2 Communications Relay`
- `/equip preset save maxar rhand Dreadloch Modified Shark`
- `/equip preset save maxnr neck Hold Hell at Bay`

After that, bind whichever command you want in AO:

- `/equip nanoskills`
- `/equip maxar`
- `/equip maxnr`

## ItemFinder note

The current `find` implementation works in layers:

- it first checks your inventory
- then it checks currently open bank contents and can move a matching item into inventory
- then it asks Mali's Item Finder index where the item was last seen

`/equip scanindex` is a first reflection-based attempt to trigger Mali's scanner directly so the index can be refreshed from inside this plugin.

Right now the ItemFinder bridge can report indexed bank hits, but its public API still does not expose the exact bag name cleanly enough to auto-pull the correct bank bag on its own.
