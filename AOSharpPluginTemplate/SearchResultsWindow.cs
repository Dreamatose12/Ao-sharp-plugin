using System;
using System.Collections.Generic;
using System.Linq;
using AOSharp.Common.GameData;
using AOSharp.Common.GameData.UI;
using AOSharp.Core;
using AOSharp.Core.Inventory;
using AOSharp.Core.UI;

namespace EquipManager
{
    public partial class EquipManager
    {
        private Window _searchWindow;
        private List<SearchResultRow> _searchRows = new List<SearchResultRow>();
        private int _searchPage;
        private string _searchQuery;
        private string _searchStatus;

        private void ShowSearchResults(string query)
        {
            _searchQuery = query;
            _searchPage = 0;
            _searchRows.Clear();
            var status = new List<string>();
            try
            {
                AddVisibleSearchItems(Inventory.Items.Where(item => item != null && !item.IsEquipped), "Inventory", query);
                foreach (Backpack bag in Inventory.Backpacks.Where(bag => bag != null && bag.IsOpen))
                    AddVisibleSearchItems(bag.Items, "Inventory / " + bag.Name, query);
                var bank = new Bank();
                if (bank.IsOpen)
                {
                    AddVisibleSearchItems(bank.Items, "Bank / main slots", query);
                    foreach (Backpack bag in bank.Backpacks.Where(bag => bag != null && bag.IsOpen))
                        AddVisibleSearchItems(bag.Items, "Bank / " + bag.Name, query);
                }
                else status.Add("Bank closed: its contents were not checked.");
                status.Add("Closed bags are not searched. Open them and refresh.");
            }
            catch (Exception ex)
            {
                status.Add("Visible search incomplete (" + ex.GetType().Name + "). Refresh to retry.");
            }

            IndexLookupResult indexed = TryLookupItemFinder(query);
            if (indexed.Status == IndexLookupStatus.Found)
            {
                var hints = GetVisibleBankBagHints();
                foreach (IndexedLocation match in indexed.Matches)
                    _searchRows.Add(SearchResultRow.FromIndex(match, hints));
                status.Add("Index: showing " + indexed.Matches.Count + " of " + indexed.TotalMatches +
                    " matches. Indexed rows may repeat visible items.");
            }
            else status.Add(indexed.Message);
            _searchRows = _searchRows.OrderByDescending(row =>
                string.Equals(row.Item, query, StringComparison.OrdinalIgnoreCase))
                .ThenBy(row => row.Availability == "Visible now" ? 0 : 1)
                .ThenBy(row => row.Item, StringComparer.OrdinalIgnoreCase).ToList();
            _searchStatus = string.Join("\n", status);
            OpenSearchWindow();
        }

        private void AddVisibleSearchItems(IEnumerable<Item> items, string location, string query)
        {
            foreach (Item item in items.Where(item => item != null && !string.IsNullOrWhiteSpace(item.Name) &&
                item.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0))
                _searchRows.Add(new SearchResultRow {
                    Item = item.Name, QualityLevel = item.QualityLevel.ToString(),
                    Location = location, Availability = "Visible now"
                });
        }

        private void OpenSearchWindow()
        {
            if (_searchWindow == null || !_searchWindow.IsVisible)
            {
                _searchWindow?.Close();
                _searchWindow = Window.CreateFromXml("EquipManager Search",
                    System.IO.Path.Combine(_pluginDir, "UI", "EquipManagerSearch.xml"),
                    new Rect(0, 0, 790, 420), WindowStyle.Default,
                    WindowFlags.AutoScale | WindowFlags.NoFade);
                if (_searchWindow == null)
                {
                    Chat.WriteLine("Could not open search results. Check that UI/EquipManagerSearch.xml is installed.");
                    return;
                }
                if (_searchWindow.FindView("SearchPrevious", out Button previous))
                    previous.Clicked = (sender, button) => { if (_searchPage > 0) _searchPage--; RenderSearchResults(); };
                if (_searchWindow.FindView("SearchNext", out Button next))
                    next.Clicked = (sender, button) => {
                        if ((_searchPage + 1) * 5 < _searchRows.Count) _searchPage++;
                        RenderSearchResults();
                    };
                if (_searchWindow.FindView("SearchRefresh", out Button refresh))
                    refresh.Clicked = (sender, button) => ShowSearchResults(_searchQuery);
                for (int i = 0; i < 5; i++)
                {
                    int rowIndex = i;
                    if (_searchWindow.FindView("SearchItem" + i, out Button itemButton))
                        itemButton.Clicked = (sender, button) => {
                            int index = _searchPage * 5 + rowIndex;
                            if (index < _searchRows.Count)
                            {
                                var row = _searchRows[index];
                                SetSearchText("SearchDetails", row.Item + "\n" + row.Location +
                                    "\nQL " + row.QualityLevel + " | " + row.Availability);
                            }
                        };
                }
                _searchWindow.MoveToCenter();
            }
            _searchWindow.Show(true);
            RenderSearchResults();
        }

        private void RenderSearchResults()
        {
            SetSearchText("SearchTitle", "Results for: " + SearchResultRow.Clip(_searchQuery, 85));
            SetSearchText("SearchStatus", _searchStatus);
            SetSearchText("SearchPage", _searchRows.Count == 0 ? "No matching results" :
                "Page " + (_searchPage + 1) + " of " + ((_searchRows.Count + 4) / 5) +
                " | " + _searchRows.Count + " rows");
            SetSearchText("SearchDetails", "Select an item name for full details.\nVisible now = observed during this search.\nIndexed only = saved location; current availability unverified.");
            for (int i = 0; i < 5; i++)
            {
                int index = _searchPage * 5 + i;
                SearchResultRow row = index < _searchRows.Count ? _searchRows[index] : null;
                if (_searchWindow.FindView("SearchItem" + i, out Button itemButton))
                    itemButton.SetLabel(row == null ? "-" : SearchResultRow.Clip(row.Item, 35));
                SetSearchText("SearchQl" + i, row?.QualityLevel ?? "");
                SetSearchText("SearchLocation" + i, SearchResultRow.Clip(row?.Location, 31));
                SetSearchText("SearchAvailability" + i, row?.Availability ?? "");
            }
        }

        private void SetSearchText(string name, string text)
        {
            if (_searchWindow.FindView(name, out TextView view)) view.Text = text ?? "";
        }
    }
}
