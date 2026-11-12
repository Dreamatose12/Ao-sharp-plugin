using System;
using System.Collections.Generic;
using System.Linq;

namespace EquipManager
{
    internal sealed class SearchResultRow
    {
        public string Item;
        public string QualityLevel;
        public string Location;
        public string Availability;

        internal static SearchResultRow FromIndex(IndexedLocation match, IEnumerable<BankBagHint> hints)
        {
            string location = match.Container == "Bank" ? "Bank / main slots (recorded)" :
                match.IsBank ? "Bank / bag unknown" : match.Root + " / " + match.Container;
            if (match.IsBank && match.BagInstanceHint.HasValue)
            {
                var bags = hints.Where(bag => bag != null && bag.Instance == match.BagInstanceHint.Value).ToList();
                if (bags.Count == 1 && !string.IsNullOrWhiteSpace(bags[0].Name))
                    location = "Bank / " + bags[0].Name + " (hint)";
            }
            return new SearchResultRow {
                Item = match.Name, QualityLevel = match.QualityLevel?.ToString() ?? "?",
                Location = location, Availability = "Indexed only"
            };
        }

        internal static string Clip(string text, int limit)
        {
            if (string.IsNullOrEmpty(text)) return "";
            return text.Length <= limit ? text : text.Substring(0, limit - 3) + "...";
        }
    }
}
