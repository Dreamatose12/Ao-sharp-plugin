using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;

namespace EquipManager
{
    internal enum IndexLookupStatus { Found, NoMatch, Unavailable, Incompatible, Failed }

    internal sealed class IndexedLocation
    {
        public string Name;
        public int? QualityLevel;
        public string Container;
        public string Root;
        public int? BagInstanceHint;
        public bool IsBank => Container == "Bank" || Root == "Bank";
    }

    internal sealed class IndexLookupResult
    {
        public IndexLookupStatus Status;
        public string Message;
        public int TotalMatches;
        public List<IndexedLocation> Matches = new List<IndexedLocation>();
    }

    internal sealed class BankBagHint
    {
        public int Instance;
        public string Name;
    }

    // This adapter reads the already-loaded ItemFinder instance. It never creates a
    // Database, starts a scan, opens a bag, or sends an inventory operation.
    internal static class ItemFinderLookup
    {
        internal const int ResultLimit = 10;
        private const BindingFlags StaticMembers =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

        internal static IndexLookupResult Read(Assembly assembly, string character, string query)
        {
            if (assembly == null)
                return Result(IndexLookupStatus.Unavailable,
                    "ItemFinder is not loaded; its index was not checked. Load it through your usual AOSharp setup and retry.");
            if (string.IsNullOrWhiteSpace(character) || string.Equals(character, "All", StringComparison.OrdinalIgnoreCase))
                return Result(IndexLookupStatus.Unavailable, "The current character is unavailable; ItemFinder was not queried.");
            if (string.IsNullOrWhiteSpace(query))
                return Result(IndexLookupStatus.NoMatch, "Usage: /equip find <item name>");

            try
            {
                Type main = RequiredType(assembly, "MalisItemFinder.Main");
                Type databaseType = RequiredType(assembly, "MalisItemFinder.Database");
                Type filter = RequiredType(assembly, "FilterCriteria");
                Type search = RequiredType(assembly, "SearchCriteria");
                Type slot = RequiredType(assembly, "EFDataAccessLibrary.Models.Slot");
                Type info = RequiredType(assembly, "EFDataAccessLibrary.Models.ItemInfo");
                Type container = RequiredType(assembly, "EFDataAccessLibrary.Models.ItemContainer");
                Type containerId = RequiredType(assembly, "ContainerId");
                if (!filter.IsEnum || !containerId.IsEnum)
                    throw new NotSupportedException("Expected ItemFinder enum types.");

                FieldInfo databaseField = main.GetField("Database", StaticMembers);
                FieldInfo windowField = main.GetField("MainWindow", StaticMembers);
                if (databaseField == null || databaseField.FieldType != databaseType || windowField == null)
                    throw new NotSupportedException("Expected ItemFinder initialization fields.");
                object database = databaseField.GetValue(null);
                if (database == null || windowField.GetValue(null) == null)
                    return Result(IndexLookupStatus.Unavailable,
                        "ItemFinder is loaded but its index or window is not ready. Finish its normal initialization and retry.");

                Type processor = RequiredType(assembly, "DatabaseProcessor");
                MethodInfo isOccupied = processor.GetMethod("IsOccupied", StaticMembers, null, Type.EmptyTypes, null);
                if (isOccupied == null || isOccupied.ReturnType != typeof(bool))
                    throw new NotSupportedException("Expected ItemFinder update-status method.");
                if ((bool)isOccupied.Invoke(null, null))
                    return Result(IndexLookupStatus.Unavailable, "ItemFinder is updating its index. Wait for it to finish, then retry.");

                Type searchList = typeof(List<>).MakeGenericType(search);
                Type criteriaType = typeof(Dictionary<,>).MakeGenericType(filter, searchList);
                MethodInfo lookup = databaseType.GetMethod("ItemLookup", BindingFlags.Public | BindingFlags.Instance,
                    null, new[] { typeof(string), typeof(IEnumerable<string>), criteriaType,
                        typeof(int), typeof(int).MakeByRefType() }, null);
                if (lookup == null || lookup.ReturnType != typeof(List<>).MakeGenericType(slot))
                    throw new NotSupportedException("Expected ItemFinder lookup signature.");

                PropertyInfo itemProperty = RequiredProperty(slot, "ItemInfo", info);
                PropertyInfo containerProperty = RequiredProperty(slot, "ItemContainer", container);
                PropertyInfo nameProperty = RequiredProperty(info, "Name", typeof(string));
                PropertyInfo instanceProperty = RequiredProperty(container, "ContainerInstance", containerId);
                PropertyInfo rootProperty = RequiredProperty(container, "Root", containerId);
                PropertyInfo qlProperty = info.GetProperty("Ql", BindingFlags.Public | BindingFlags.Instance);

                var criteria = (IDictionary)Activator.CreateInstance(criteriaType);
                foreach (object key in Enum.GetValues(filter))
                    criteria[key] = Activator.CreateInstance(searchList);

                string[] terms = query.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(term => term.ToLowerInvariant()).ToArray();
                object[] args = { character, terms, criteria, ResultLimit, 0 };
                var rows = lookup.Invoke(database, args) as IEnumerable;
                if (rows == null)
                    throw new NotSupportedException("ItemFinder returned no result collection.");

                var result = new IndexLookupResult { Status = IndexLookupStatus.Found };
                foreach (object row in rows.Cast<object>().Take(ResultLimit))
                {
                    if (row == null) throw new NotSupportedException("ItemFinder returned an empty row.");
                    object item = itemProperty.GetValue(row);
                    object location = containerProperty.GetValue(row);
                    if (item == null || location == null)
                        throw new NotSupportedException("ItemFinder returned incomplete item metadata.");
                    string name = nameProperty.GetValue(item) as string;
                    if (string.IsNullOrWhiteSpace(name))
                        throw new NotSupportedException("ItemFinder returned an unnamed item.");
                    object instance = instanceProperty.GetValue(location);
                    object root = rootProperty.GetValue(location);
                    string containerName = Enum.GetName(containerId, instance);
                    string rootName = Enum.GetName(containerId, root);
                    int rawInstance = Convert.ToInt32(instance, CultureInfo.InvariantCulture);
                    var match = new IndexedLocation
                    {
                        Name = name,
                        Container = containerName ?? rawInstance.ToString(CultureInfo.InvariantCulture),
                        Root = rootName ?? Convert.ToString(root, CultureInfo.InvariantCulture)
                    };
                    // In the inspected ItemFinder build, AddContainer stores the bag's
                    // UniqueIdentity.Instance in ContainerInstance. Keep this advisory:
                    // do not interpret database row IDs or SlotInstance as game identities.
                    if (rootName == "Bank" && containerName == null && rawInstance > 0)
                        match.BagInstanceHint = rawInstance;
                    if (qlProperty != null && qlProperty.PropertyType == typeof(int) &&
                        qlProperty.GetIndexParameters().Length == 0)
                        match.QualityLevel = (int)qlProperty.GetValue(item);
                    result.Matches.Add(match);
                }
                result.TotalMatches = Math.Max(result.Matches.Count, (int)args[4]);
                if (result.Matches.Count == 0)
                    return Result(IndexLookupStatus.NoMatch,
                        $"No ItemFinder index match for '{query}'. Unopened bags have not been ruled out; refresh the index in ItemFinder if needed.");
                // Exact names come first within the returned page; no match is auto-selected.
                result.Matches = result.Matches.OrderByDescending(match =>
                    string.Equals(match.Name, query, StringComparison.OrdinalIgnoreCase)).ToList();
                return result;
            }
            catch (NotSupportedException)
            {
                return Result(IndexLookupStatus.Incompatible,
                    "This ItemFinder interface is not compatible with EquipManager's lookup. Its index was not reliably checked; no indexed item was moved.");
            }
            catch (Exception ex)
            {
                Exception actual = ex is TargetInvocationException invocation && invocation.InnerException != null
                    ? invocation.InnerException : ex;
                return Result(IndexLookupStatus.Failed,
                    $"ItemFinder lookup failed ({actual.GetType().Name}). Its index was not reliably checked; no indexed item was moved.");
            }
        }

        internal static IEnumerable<string> Describe(IndexLookupResult result, string query, IEnumerable<BankBagHint> visibleBags)
        {
            if (result.Status != IndexLookupStatus.Found)
            {
                yield return result.Message;
                yield break;
            }

            List<BankBagHint> bags = (visibleBags ?? Enumerable.Empty<BankBagHint>()).Where(bag => bag != null).ToList();
            yield return $"ItemFinder returned {result.Matches.Count} of {result.TotalMatches} indexed match(es). These are last-recorded locations; no item was moved.";
            foreach (IndexedLocation match in result.Matches)
            {
                string label = "'" + match.Name + "'" +
                    (match.QualityLevel.HasValue ? " (QL " + match.QualityLevel.Value + ")" : string.Empty);
                if (!match.IsBank)
                {
                    yield return $"{label}: indexed container {match.Container}, root {match.Root}.";
                    continue;
                }

                if (match.Container == "Bank")
                    yield return $"{label}: last indexed directly in the bank, outside a bag. Open your bank and check its main slots.";
                else
                {
                    List<BankBagHint> candidates = match.BagInstanceHint.HasValue
                        ? bags.Where(bag => bag.Instance == match.BagInstanceHint.Value).ToList()
                        : new List<BankBagHint>();
                    if (candidates.Count == 1 && !string.IsNullOrWhiteSpace(candidates[0].Name))
                        yield return $"{label}: indexed in the bank. Bag identity hint matches visible bag '{candidates[0].Name}' (ID {match.BagInstanceHint}); open it and confirm the item is there.";
                    else
                        yield return $"{label}: indexed in the bank, but EquipManager cannot identify its exact bag from the currently available data.";
                }
            }

            if (result.Matches.Any(match => match.IsBank))
            {
                yield return "Open your bank, use ItemFinder's own results to locate the item if possible, then open that bag. If no bag is identified, open likely bank bags and retry.";
                yield return $"After the contents load, rerun /equip find {query}. A single visible bank match can then be requested into inventory.";
                yield return "If it is still missing, refresh the index using ItemFinder's normal scan controls and search again; the saved location may be outdated.";
            }
            if (result.TotalMatches > 1)
                yield return "Use the full item name to narrow the search. For identical names or different QLs, select the desired copy manually.";
        }

        private static Type RequiredType(Assembly assembly, string name) =>
            assembly.GetType(name) ?? throw new NotSupportedException(name);

        private static PropertyInfo RequiredProperty(Type type, string name, Type expected)
        {
            PropertyInfo property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (property == null || !property.CanRead || property.PropertyType != expected ||
                property.GetIndexParameters().Length != 0)
                throw new NotSupportedException(name);
            return property;
        }

        private static IndexLookupResult Result(IndexLookupStatus status, string message) =>
            new IndexLookupResult { Status = status, Message = message };
    }

    internal static class BankLookupSafety
    {
        // Apply across bank-root items and open bags together, so bag ordering cannot
        // choose a partial match ahead of an exact name or hide a second matching copy.
        internal static List<T> SelectMatches<T>(IEnumerable<T> items, Func<T, string> name, string query)
        {
            var matches = items.Where(item => !string.IsNullOrWhiteSpace(name(item)) &&
                name(item).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            var exact = matches.Where(item => string.Equals(name(item), query, StringComparison.OrdinalIgnoreCase)).ToList();
            return exact.Count > 0 ? exact : matches;
        }

        internal static string RequestMove(bool sourceVisible, int freeSlots, Action move)
        {
            if (!sourceVisible) return "The bank or source bag is no longer open; no transfer was sent.";
            if (freeSlots <= 0) return "Inventory is full; no transfer was sent.";
            try
            {
                move();
                return "One transfer request was sent. Check inventory to confirm it arrived.";
            }
            catch (Exception ex)
            {
                return $"Transfer request failed ({ex.GetType().Name}); no retry was sent. Check the source and inventory before retrying.";
            }
        }
    }
}
