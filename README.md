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

## GUI`

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

## ItemFinder lookup

The proposed lookup checks inventory as before, then the currently open bank and its open bags, then the already-loaded Mali's Item Finder index for the current character.

For visible bank items, an exact name takes priority over a partial match. Multiple matching copies require manual selection. A transfer is requested only for a single visible match while its bank/source bag is open and inventory has space. Success means one request was sent; check inventory to confirm arrival. Failed requests are not retried automatically.

Indexed results are advisory. They show the stored name, QL when available, and recorded location, up to ten matches. An index entry never opens a bank/bag or sends a move. When an indexed bag identity matches exactly one currently visible bank bag with a name, that name is shown as a hint. Otherwise the message explains how to open likely bags, retry the full find command, and refresh stale data through ItemFinder's own scan controls.

The inspected local ItemFinder build stores a bag's game identity instance in ItemContainer.ContainerInstance. ItemContainer has no bag-name property. Its Id/ItemContainerId database keys and SlotInstance must not be treated as a bag identity. This was established by read-only inspection of one installed DLL, not a documented cross-version API guarantee. Bag-name hints therefore require subsequent in-game verification and never authorize a transfer.

The lookup reuses ItemFinder's loaded Database. It does not create another Database or automatically scan. Missing initialization, a busy index, an incompatible interface, query failure, and no matches have distinct messages. The adapter validates the reflected method/property shapes and falls back safely when they differ.

The separate /equip scanindex command is unchanged and remains an experimental reflection-based scan path. The new lookup instructions recommend ItemFinder's normal scan controls.

## Offline verification for this proposal

On Windows with the .NET SDK and .NET Framework 4.8 targeting pack installed, open PowerShell in the repository folder and run:

~~~powershell
dotnet build .\AOSharpPluginTemplate\AOSharpPluginTemplate.csproj -c Release
dotnet run --project .\Lookup.Tests\Lookup.Tests.csproj -c Release
~~~

The plugin build uses the existing AOSharpSDK 1.0.74 and Newtonsoft.Json 13.0.3 package references. The offline console tests have no package dependencies and link the actual proposed lookup/safety helper source. Their ItemFinder model/database objects are synthetic fixtures. They do not load ItemFinder, open its database, launch AO, or send game operations.

Expected test summary: 36 passed; 0 failed.

Covered cases include unavailable/busy/incompatible ItemFinder, malformed metadata and exceptions, current-character scoping, query normalization, reuse of the loaded database, bank-root versus bag locations, unique/duplicate/missing bag hints, QL and multiple results, exact/partial visible-bank matches, full inventory, closed source, and no retry after a failed move.

## Subsequent in-game verification

Use a nonessential item and a disposable preset for checks. The user performs the interactive steps.

1. With ItemFinder loaded normally, search an item indexed in a closed bank bag. Confirm the result is advisory and no bag or item moves.
2. Open the bank while leaving that bag closed. If the bag identity/name is visible, check that the hinted name is the correct bag, including after renaming a bag. Otherwise confirm the fallback instructions are shown.
3. Open the identified bag, wait for contents to appear, and repeat the exact full item-name search. With one visible match and an empty inventory slot, confirm one transfer request and check that the item actually arrives.
4. Repeat with two matching copies/QLs visible. Confirm no transfer is sent and manual selection is requested. Repeat with a full inventory and confirm no request.
5. Move the item manually after indexing, then search again with the relevant contents unavailable. Confirm the message treats the old location as historical. Refresh using ItemFinder's own controls and repeat.
6. Check a bank-root item, a broad query with several results, ItemFinder unloaded, and an index update in progress.
7. Verify existing direct inventory search, inventory-bag search, equipment commands, preset save/equip, and remembered/overflow-bag behavior with a disposable preset.

Offline compilation and synthetic tests do not establish live identity correlation, scanner compatibility, AO UI behavior, or server acceptance of transfer requests.

## Search-results window

The main window's Search button opens a separate read-only results window with Item, QL, Location and Availability columns. Five rows appear per page; Previous and Next navigate the results. Select an item name to see its full stored details. Refresh repeats the current search after opening a bank or bag.

Visible now means the item was observed during that search; it is a snapshot, not a transfer guarantee. Indexed only means the saved location remains unverified. Bank names obtained from index identity correlation are explicitly marked as hints. Indexed rows are retained separately and may repeat visible items. ItemFinder results are limited to ten and the total is displayed; narrow the search when needed. Unknown QL is shown as ?. Blank rows are cleared on each render.

GUI search does not open bags, request moves, or equip items. The /equip find command retains its reviewed behavior. Existing preset controls remain in the original window. The new UI XML is copied by the build and must be distributed alongside EquipManagerWindow.xml.

In-game UI verification remains required: check both windows at your normal resolution/UI scale, long item and bag names, full detail text, empty results, more than five rows, page boundaries, refresh after opening bags, failed/unavailable ItemFinder, closing/reopening the results window, and plugin teardown. No live rendering is claimed by the offline tests.