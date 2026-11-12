using AOSharp.Common.GameData;
using AOSharp.Common.GameData.UI;
using AOSharp.Core;
using AOSharp.Core.Inventory;
using AOSharp.Core.UI;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Item = AOSharp.Core.Inventory.Item;

namespace EquipManager
{
    public class EquipManager : AOPluginEntry
    {
        private static readonly TimeSpan EquipOperationTimeout = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan BagOpenTimeout = TimeSpan.FromSeconds(1.5);
        private static readonly TimeSpan SlotClearTimeout = TimeSpan.FromSeconds(2);

        private static readonly Dictionary<string, EquipSlot> SlotAliases = BuildSlotAliasMap();
        private static readonly Dictionary<string, string> BuiltInPresetAliases =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["nanoskills"] = "nanoskills",
                ["nano"] = "nanoskills",
                ["ns"] = "nanoskills",
                ["maxar"] = "maxar",
                ["ar"] = "maxar",
                ["attackrating"] = "maxar",
                ["attack"] = "maxar",
                ["maxnr"] = "maxnr",
                ["nr"] = "maxnr",
                ["nanoresist"] = "maxnr"
            };

        private readonly Queue<PendingEquipAction> _equipQueue = new Queue<PendingEquipAction>();

        private EquipManagerConfig _config;
        private string _configPath;
        private string _pluginDir;
        private Window _window;
        private TextView _summaryView;
        private TextView _selectedPresetView;
        private TextInputView _presetInput;
        private TextInputView _slotInput;
        private TextInputView _itemInput;
        private TextInputView _findItemInput;
        private readonly List<Button> _presetButtons = new List<Button>();
        private readonly List<Button> _entryButtons = new List<Button>();
        private readonly Dictionary<string, BagReference> _itemHomeBags =
            new Dictionary<string, BagReference>(StringComparer.OrdinalIgnoreCase);
        private string _activePresetName;
        private PendingEquipState _pendingEquipState;
        private PendingBagOpenState _pendingBagOpenState;
        private PendingSlotClearState _pendingSlotClearState;

        [Obsolete("AOSharp currently loads plugins through Run(string pluginDir).")]
        public override void Run(string pluginDir)
        {
            _pluginDir = pluginDir;
            _configPath = Path.Combine(pluginDir, "EquipManagerConfig.json");
            _config = LoadConfig(_configPath);

            Game.OnUpdate += OnUpdate;
            Chat.RegisterCommand("equip", HandleEquipCommand);
            Chat.RegisterCommand("equipmanager", HandleEquipManagerCommand);

            Chat.WriteLine("EquipManager loaded.");
            Chat.WriteLine("Use /equip help for commands or /equip ui to open the window.");
        }

        public override void Teardown()
        {
            Game.OnUpdate -= OnUpdate;
            _equipQueue.Clear();
            _pendingEquipState = null;
            _pendingBagOpenState = null;
            _pendingSlotClearState = null;
            _window?.Close();
            Chat.WriteLine("EquipManager unloaded.");
        }

        private void HandleEquipManagerCommand(string command, string[] parameters, ChatWindow chatWindow)
        {
            ShowWindow();
        }

        private void HandleEquipCommand(string command, string[] parameters, ChatWindow chatWindow)
        {
            if (parameters == null || parameters.Length == 0)
            {
                WriteHelp();
                return;
            }

            string action = parameters[0].ToLowerInvariant();

            switch (action)
            {
                case "help":
                    WriteHelp();
                    return;
                case "overflow":
                    HandleOverflowCommand(parameters);
                    return;
                case "ui":
                    ShowWindow();
                    return;
                case "presets":
                    ListPresets();
                    return;
                case "savecurrent":
                    SaveCurrentlyEquipped(parameters);
                    return;
                case "scanindex":
                    HandleScanIndexCommand();
                    return;
                case "find":
                    HandleFindCommand(parameters);
                    return;
                case "preset":
                    HandlePresetCommand(parameters);
                    return;
                case "slots":
                    WriteSlots();
                    return;
                case "list":
                    ListSets();
                    return;
                case "show":
                    ShowSet(parameters);
                    return;
                case "set":
                    EquipSet(parameters);
                    return;
                case "save":
                    SaveSetEntry(parameters);
                    return;
                case "remove":
                    RemoveSetEntry(parameters);
                    return;
                case "delete":
                    DeleteSet(parameters);
                    return;
                case "clear":
                    _equipQueue.Clear();
                    Chat.WriteLine("Equip queue cleared.");
                    return;
                default:
                    if (TryResolvePresetName(action, out string presetName))
                    {
                        QueueSetByName(presetName);
                        return;
                    }

                    QueueSingleEquip(parameters);
                    return;
            }
        }

        private void HandlePresetCommand(string[] parameters)
        {
            if (parameters.Length < 2)
            {
                Chat.WriteLine("Usage: /equip preset <name>");
                Chat.WriteLine("Usage: /equip preset save <name> <slot> <item name>");
                Chat.WriteLine("Usage: /equip preset delete <name>");
                return;
            }

            string subCommand = parameters[1].ToLowerInvariant();

            if (subCommand == "save")
            {
                if (parameters.Length < 5)
                {
                    Chat.WriteLine("Usage: /equip preset save <name> <slot> <item name>");
                    return;
                }

                SaveSetEntry(new[]
                {
                    "save",
                    NormalizePresetName(parameters[2]),
                    parameters[3]
                }.Concat(parameters.Skip(4)).ToArray());
                return;
            }

            if (subCommand == "delete")
            {
                if (parameters.Length < 3)
                {
                    Chat.WriteLine("Usage: /equip preset delete <name>");
                    return;
                }

                DeleteSet(new[] { "delete", NormalizePresetName(parameters[2]) });
                return;
            }

            QueueSetByName(NormalizePresetName(parameters[1]));
        }

        private void HandleOverflowCommand(string[] parameters)
        {
            if (parameters.Length == 1)
            {
                Chat.WriteLine(string.IsNullOrWhiteSpace(_config.OverflowBagName)
                    ? "No overflow bag is configured."
                    : $"Overflow bag: {_config.OverflowBagName}");
                return;
            }

            string value = JoinArguments(parameters, 1);
            if (string.Equals(value, "clear", StringComparison.OrdinalIgnoreCase))
            {
                _config.OverflowBagName = null;
                SaveConfig();
                Chat.WriteLine("Overflow bag cleared.");
                return;
            }

            _config.OverflowBagName = value.Trim();
            SaveConfig();
            Chat.WriteLine($"Overflow bag set to '{_config.OverflowBagName}'.");
        }

        private void QueueSingleEquip(string[] parameters)
        {
            if (parameters.Length == 0)
            {
                WriteHelp();
                return;
            }

            int nameStartIndex = 0;
            EquipSlot? targetSlot = null;

            if (TryParseSlot(parameters[0], out EquipSlot parsedSlot))
            {
                targetSlot = parsedSlot;
                nameStartIndex = 1;
            }

            string itemName = JoinArguments(parameters, nameStartIndex);

            if (string.IsNullOrWhiteSpace(itemName))
            {
                Chat.WriteLine("Usage: /equip [slot] <item name>");
                return;
            }

            _equipQueue.Enqueue(new PendingEquipAction(itemName, targetSlot));
            Chat.WriteLine($"Queued '{itemName}'.");
        }

        private void EquipSet(string[] parameters)
        {
            if (parameters.Length < 2)
            {
                Chat.WriteLine("Usage: /equip set <setName>");
                return;
            }

            QueueSetByName(parameters[1]);
        }

        private void QueueSetByName(string setName)
        {
            string normalizedSetName = NormalizePresetName(setName);

            if (!_config.TryGetSet(normalizedSetName, out List<EquipSetEntry> entries))
            {
                Chat.WriteLine($"Preset '{normalizedSetName}' does not exist.");
                return;
            }

            foreach (EquipSetEntry entry in entries)
            {
                if (!TryParseSlot(entry.Slot, out EquipSlot slot))
                {
                    Chat.WriteLine($"Skipping invalid slot '{entry.Slot}' in preset '{normalizedSetName}'.");
                    continue;
                }

                _equipQueue.Enqueue(new PendingEquipAction(entry.ItemName, slot));
            }

            Chat.WriteLine($"Queued {entries.Count} item(s) from preset '{normalizedSetName}'.");
        }

        private void SaveSetEntry(string[] parameters)
        {
            if (parameters.Length < 4)
            {
                Chat.WriteLine("Usage: /equip save <setName> <slot> <item name>");
                return;
            }

            string setName = NormalizePresetName(parameters[1]);
            string slotToken = parameters[2];

            if (!TryParseSlot(slotToken, out EquipSlot slot))
            {
                Chat.WriteLine($"Unknown slot '{slotToken}'. Use /equip slots to see valid names.");
                return;
            }

            string itemName = JoinArguments(parameters, 3);

            if (string.IsNullOrWhiteSpace(itemName))
            {
                Chat.WriteLine("Usage: /equip save <setName> <slot> <item name>");
                return;
            }

            _config.Upsert(setName, slot.ToString(), itemName);
            SaveConfig();
            _activePresetName = setName;

            Chat.WriteLine($"Saved '{itemName}' to preset '{setName}' for slot '{slot}'.");
        }

        private void RemoveSetEntry(string[] parameters)
        {
            if (parameters.Length < 3)
            {
                Chat.WriteLine("Usage: /equip remove <setName> <slot>");
                return;
            }

            string setName = NormalizePresetName(parameters[1]);
            string slotToken = parameters[2];

            if (!TryParseSlot(slotToken, out EquipSlot slot))
            {
                Chat.WriteLine($"Unknown slot '{slotToken}'. Use /equip slots to see valid names.");
                return;
            }

            if (_config.Remove(setName, slot.ToString()))
            {
                SaveConfig();
                Chat.WriteLine($"Removed slot '{slot}' from preset '{setName}'.");
                return;
            }

            Chat.WriteLine($"No saved entry for slot '{slot}' in preset '{setName}'.");
        }

        private void DeleteSet(string[] parameters)
        {
            if (parameters.Length < 2)
            {
                Chat.WriteLine("Usage: /equip delete <setName>");
                return;
            }

            string setName = NormalizePresetName(parameters[1]);

            if (_config.DeleteSet(setName))
            {
                SaveConfig();
                if (string.Equals(_activePresetName, setName, StringComparison.OrdinalIgnoreCase))
                {
                    _activePresetName = _config.Sets.Keys
                        .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                        .FirstOrDefault();
                }
                Chat.WriteLine($"Deleted preset '{setName}'.");
                return;
            }

            Chat.WriteLine($"Preset '{setName}' does not exist.");
        }

        private void ShowSet(string[] parameters)
        {
            if (parameters.Length < 2)
            {
                Chat.WriteLine("Usage: /equip show <setName>");
                return;
            }

            string setName = NormalizePresetName(parameters[1]);

            if (!_config.TryGetSet(setName, out List<EquipSetEntry> entries))
            {
                Chat.WriteLine($"Preset '{setName}' does not exist.");
                return;
            }

            Chat.WriteLine($"Preset '{setName}':");

            foreach (EquipSetEntry entry in entries.OrderBy(x => x.Slot, StringComparer.OrdinalIgnoreCase))
            {
                Chat.WriteLine($"  {entry.Slot} -> {entry.ItemName}");
            }
        }

        private void ListSets()
        {
            if (_config.Sets.Count == 0)
            {
                Chat.WriteLine("No equip presets saved yet.");
                return;
            }

            Chat.WriteLine("Saved equip presets:");

            foreach (string setName in _config.Sets.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                Chat.WriteLine($"  {setName} ({_config.Sets[setName].Count} item(s))");
            }
        }

        private void ListPresets()
        {
            Chat.WriteLine("Built-in preset aliases:");
            Chat.WriteLine("  nanoskills, maxar, maxnr");
            ListSets();
        }

        private void ShowWindow()
        {
            if (_window != null && _window.IsVisible)
            {
                _window.Show(true);
                RefreshSummary();
                return;
            }

            string xmlPath = Path.Combine(_pluginDir, "UI", "EquipManagerWindow.xml");
            _window = Window.CreateFromXml(
                "EquipManager",
                xmlPath,
                new Rect(0, 0, 470, 760),
                WindowStyle.Default,
                WindowFlags.AutoScale | WindowFlags.NoFade);

            _window?.Show(true);
            _window?.MoveToCenter();

            BindWindowControls();
            RefreshSummary();
        }

        private void BindWindowControls()
        {
            if (_window == null)
            {
                return;
            }

            _window.FindView("SummaryView", out _summaryView);
            _window.FindView("SelectedPresetView", out _selectedPresetView);
            _window.FindView("PresetNameInput", out _presetInput);
            _window.FindView("SlotInput", out _slotInput);
            _window.FindView("ItemNameInput", out _itemInput);
            _window.FindView("FindItemInput", out _findItemInput);
            CacheButtons("PresetButton", 12, _presetButtons);
            CacheButtons("EntryButton", 24, _entryButtons);

            if (_window.FindView("PresetNanoButton", out Button nanoButton))
            {
                nanoButton.Clicked = HandlePresetButtonClick;
                nanoButton.Tag = "nanoskills";
            }

            if (_window.FindView("PresetArButton", out Button arButton))
            {
                arButton.Clicked = HandlePresetButtonClick;
                arButton.Tag = "maxar";
            }

            if (_window.FindView("PresetNrButton", out Button nrButton))
            {
                nrButton.Clicked = HandlePresetButtonClick;
                nrButton.Tag = "maxnr";
            }

            if (_window.FindView("ApplyPresetButton", out Button applyButton))
            {
                applyButton.Clicked = HandleApplyPresetClick;
            }

            if (_window.FindView("SaveEntryButton", out Button saveButton))
            {
                saveButton.Clicked = HandleSaveEntryClick;
            }

            if (_window.FindView("RemoveSlotButton", out Button removeButton))
            {
                removeButton.Clicked = HandleRemoveEntryClick;
            }

            if (_window.FindView("DeletePresetButton", out Button deleteButton))
            {
                deleteButton.Clicked = HandleDeletePresetClick;
            }

            if (_window.FindView("RefreshSummaryButton", out Button refreshButton))
            {
                refreshButton.Clicked = HandleRefreshSummaryClick;
            }

            if (_window.FindView("FindItemButton", out Button findItemButton))
            {
                findItemButton.Clicked = HandleFindItemClick;
            }

            if (_window.FindView("SaveCurrentButton", out Button saveCurrentButton))
            {
                saveCurrentButton.Clicked = HandleSaveCurrentClick;
            }

            if (_window.FindView("ClearQueueButton", out Button clearQueueButton))
            {
                clearQueueButton.Clicked = HandleClearQueueClick;
            }
        }

        private void HandlePresetButtonClick(object sender, ButtonBase button)
        {
            string presetName = button.Tag as string;
            if (string.IsNullOrWhiteSpace(presetName))
            {
                return;
            }

            if (_presetInput != null)
            {
                _presetInput.Text = presetName;
            }

            _activePresetName = NormalizePresetName(presetName);
            QueueSetByName(presetName);
            RefreshSummary();
        }

        private void HandleApplyPresetClick(object sender, ButtonBase button)
        {
            string presetName = GetPresetInput();
            if (string.IsNullOrWhiteSpace(presetName))
            {
                Chat.WriteLine("Enter a preset name first.");
                return;
            }

            QueueSetByName(presetName);
            _activePresetName = presetName;
            RefreshSummary();
        }

        private void HandleSaveEntryClick(object sender, ButtonBase button)
        {
            string presetName = GetPresetInput();
            string slotName = _slotInput?.Text?.Trim();
            string itemName = _itemInput?.Text?.Trim();

            if (string.IsNullOrWhiteSpace(presetName) ||
                string.IsNullOrWhiteSpace(slotName) ||
                string.IsNullOrWhiteSpace(itemName))
            {
                Chat.WriteLine("Preset, slot, and item name are all required.");
                return;
            }

            SaveSetEntry(new[] { "save", presetName, slotName, itemName });
            RefreshSummary();
        }

        private void HandleRemoveEntryClick(object sender, ButtonBase button)
        {
            string presetName = GetPresetInput();
            string slotName = _slotInput?.Text?.Trim();

            if (string.IsNullOrWhiteSpace(presetName) || string.IsNullOrWhiteSpace(slotName))
            {
                Chat.WriteLine("Preset and slot are required to remove an entry.");
                return;
            }

            RemoveSetEntry(new[] { "remove", presetName, slotName });
            RefreshSummary();
        }

        private void HandleDeletePresetClick(object sender, ButtonBase button)
        {
            string presetName = GetPresetInput();
            if (string.IsNullOrWhiteSpace(presetName))
            {
                Chat.WriteLine("Enter a preset name first.");
                return;
            }

            DeleteSet(new[] { "delete", presetName });
            RefreshSummary();
        }

        private void HandleRefreshSummaryClick(object sender, ButtonBase button)
        {
            if (!string.IsNullOrWhiteSpace(_presetInput?.Text))
            {
                _activePresetName = GetPresetInput();
            }

            RefreshSummary();
        }

        private void HandleEntryButtonClick(object sender, ButtonBase button)
        {
            if (!(button.Tag is EquipSetEntry entry))
            {
                return;
            }

            if (!TryParseSlot(entry.Slot, out EquipSlot slot))
            {
                Chat.WriteLine($"Invalid saved slot '{entry.Slot}'.");
                return;
            }

            _equipQueue.Enqueue(new PendingEquipAction(entry.ItemName, slot));
            Chat.WriteLine($"Queued '{entry.ItemName}' for slot '{entry.Slot}'.");
            RefreshSummary();
        }

        private void HandleFindItemClick(object sender, ButtonBase button)
        {
            string itemName = _findItemInput?.Text?.Trim();
            if (string.IsNullOrWhiteSpace(itemName))
            {
                Chat.WriteLine("Enter an item name to search for.");
                return;
            }

            HandleFindCommand(new[] { "find", itemName });
        }

        private void HandleSaveCurrentClick(object sender, ButtonBase button)
        {
            string presetName = GetPresetInput();
            if (string.IsNullOrWhiteSpace(presetName))
            {
                Chat.WriteLine("Enter a preset name first.");
                return;
            }

            SaveCurrentIntoPreset(presetName);
            RefreshSummary();
        }

        private void HandleClearQueueClick(object sender, ButtonBase button)
        {
            _equipQueue.Clear();
            Chat.WriteLine("Equip queue cleared.");
            RefreshSummary();
        }

        private void RefreshSummary()
        {
            if (_summaryView == null)
            {
                return;
            }

            var lines = new List<string>
            {
                "EquipManager",
                "Built-in hotkeys: nanoskills, maxar, maxnr",
                $"Queued actions: {_equipQueue.Count}",
                $"Overflow bag: {(_config?.OverflowBagName ?? "(none)")}",
                string.Empty
            };

            foreach (string setName in _config.Sets.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                lines.Add($"[{setName}]");

                foreach (EquipSetEntry entry in _config.Sets[setName].OrderBy(x => x.Slot, StringComparer.OrdinalIgnoreCase))
                {
                    lines.Add($"{entry.Slot}: {entry.ItemName}");
                }

                lines.Add(string.Empty);
            }

            if (_config.Sets.Count == 0)
            {
                lines.Add("No presets saved yet.");
                lines.Add("Save entries below, then use the quick buttons or hotkeys.");
            }

            if (string.IsNullOrWhiteSpace(_activePresetName))
            {
                _activePresetName = _config.Sets.Keys
                    .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
            }

            _summaryView.Text = string.Join("\n", lines);
            RenderPresetButtons();
            RenderEntryButtons();
            RefreshSelectedPresetLabel();
        }

        private void CacheButtons(string prefix, int count, List<Button> target)
        {
            target.Clear();

            if (_window == null)
            {
                return;
            }

            for (int i = 1; i <= count; i++)
            {
                if (_window.FindView($"{prefix}{i}", out Button button))
                {
                    target.Add(button);
                }
            }
        }

        private void RenderPresetButtons()
        {
            List<string> presetNames = _config.Sets.Keys
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList();

            for (int i = 0; i < _presetButtons.Count; i++)
            {
                Button button = _presetButtons[i];

                if (i >= presetNames.Count)
                {
                    button.Show(false, false);
                    button.Enable(false);
                    button.Tag = null;
                    continue;
                }

                string presetName = presetNames[i];
                int pieceCount = _config.Sets[presetName].Count;

                button.SetLabel($"{presetName} ({pieceCount})");
                button.Tag = presetName;
                button.Clicked = HandleDynamicPresetButtonClick;
                button.Enable(true);
                button.Show(true, false);
            }
        }

        private void RenderEntryButtons()
        {
            List<EquipSetEntry> entries = GetActivePresetEntries();

            for (int i = 0; i < _entryButtons.Count; i++)
            {
                Button button = _entryButtons[i];

                if (i >= entries.Count)
                {
                    button.Show(false, false);
                    button.Enable(false);
                    button.Tag = null;
                    continue;
                }

                EquipSetEntry entry = entries[i];
                button.SetLabel($"{entry.Slot}: {entry.ItemName}");
                button.Tag = entry;
                button.Clicked = HandleEntryButtonClick;
                button.Enable(true);
                button.Show(true, false);
            }
        }

        private void HandleDynamicPresetButtonClick(object sender, ButtonBase button)
        {
            string presetName = button.Tag as string;
            if (string.IsNullOrWhiteSpace(presetName))
            {
                return;
            }

            _activePresetName = NormalizePresetName(presetName);

            if (_presetInput != null)
            {
                _presetInput.Text = _activePresetName;
            }

            QueueSetByName(_activePresetName);
            RefreshSummary();
        }

        private List<EquipSetEntry> GetActivePresetEntries()
        {
            if (string.IsNullOrWhiteSpace(_activePresetName) ||
                !_config.TryGetSet(_activePresetName, out List<EquipSetEntry> entries))
            {
                return new List<EquipSetEntry>();
            }

            return entries
                .OrderBy(x => x.Slot, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private void RefreshSelectedPresetLabel()
        {
            if (_selectedPresetView == null)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(_activePresetName))
            {
                _selectedPresetView.Text = "No preset selected";
                return;
            }

            int count = _config.TryGetSet(_activePresetName, out List<EquipSetEntry> entries)
                ? entries.Count
                : 0;

            _selectedPresetView.Text = $"Selected: {_activePresetName} ({count} items)";
        }

        private void WriteHelp()
        {
            Chat.WriteLine("/equip nanoskills, /equip maxar, and /equip maxnr are hotkey-friendly preset commands.");
            Chat.WriteLine("/equip overflow <bag name> sets the fallback bag for removed items. /equip overflow clear removes it.");
            Chat.WriteLine("/equip ui opens the in-game preset manager window.");
            Chat.WriteLine("/equip savecurrent <name> snapshots currently equipped items into a preset.");
            Chat.WriteLine("/equip scanindex attempts to run Mali's fast inventory/bank indexing scan.");
            Chat.WriteLine("/equip find <item name> attempts an ItemFinder/bank lookup for missing items.");
            Chat.WriteLine("/equip preset <name> equips a named preset.");
            Chat.WriteLine("/equip preset save <name> <slot> <item name> saves an item into a preset.");
            Chat.WriteLine("/equip presets lists built-in aliases and your saved presets.");
            Chat.WriteLine("/equip <item name> equips the first supported slot for that item.");
            Chat.WriteLine("/equip <slot> <item name> equips a specific slot.");
            Chat.WriteLine("/equip save <set> <slot> <item name> saves one set entry.");
            Chat.WriteLine("/equip set <set> equips every item saved in the set.");
            Chat.WriteLine("/equip show <set>, /equip list, /equip remove <set> <slot>, /equip delete <set>, /equip clear, /equip slots");
        }

        private void WriteSlots()
        {
            Chat.WriteLine("Valid slots:");
            Chat.WriteLine(string.Join(", ", Enum.GetNames(typeof(EquipSlot))));
        }

        private void OnUpdate(object sender, float deltaTime)
        {
            try
            {
                if (_pendingBagOpenState != null)
                {
                    bool bagWaitExpired = DateTime.UtcNow - _pendingBagOpenState.StartedAtUtc >= BagOpenTimeout;

                    if (!bagWaitExpired &&
                        !TryFindAccessibleItem(_pendingBagOpenState.Action.ItemName, out _, out _, out _))
                    {
                        return;
                    }

                    PendingEquipAction deferredAction = _pendingBagOpenState.Action;
                    _pendingBagOpenState = null;
                    ProcessEquipAction(deferredAction);
                    return;
                }

                if (_pendingSlotClearState != null)
                {
                    if (!TryFindEquippedItemInSlot(_pendingSlotClearState.TargetSlot, out _))
                    {
                        PendingEquipAction deferredAction = _pendingSlotClearState.Action;
                        Chat.WriteLine($"Cleared '{_pendingSlotClearState.TargetSlot}'.");
                        _pendingSlotClearState = null;
                        ProcessEquipAction(deferredAction);
                        return;
                    }

                    if (DateTime.UtcNow - _pendingSlotClearState.StartedAtUtc >= SlotClearTimeout)
                    {
                        Chat.WriteLine($"Timed out waiting for '{_pendingSlotClearState.TargetSlot}' to clear.");
                        _pendingSlotClearState = null;
                    }
                    else
                    {
                        return;
                    }
                }

                if (_pendingEquipState != null)
                {
                    if (TryResolvePendingEquip(out string completionMessage))
                    {
                        if (!string.IsNullOrWhiteSpace(completionMessage))
                        {
                            Chat.WriteLine(completionMessage);
                        }

                        _pendingEquipState = null;
                    }
                    else if (DateTime.UtcNow - _pendingEquipState.StartedAtUtc >= EquipOperationTimeout)
                    {
                        Chat.WriteLine($"Timed out waiting for '{_pendingEquipState.ItemName}' to equip to '{_pendingEquipState.TargetSlot}'.");
                        _pendingEquipState = null;
                    }
                    else
                    {
                        return;
                    }
                }

                if (_equipQueue.Count == 0)
                {
                    return;
                }

                PendingEquipAction action = _equipQueue.Dequeue();
                ProcessEquipAction(action);
            }
            catch (Exception ex)
            {
                _equipQueue.Clear();
                _pendingEquipState = null;
                _pendingBagOpenState = null;
                _pendingSlotClearState = null;
                Chat.WriteLine($"EquipManager update error: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private void ProcessEquipAction(PendingEquipAction action)
        {
            try
            {
                if (!TryFindAccessibleItem(action.ItemName, out Item item, out string sourceDescription, out BagReference sourceBagReference))
                {
                    if (action.AttemptCount == 0 &&
                        TryOpenClosedInventoryBags(out int openedBagCount))
                    {
                        _pendingBagOpenState = new PendingBagOpenState(
                            action.WithAttemptCount(action.AttemptCount + 1),
                            DateTime.UtcNow);

                        Chat.WriteLine($"Opening {openedBagCount} inventory bag(s) to look for '{action.ItemName}'.");
                        return;
                    }

                    Chat.WriteLine($"Could not find '{action.ItemName}' in inventory or open inventory bags.");
                    return;
                }

                if (action.TargetSlot.HasValue &&
                    TryFindEquippedItemInSlot(action.TargetSlot.Value, out Item equippedItem) &&
                    IsMatchingItemName(equippedItem?.Name, action.ItemName))
                {
                    Chat.WriteLine($"'{equippedItem.Name}' is already equipped in '{action.TargetSlot.Value}'.");
                    return;
                }

                if (!TryResolveTargetSlot(item, action.TargetSlot, out EquipSlot slot, out string errorMessage))
                {
                    Chat.WriteLine($"Could not equip '{action.ItemName}': {errorMessage}");
                    return;
                }

                RememberItemHomeBag(item, sourceBagReference);

                if (TryFindEquippedItemInSlot(slot, out Item currentlyEquippedItem) &&
                    !IsMatchingItemName(currentlyEquippedItem?.Name, action.ItemName))
                {
                    try
                    {
                        if (!TryStoreClearedItem(currentlyEquippedItem, out string destinationDescription, out string storageError))
                        {
                            Chat.WriteLine($"Could not clear '{slot}': {storageError}");
                            return;
                        }

                        _pendingSlotClearState = new PendingSlotClearState(
                            action.WithAttemptCount(action.AttemptCount + 1),
                            slot,
                            DateTime.UtcNow);

                        Chat.WriteLine($"Clearing '{slot}' by moving '{currentlyEquippedItem.Name}' to {destinationDescription}.");
                        return;
                    }
                    catch (Exception ex)
                    {
                        Chat.WriteLine($"Could not clear '{slot}': {ex.Message}");
                        return;
                    }
                }

                if (!TryEquipItemToSlot(item, slot, out errorMessage))
                {
                    Chat.WriteLine($"Could not equip '{action.ItemName}': {errorMessage}");
                    return;
                }

                _pendingEquipState = new PendingEquipState(item.Name ?? action.ItemName, slot, DateTime.UtcNow);
                string sourceSuffix = string.IsNullOrWhiteSpace(sourceDescription) ? string.Empty : $" from {sourceDescription}";
                Chat.WriteLine($"Starting equip of '{item.Name}' to '{slot}'{sourceSuffix}.");
            }
            catch (Exception ex)
            {
                Chat.WriteLine($"EquipManager error for '{action.ItemName}': {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static bool TryFindItem(string itemName, out Item item)
        {
            List<Item> exactMatches = Inventory.FindAll(itemName, false);

            item = exactMatches.FirstOrDefault();
            if (item != null)
            {
                return true;
            }

            item = Inventory.Items.FirstOrDefault(candidate =>
                !candidate.IsEquipped &&
                !string.IsNullOrWhiteSpace(candidate.Name) &&
                candidate.Name.IndexOf(itemName, StringComparison.OrdinalIgnoreCase) >= 0);

            return item != null;
        }

        private static bool TryFindAccessibleItem(string itemName, out Item item, out string sourceDescription, out BagReference sourceBagReference)
        {
            if (TryFindItem(itemName, out item))
            {
                sourceDescription = null;
                sourceBagReference = null;
                return true;
            }

            foreach (Backpack backpack in Inventory.Backpacks)
            {
                item = FindMatchingItem(backpack.Items, itemName);
                if (item != null)
                {
                    sourceDescription = string.IsNullOrWhiteSpace(backpack.Name) ? "an inventory bag" : backpack.Name;
                    sourceBagReference = new BagReference(backpack.Identity, sourceDescription);
                    return true;
                }
            }

            sourceDescription = null;
            sourceBagReference = null;
            item = null;
            return false;
        }

        private static bool TryOpenClosedInventoryBags(out int openedBagCount)
        {
            openedBagCount = 0;

            foreach (Backpack backpack in Inventory.Backpacks.Where(candidate => !candidate.IsOpen).ToList())
            {
                Item bagItem = Inventory.Items.FirstOrDefault(candidate =>
                    candidate != null &&
                    candidate.UniqueIdentity == backpack.Identity);

                if (bagItem == null)
                {
                    continue;
                }

                try
                {
                    bagItem.Use();
                    bagItem.Use();
                    openedBagCount++;
                }
                catch
                {
                }
            }

            return openedBagCount > 0;
        }

        private void RememberItemHomeBag(Item item, BagReference sourceBagReference)
        {
            string itemKey = GetItemIdentityKey(item);
            if (string.IsNullOrWhiteSpace(itemKey) || sourceBagReference == null)
            {
                return;
            }

            _itemHomeBags[itemKey] = sourceBagReference;
        }

        private bool TryStoreClearedItem(Item item, out string destinationDescription, out string errorMessage)
        {
            destinationDescription = null;
            errorMessage = null;

            if (item == null)
            {
                errorMessage = "item reference was null.";
                return false;
            }

            if (TryGetRememberedHomeBag(item, out BagReference homeBag) &&
                TryMoveItemToBag(item, homeBag, out destinationDescription))
            {
                return true;
            }

            if (TryGetOverflowBag(out BagReference overflowBag) &&
                TryMoveItemToBag(item, overflowBag, out destinationDescription))
            {
                RememberItemHomeBag(item, overflowBag);
                return true;
            }

            if (Inventory.NumFreeSlots <= 0)
            {
                errorMessage = "inventory is full and no bag destination was available.";
                return false;
            }

            item.MoveToInventory();
            destinationDescription = "inventory";
            return true;
        }

        private bool TryGetRememberedHomeBag(Item item, out BagReference bagReference)
        {
            string itemKey = GetItemIdentityKey(item);
            if (string.IsNullOrWhiteSpace(itemKey))
            {
                bagReference = null;
                return false;
            }

            return _itemHomeBags.TryGetValue(itemKey, out bagReference);
        }

        private bool TryGetOverflowBag(out BagReference bagReference)
        {
            bagReference = null;

            string bagName = _config?.OverflowBagName?.Trim();
            if (string.IsNullOrWhiteSpace(bagName))
            {
                return false;
            }

            Backpack backpack = Inventory.Backpacks.FirstOrDefault(candidate =>
                !string.IsNullOrWhiteSpace(candidate.Name) &&
                string.Equals(candidate.Name, bagName, StringComparison.OrdinalIgnoreCase));

            if (backpack == null)
            {
                backpack = Inventory.Backpacks.FirstOrDefault(candidate =>
                    !string.IsNullOrWhiteSpace(candidate.Name) &&
                    candidate.Name.IndexOf(bagName, StringComparison.OrdinalIgnoreCase) >= 0);
            }

            if (backpack == null)
            {
                return false;
            }

            bagReference = new BagReference(backpack.Identity, string.IsNullOrWhiteSpace(backpack.Name) ? bagName : backpack.Name);
            return true;
        }

        private bool TryMoveItemToBag(Item item, BagReference bagReference, out string destinationDescription)
        {
            destinationDescription = null;

            if (item == null || bagReference == null || bagReference.Identity == Identity.None)
            {
                return false;
            }

            if (!TryResolveBackpack(bagReference, out Backpack backpack))
            {
                return false;
            }

            if (!backpack.IsOpen)
            {
                TryOpenBackpack(backpack);
            }

            try
            {
                item.MoveToContainer(backpack.Identity);
                destinationDescription = string.IsNullOrWhiteSpace(backpack.Name) ? "a bag" : backpack.Name;
                string itemKey = GetItemIdentityKey(item);
                if (!string.IsNullOrWhiteSpace(itemKey))
                {
                    _itemHomeBags[itemKey] = new BagReference(backpack.Identity, destinationDescription);
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryResolveBackpack(BagReference bagReference, out Backpack backpack)
        {
            backpack = Inventory.Backpacks.FirstOrDefault(candidate => candidate.Identity == bagReference.Identity);

            if (backpack != null)
            {
                return true;
            }

            if (string.IsNullOrWhiteSpace(bagReference.Name))
            {
                return false;
            }

            backpack = Inventory.Backpacks.FirstOrDefault(candidate =>
                !string.IsNullOrWhiteSpace(candidate.Name) &&
                string.Equals(candidate.Name, bagReference.Name, StringComparison.OrdinalIgnoreCase));

            return backpack != null;
        }

        private static void TryOpenBackpack(Backpack backpack)
        {
            if (backpack == null)
            {
                return;
            }

            Item bagItem = Inventory.Items.FirstOrDefault(candidate =>
                candidate != null &&
                candidate.UniqueIdentity == backpack.Identity);

            if (bagItem == null)
            {
                return;
            }

            try
            {
                bagItem.Use();
                bagItem.Use();
            }
            catch
            {
            }
        }

        private static string GetItemIdentityKey(Item item)
        {
            Identity identity = GetIdentityProperty<Identity>(item, "UniqueIdentity");
            return GetIdentityKey(identity);
        }

        private static string GetIdentityKey(Identity identity)
        {
            return identity == Identity.None ? null : $"{(int)identity.Type}:{identity.Instance}";
        }

        private bool TryResolvePendingEquip(out string completionMessage)
        {
            completionMessage = null;

            if (_pendingEquipState == null)
            {
                return true;
            }

            if (!TryFindEquippedItemInSlot(_pendingEquipState.TargetSlot, out Item equippedItem))
            {
                return false;
            }

            if (!IsMatchingItemName(equippedItem?.Name, _pendingEquipState.ItemName))
            {
                return false;
            }

            completionMessage = $"Equipped '{equippedItem.Name}' to '{_pendingEquipState.TargetSlot}'.";
            return true;
        }

        private static bool TryFindEquippedItemInSlot(EquipSlot slot, out Item equippedItem)
        {
            equippedItem = Inventory.Items.FirstOrDefault(candidate =>
                candidate != null &&
                candidate.IsEquipped &&
                TryMapIdentityToEquipSlot(GetIdentityProperty<Identity>(candidate, "Slot"), out EquipSlot equippedSlot) &&
                equippedSlot == slot);

            return equippedItem != null;
        }

        private static bool IsMatchingItemName(string candidateName, string expectedName)
        {
            if (string.IsNullOrWhiteSpace(candidateName) || string.IsNullOrWhiteSpace(expectedName))
            {
                return false;
            }

            return string.Equals(candidateName, expectedName, StringComparison.OrdinalIgnoreCase) ||
                candidateName.IndexOf(expectedName, StringComparison.OrdinalIgnoreCase) >= 0 ||
                expectedName.IndexOf(candidateName, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool TryResolveTargetSlot(Item item, EquipSlot? requestedSlot, out EquipSlot slot, out string errorMessage)
        {
            slot = default;
            errorMessage = null;

            if (item == null)
            {
                errorMessage = "item reference was null.";
                return false;
            }

            if (requestedSlot.HasValue)
            {
                slot = requestedSlot.Value;

                if (TryGetEquipSlots(item, out List<EquipSlot> equipSlots) &&
                    equipSlots.Count > 0 &&
                    !equipSlots.Contains(slot))
                {
                    errorMessage = $"'{item.Name}' cannot equip to '{slot}'.";
                    return false;
                }

                return true;
            }

            if (!TryGetEquipSlots(item, out List<EquipSlot> availableSlots) || availableSlots.Count == 0)
            {
                errorMessage = "no equip slot was provided and the item did not expose equip slots.";
                return false;
            }

            slot = availableSlots.First();
            return true;
        }

        private static bool TryEquipItemToSlot(Item item, EquipSlot slot, out string errorMessage)
        {
            errorMessage = null;
            try
            {
                item.Equip(slot);
                return true;
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                return false;
            }
        }

        private static bool TryGetEquipSlots(Item item, out List<EquipSlot> equipSlots)
        {
            try
            {
                equipSlots = item?.EquipSlots ?? new List<EquipSlot>();
                return true;
            }
            catch
            {
                equipSlots = new List<EquipSlot>();
                return false;
            }
        }

        private static InventoryBagLookupResult TryResolveInventoryBagItem(string itemName, out string sourceBag, out string foundItemName)
        {
            sourceBag = null;
            foundItemName = null;

            foreach (Backpack backpack in Inventory.Backpacks)
            {
                Item item = FindMatchingItem(backpack.Items, itemName);
                if (item == null)
                {
                    continue;
                }

                try
                {
                    if (Inventory.NumFreeSlots <= 0)
                    {
                        return InventoryBagLookupResult.FoundButInventoryFull;
                    }

                    item.MoveToInventory();
                    sourceBag = string.IsNullOrWhiteSpace(backpack.Name) ? "an inventory bag" : backpack.Name;
                    foundItemName = item.Name;
                    return InventoryBagLookupResult.MovedToInventory;
                }
                catch (Exception)
                {
                    sourceBag = string.IsNullOrWhiteSpace(backpack.Name) ? "an inventory bag" : backpack.Name;
                    foundItemName = item.Name;
                    return InventoryBagLookupResult.MoveFailed;
                }
            }

            return InventoryBagLookupResult.NotFound;
        }

        private static Item FindMatchingItem(IEnumerable<Item> items, string itemName)
        {
            Item exactMatch = items.FirstOrDefault(candidate =>
                !string.IsNullOrWhiteSpace(candidate.Name) &&
                string.Equals(candidate.Name, itemName, StringComparison.OrdinalIgnoreCase));

            if (exactMatch != null)
            {
                return exactMatch;
            }

            return items.FirstOrDefault(candidate =>
                !string.IsNullOrWhiteSpace(candidate.Name) &&
                candidate.Name.IndexOf(itemName, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static bool TryParseSlot(string value, out EquipSlot slot)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                slot = default;
                return false;
            }

            return SlotAliases.TryGetValue(Normalize(value), out slot);
        }

        private static Dictionary<string, EquipSlot> BuildSlotAliasMap()
        {
            var aliases = new Dictionary<string, EquipSlot>(StringComparer.OrdinalIgnoreCase);

            foreach (EquipSlot slot in Enum.GetValues(typeof(EquipSlot)))
            {
                string enumName = slot.ToString();
                aliases[Normalize(enumName)] = slot;

                string[] parts = enumName.Split('_');
                if (parts.Length == 2)
                {
                    aliases[Normalize(parts[0] + "-" + parts[1])] = slot;
                    aliases[Normalize(parts[0] + parts[1])] = slot;
                }
            }

            AddAlias(aliases, EquipSlot.Weap_RightHand, "rhand", "right-hand", "right hand");
            AddAlias(aliases, EquipSlot.Weap_LeftHand, "lhand", "left-hand", "left hand");
            AddAlias(aliases, EquipSlot.Weap_Hud1, "hud1");
            AddAlias(aliases, EquipSlot.Weap_Hud2, "hud2");
            AddAlias(aliases, EquipSlot.Weap_Hud3, "hud3");
            AddAlias(aliases, EquipSlot.Weap_Utils1, "util1");
            AddAlias(aliases, EquipSlot.Weap_Utils2, "util2");
            AddAlias(aliases, EquipSlot.Weap_Utils3, "util3");
            AddAlias(aliases, EquipSlot.Cloth_Body, "body", "chest", "armor-body");
            AddAlias(aliases, EquipSlot.Cloth_Head, "head", "armor-head");
            AddAlias(aliases, EquipSlot.Cloth_Back, "back", "armor-back");
            AddAlias(aliases, EquipSlot.Cloth_Hands, "hands", "gloves");
            AddAlias(aliases, EquipSlot.Cloth_Legs, "legs");
            AddAlias(aliases, EquipSlot.Cloth_Feet, "feet", "boots");
            AddAlias(aliases, EquipSlot.Cloth_Neck, "neck");
            AddAlias(aliases, EquipSlot.Cloth_RightFinger, "right-finger");
            AddAlias(aliases, EquipSlot.Cloth_LeftFinger, "left-finger");
            AddAlias(aliases, EquipSlot.Cloth_RightWrist, "right-wrist");
            AddAlias(aliases, EquipSlot.Cloth_LeftWrist, "left-wrist");
            AddAlias(aliases, EquipSlot.Imp_Head, "imp-head");
            AddAlias(aliases, EquipSlot.Imp_Eye, "imp-eye");
            AddAlias(aliases, EquipSlot.Imp_Ear, "imp-ear");
            AddAlias(aliases, EquipSlot.Imp_Body, "imp-body");
            AddAlias(aliases, EquipSlot.Imp_Waist, "imp-waist");
            AddAlias(aliases, EquipSlot.Imp_Legs, "imp-legs");
            AddAlias(aliases, EquipSlot.Imp_Feet, "imp-feet");
            AddAlias(aliases, EquipSlot.Social_Head, "social-head");
            AddAlias(aliases, EquipSlot.Social_Back, "social-back");
            AddAlias(aliases, EquipSlot.Social_Chest, "social-chest");

            return aliases;
        }

        private static void AddAlias(Dictionary<string, EquipSlot> aliases, EquipSlot slot, params string[] values)
        {
            foreach (string value in values)
            {
                aliases[Normalize(value)] = slot;
            }
        }

        private static string Normalize(string value)
        {
            return new string(value
                .Where(char.IsLetterOrDigit)
                .Select(char.ToLowerInvariant)
                .ToArray());
        }

        private static string JoinArguments(string[] values, int startIndex)
        {
            if (values.Length <= startIndex)
            {
                return string.Empty;
            }

            return string.Join(" ", values.Skip(startIndex)).Trim();
        }

        private static bool TryResolvePresetName(string value, out string presetName)
        {
            if (BuiltInPresetAliases.TryGetValue(NormalizePresetName(value), out presetName))
            {
                return true;
            }

            presetName = null;
            return false;
        }

        private static string NormalizePresetName(string value)
        {
            return Normalize(value);
        }

        private string GetPresetInput()
        {
            return NormalizePresetName(_presetInput?.Text?.Trim());
        }

        private void SaveCurrentlyEquipped(string[] parameters)
        {
            if (parameters.Length < 2)
            {
                Chat.WriteLine("Usage: /equip savecurrent <presetName>");
                return;
            }

            SaveCurrentIntoPreset(parameters[1]);
        }

        private void SaveCurrentIntoPreset(string presetName)
        {
            string normalizedPresetName = NormalizePresetName(presetName);
            List<EquipSetEntry> entries = BuildCurrentPresetEntries();

            if (entries.Count == 0)
            {
                Chat.WriteLine("Could not find any currently equipped items to save.");
                return;
            }

            _config.Sets[normalizedPresetName] = entries;
            SaveConfig();
            _activePresetName = normalizedPresetName;

            Chat.WriteLine($"Saved {entries.Count} currently equipped item(s) into preset '{normalizedPresetName}'.");
        }

        private static List<EquipSetEntry> BuildCurrentPresetEntries()
        {
            var entries = new List<EquipSetEntry>();

            foreach (Item item in Inventory.Items)
            {
                Identity slotIdentity = GetIdentityProperty<Identity>(item, "Slot");

                if (!TryMapIdentityToEquipSlot(slotIdentity, out EquipSlot equipSlot))
                {
                    continue;
                }

                string itemName = item.Name;
                if (string.IsNullOrWhiteSpace(itemName))
                {
                    continue;
                }

                entries.Add(new EquipSetEntry
                {
                    Slot = equipSlot.ToString(),
                    ItemName = itemName
                });
            }

            return entries
                .GroupBy(entry => entry.Slot, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderBy(entry => entry.Slot, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static bool TryMapIdentityToEquipSlot(Identity identity, out EquipSlot equipSlot)
        {
            if (identity == Identity.None)
            {
                equipSlot = default;
                return false;
            }

            if (identity.Type != IdentityType.WeaponPage &&
                identity.Type != IdentityType.ArmorPage &&
                identity.Type != IdentityType.ImplantPage &&
                identity.Type != IdentityType.SocialPage)
            {
                equipSlot = default;
                return false;
            }

            if (!Enum.IsDefined(typeof(EquipSlot), identity.Instance))
            {
                equipSlot = default;
                return false;
            }

            equipSlot = (EquipSlot)identity.Instance;
            return true;
        }

        private void HandleFindCommand(string[] parameters)
        {
            string itemName = JoinArguments(parameters, 1);
            if (string.IsNullOrWhiteSpace(itemName))
            {
                Chat.WriteLine("Usage: /equip find <item name>");
                return;
            }

            if (TryFindItem(itemName, out Item inventoryItem))
            {
                Chat.WriteLine($"'{inventoryItem.Name}' is already available in inventory.");
                return;
            }

            if (TryOpenClosedInventoryBags(out int openedBagCount))
            {
                Chat.WriteLine($"Opened {openedBagCount} inventory bag(s). Try /equip find {itemName} again in a moment.");
                return;
            }

            InventoryBagLookupResult bagResult = TryResolveInventoryBagItem(itemName, out string sourceBag, out string foundItemName);
            if (bagResult == InventoryBagLookupResult.MovedToInventory)
            {
                Chat.WriteLine($"Moved '{foundItemName}' from {sourceBag} into inventory.");
                return;
            }

            if (bagResult == InventoryBagLookupResult.FoundButInventoryFull)
            {
                Chat.WriteLine($"Found '{foundItemName}' in {sourceBag}, but inventory is full.");
                return;
            }

            if (bagResult == InventoryBagLookupResult.MoveFailed)
            {
                Chat.WriteLine($"Found '{foundItemName}' in {sourceBag}, but failed to move it to inventory.");
                return;
            }

            if (TryHandleVisibleBankLookup(itemName))
            {
                return;
            }

            IndexLookupResult lookup = TryLookupItemFinder(itemName);
            foreach (string line in ItemFinderLookup.Describe(lookup, itemName, GetVisibleBankBagHints()))
            {
                Chat.WriteLine(line);
            }
        }

        private void HandleScanIndexCommand()
        {
            if (TryStartItemFinderScan(out string message))
            {
                Chat.WriteLine(message);
                return;
            }

            Chat.WriteLine(message);
        }

        private static bool TryHandleVisibleBankLookup(string itemName)
        {
            try
            {
                var bank = new Bank();
                if (!bank.IsOpen) return false;

                var candidates = new List<KeyValuePair<Item, Backpack>>();
                candidates.AddRange(bank.Items.Where(item => item != null)
                    .Select(item => new KeyValuePair<Item, Backpack>(item, null)));
                foreach (Backpack pack in bank.Backpacks.Where(pack => pack != null && pack.IsOpen))
                {
                    candidates.AddRange(pack.Items.Where(item => item != null)
                        .Select(item => new KeyValuePair<Item, Backpack>(item, pack)));
                }

                var matches = BankLookupSafety.SelectMatches(candidates, pair => pair.Key.Name, itemName);
                if (matches.Count == 0) return false;
                if (matches.Count > 1)
                {
                    Chat.WriteLine($"Found {matches.Count} visible bank matches for '{itemName}'; no item was moved. Use the full item name or select the desired copy manually.");
                    return true;
                }

                Item bankItem = matches[0].Key;
                Backpack sourceBag = matches[0].Value;
                string source = sourceBag == null ? "the bank's main slots" :
                    string.IsNullOrWhiteSpace(sourceBag.Name) ? "an open bank bag" : $"bank bag '{sourceBag.Name}'";
                // Recheck the source immediately before requesting a move.
                bool sourceVisible = bank.IsOpen && (sourceBag == null || sourceBag.IsOpen);
                string moveMessage = BankLookupSafety.RequestMove(sourceVisible, Inventory.NumFreeSlots,
                    () => bankItem.MoveToInventory());
                Chat.WriteLine($"Found '{bankItem.Name}' in {source}. {moveMessage}");
                return true;
            }
            catch (Exception ex)
            {
                Chat.WriteLine($"Could not complete the visible bank lookup ({ex.GetType().Name}). Check the bank and inventory before retrying.");
                return true;
            }
        }

        private static List<BankBagHint> GetVisibleBankBagHints()
        {
            try
            {
                var bank = new Bank();
                if (!bank.IsOpen) return new List<BankBagHint>();
                // A closed bag's identity/name can be visible in the bank root even
                // when its contents are unavailable. It is only used for a hint.
                return bank.Backpacks.Where(pack => pack != null)
                    .Select(pack => new BankBagHint { Instance = pack.Identity.Instance, Name = pack.Name })
                    .ToList();
            }
            catch
            {
                // Missing live metadata must not suppress the indexed result or
                // convert an unverified location into a move request.
                return new List<BankBagHint>();
            }
        }

        private static IndexLookupResult TryLookupItemFinder(string itemName)
        {
            Assembly assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(candidate =>
                string.Equals(candidate.GetName().Name, "Malis Item Finder", StringComparison.OrdinalIgnoreCase));
            return ItemFinderLookup.Read(assembly, DynelManager.LocalPlayer?.Name, itemName);
        }

        private static bool TryStartItemFinderScan(out string message)
        {
            message = null;

            string toolkitPath = GetItemFinderPath();
            string assemblyPath = Path.Combine(toolkitPath, "Malis Item Finder.dll");

            if (!File.Exists(assemblyPath))
            {
                message = "Could not find Mali's Item Finder installation.";
                return false;
            }

            try
            {
                AppDomain.CurrentDomain.AssemblyResolve += ResolveItemFinderAssemblies;

                Assembly assembly = Assembly.LoadFrom(assemblyPath);
                Type mainType = assembly.GetType("MalisItemFinder.Main");
                Type settingsType = assembly.GetType("MalisItemFinder.Settings");
                Type databaseType = assembly.GetType("MalisItemFinder.Database");
                Type itemScannerType = assembly.GetType("MalisItemFinder.ItemScanner");

                string settingsPath = Path.Combine(toolkitPath, "JSON", "Settings.json");
                object settings = Activator.CreateInstance(settingsType, settingsPath);
                object database = Activator.CreateInstance(databaseType);
                object scanner = Activator.CreateInstance(itemScannerType);

                databaseType
                    .GetMethod("LoadDb", BindingFlags.NonPublic | BindingFlags.Instance)
                    ?.Invoke(database, Array.Empty<object>());

                SetStaticField(mainType, "PluginDir", toolkitPath);
                SetStaticField(mainType, "Settings", settings);
                SetStaticField(mainType, "Database", database);
                SetStaticField(mainType, "ItemScanner", scanner);

                itemScannerType
                    .GetMethod("Scan", BindingFlags.NonPublic | BindingFlags.Instance)
                    ?.Invoke(scanner, Array.Empty<object>());

                message = "Started Mali ItemFinder scan attempt. If the scanner path is compatible, it should begin indexing inventory and bank bags.";
                return true;
            }
            catch (Exception ex)
            {
                message = $"Failed to start Mali ItemFinder scan: {ex.Message}";
                return false;
            }
            finally
            {
                AppDomain.CurrentDomain.AssemblyResolve -= ResolveItemFinderAssemblies;
            }
        }

        private static Assembly ResolveItemFinderAssemblies(object sender, ResolveEventArgs args)
        {
            string toolkitPath = GetItemFinderPath();

            string assemblyName = new AssemblyName(args.Name).Name + ".dll";
            string candidatePath = Path.Combine(toolkitPath, assemblyName);

            return File.Exists(candidatePath) ? Assembly.LoadFrom(candidatePath) : null;
        }

        private static string GetItemFinderPath()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                "Mali's AO Toolkit",
                "Mali's AO Toolkit",
                "Malis-AO-Toolkit-27-01-26",
                "Mali's AO Toolkit",
                "lib",
                "ItemFinder");
        }

        private static void SetStaticField(Type type, string fieldName, object value)
        {
            type.GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Static)
                ?.SetValue(null, value);
        }

        private static T GetIdentityProperty<T>(object instance, string propertyName)
        {
            if (instance == null)
            {
                return default;
            }

            Type type = instance.GetType();

            object value = type
                .GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance)
                ?.GetValue(instance);

            if (value == null)
            {
                value = type
                    .GetField(propertyName, BindingFlags.Public | BindingFlags.Instance)
                    ?.GetValue(instance);
            }

            return value is T typedValue ? typedValue : default;
        }

        private EquipManagerConfig LoadConfig(string path)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return new EquipManagerConfig();
                }

                var config = JsonConvert.DeserializeObject<EquipManagerConfig>(File.ReadAllText(path));
                return config ?? new EquipManagerConfig();
            }
            catch (Exception ex)
            {
                Chat.WriteLine($"Failed to load config: {ex.Message}");
                return new EquipManagerConfig();
            }
        }

        private void SaveConfig()
        {
            try
            {
                string json = JsonConvert.SerializeObject(_config, Formatting.Indented);
                File.WriteAllText(_configPath, json);
            }
            catch (Exception ex)
            {
                Chat.WriteLine($"Failed to save config: {ex.Message}");
            }
        }
    }

    public class EquipManagerConfig
    {
        public Dictionary<string, List<EquipSetEntry>> Sets { get; set; } =
            new Dictionary<string, List<EquipSetEntry>>(StringComparer.OrdinalIgnoreCase);

        public string OverflowBagName { get; set; }

        public bool TryGetSet(string setName, out List<EquipSetEntry> entries)
        {
            return Sets.TryGetValue(setName, out entries);
        }

        public void Upsert(string setName, string slot, string itemName)
        {
            if (!Sets.TryGetValue(setName, out List<EquipSetEntry> entries))
            {
                entries = new List<EquipSetEntry>();
                Sets[setName] = entries;
            }

            EquipSetEntry existing = entries.FirstOrDefault(entry =>
                string.Equals(entry.Slot, slot, StringComparison.OrdinalIgnoreCase));

            if (existing == null)
            {
                entries.Add(new EquipSetEntry
                {
                    Slot = slot,
                    ItemName = itemName
                });
                return;
            }

            existing.ItemName = itemName;
        }

        public bool Remove(string setName, string slot)
        {
            if (!Sets.TryGetValue(setName, out List<EquipSetEntry> entries))
            {
                return false;
            }

            EquipSetEntry existing = entries.FirstOrDefault(entry =>
                string.Equals(entry.Slot, slot, StringComparison.OrdinalIgnoreCase));

            if (existing == null)
            {
                return false;
            }

            entries.Remove(existing);

            if (entries.Count == 0)
            {
                Sets.Remove(setName);
            }

            return true;
        }

        public bool DeleteSet(string setName)
        {
            return Sets.Remove(setName);
        }
    }

    public class BagReference
    {
        public BagReference(Identity identity, string name)
        {
            Identity = identity;
            Name = name;
        }

        public Identity Identity { get; }

        public string Name { get; }
    }

    public class EquipSetEntry
    {
        public string Slot { get; set; }

        public string ItemName { get; set; }
    }

    public class PendingEquipState
    {
        public PendingEquipState(string itemName, EquipSlot targetSlot, DateTime startedAtUtc)
        {
            ItemName = itemName;
            TargetSlot = targetSlot;
            StartedAtUtc = startedAtUtc;
        }

        public string ItemName { get; }

        public EquipSlot TargetSlot { get; }

        public DateTime StartedAtUtc { get; }
    }

    public class PendingBagOpenState
    {
        public PendingBagOpenState(PendingEquipAction action, DateTime startedAtUtc)
        {
            Action = action;
            StartedAtUtc = startedAtUtc;
        }

        public PendingEquipAction Action { get; }

        public DateTime StartedAtUtc { get; }
    }

    public class PendingSlotClearState
    {
        public PendingSlotClearState(PendingEquipAction action, EquipSlot targetSlot, DateTime startedAtUtc)
        {
            Action = action;
            TargetSlot = targetSlot;
            StartedAtUtc = startedAtUtc;
        }

        public PendingEquipAction Action { get; }

        public EquipSlot TargetSlot { get; }

        public DateTime StartedAtUtc { get; }
    }

    public enum InventoryBagLookupResult
    {
        NotFound,
        MovedToInventory,
        FoundButInventoryFull,
        MoveFailed
    }

    public class PendingEquipAction
    {
        public PendingEquipAction(string itemName, EquipSlot? targetSlot, int attemptCount = 0)
        {
            ItemName = itemName;
            TargetSlot = targetSlot;
            AttemptCount = attemptCount;
        }

        public string ItemName { get; }

        public EquipSlot? TargetSlot { get; }

        public int AttemptCount { get; }

        public PendingEquipAction WithAttemptCount(int attemptCount)
        {
            return new PendingEquipAction(ItemName, TargetSlot, attemptCount);
        }
    }
}
