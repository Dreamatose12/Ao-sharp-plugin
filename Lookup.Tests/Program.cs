using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using EquipManager;

// Synthetic ItemFinder contract objects: no external DLL is loaded or executed.
public enum ContainerId { None = 0, Inventory = 1, Bank = 2, Backpack = 3 }
public enum FilterCriteria { Name, Ql, Id, Location }
public class SearchCriteria { }
public static class DatabaseProcessor
{
    public static bool Busy;
    public static bool IsOccupied() => Busy;
}
namespace EFDataAccessLibrary.Models
{
    public class ItemInfo { public string Name { get; set; } public int Ql { get; set; } }
    public class ItemContainer { public ContainerId ContainerInstance { get; set; } public ContainerId Root { get; set; } }
    public class Slot { public ItemInfo ItemInfo { get; set; } public ItemContainer ItemContainer { get; set; } }
}
namespace MalisItemFinder
{
    using EFDataAccessLibrary.Models;
    public static class Main
    {
        public static Database Database;
        public static object MainWindow;
    }
    public class Database
    {
        public static int ConstructorCalls;
        public Database() { ConstructorCalls++; }
        public List<Slot> Rows = new List<Slot>();
        public int Total;
        public int Calls;
        public bool Throw;
        public string Character;
        public string[] Terms;
        public Dictionary<FilterCriteria, List<SearchCriteria>> Criteria;
        public int Limit;
        public List<Slot> ItemLookup(string character, IEnumerable<string> terms,
            Dictionary<FilterCriteria, List<SearchCriteria>> criteria, int limit, ref int count)
        {
            Calls++;
            Character = character;
            Terms = terms.ToArray();
            Criteria = criteria;
            Limit = limit;
            if (Throw) throw new InvalidOperationException("synthetic failure");
            count = Total;
            return Rows;
        }
    }
}
internal static class Program
{
    private static int passed;
    private static int failed;
    private static Assembly FixtureAssembly => typeof(Program).Assembly;

    private static void Main()
    {
        Run("missing ItemFinder is unavailable, not no-match", () =>
            Equal(IndexLookupStatus.Unavailable, ItemFinderLookup.Read(null, "Tester", "Thing").Status));
        Run("unknown interface is incompatible", () =>
            Equal(IndexLookupStatus.Incompatible, ItemFinderLookup.Read(typeof(string).Assembly, "Tester", "Thing").Status));
        Run("missing database does not construct one", () => {
            MalisItemFinder.Main.Database = null;
            int before = MalisItemFinder.Database.ConstructorCalls;
            Equal(IndexLookupStatus.Unavailable, Lookup().Status);
            Equal(before, MalisItemFinder.Database.ConstructorCalls);
        });
        Run("uninitialized window does not query", () => {
            MalisItemFinder.Main.MainWindow = null;
            Equal(IndexLookupStatus.Unavailable, Lookup().Status);
            Equal(0, Db.Calls);
        });
        Run("busy index does not query", () => {
            DatabaseProcessor.Busy = true;
            Equal(IndexLookupStatus.Unavailable, Lookup().Status);
            Equal(0, Db.Calls);
        });
        Run("missing character does not query", () => {
            Equal(IndexLookupStatus.Unavailable, ItemFinderLookup.Read(FixtureAssembly, null, "Thing").Status);
            Equal(0, Db.Calls);
        });
        Run("All character selector is rejected", () => {
            Equal(IndexLookupStatus.Unavailable, ItemFinderLookup.Read(FixtureAssembly, "All", "Thing").Status);
            Equal(0, Db.Calls);
        });
        Run("blank query does not query", () => {
            Equal(IndexLookupStatus.NoMatch, ItemFinderLookup.Read(FixtureAssembly, "Tester", "  ").Status);
            Equal(0, Db.Calls);
        });
        Run("empty index is a no-match result", () => Equal(IndexLookupStatus.NoMatch, Lookup().Status));
        Run("lookup exceptions remain failures", () => {
            Db.Throw = true;
            var result = Lookup();
            Equal(IndexLookupStatus.Failed, result.Status);
            Contains(result.Message, "InvalidOperationException");
        });
        Run("null collection is incompatible", () => {
            Db.Rows = null;
            Equal(IndexLookupStatus.Incompatible, Lookup().Status);
        });
        Run("incomplete metadata is incompatible", () => {
            Db.Rows.Add(new EFDataAccessLibrary.Models.Slot());
            Equal(IndexLookupStatus.Incompatible, Lookup().Status);
        });
        Run("query uses current character, normalized terms and all criteria keys", () => {
            Lookup("  Ancient  Relay ");
            Equal("Tester", Db.Character);
            Equal("ancient|relay", string.Join("|", Db.Terms));
            Equal(4, Db.Criteria.Count);
            True(Db.Criteria.Values.All(list => list.Count == 0));
            Equal(10, Db.Limit);
            Equal(1, Db.Calls);
        });
        Run("loaded database is reused without construction", () => {
            int before = MalisItemFinder.Database.ConstructorCalls;
            Lookup();
            Equal(before, MalisItemFinder.Database.ConstructorCalls);
        });
        Run("bank root is distinguished from a bag", () => {
            Db.Rows.Add(Row("Thing", ContainerId.Bank, ContainerId.Bank));
            var result = Lookup();
            True(result.Matches[0].IsBank);
            Equal<int?>(null, result.Matches[0].BagInstanceHint);
            Contains(Messages(result), "outside a bag");
        });
        Run("bank bag ID is advisory and yields practical fallback", () => {
            Db.Rows.Add(Row("Thing", (ContainerId)12345, ContainerId.Bank));
            var result = Lookup();
            Equal<int?>(12345, result.Matches[0].BagInstanceHint);
            string text = Messages(result);
            Contains(text, "cannot identify its exact bag");
            Contains(text, "/equip find Thing");
            Contains(text, "normal scan controls");
            Contains(text, "no item was moved");
        });
        Run("single visible bag identity gives a name hint", () => {
            Db.Rows.Add(Row("Thing", (ContainerId)12345, ContainerId.Bank));
            string text = Messages(Lookup(), new BankBagHint { Instance = 12345, Name = "Twink gear" });
            Contains(text, "hint matches visible bag 'Twink gear'");
            Contains(text, "confirm the item");
        });
        Run("duplicate bag identity is never chosen", () => {
            Db.Rows.Add(Row("Thing", (ContainerId)12345, ContainerId.Bank));
            string text = Messages(Lookup(), new BankBagHint { Instance = 12345, Name = "A" },
                new BankBagHint { Instance = 12345, Name = "B" });
            Contains(text, "cannot identify its exact bag");
            True(!text.Contains("hint matches"));
        });
        Run("mismatched bag identity gives no name hint", () => {
            Db.Rows.Add(Row("Thing", (ContainerId)12345, ContainerId.Bank));
            Contains(Messages(Lookup(), new BankBagHint { Instance = 98765, Name = "Wrong bag" }), "cannot identify its exact bag");
        });
        Run("blank bag name gives fallback", () => {
            Db.Rows.Add(Row("Thing", (ContainerId)12345, ContainerId.Bank));
            Contains(Messages(Lookup(), new BankBagHint { Instance = 12345, Name = " " }), "cannot identify its exact bag");
        });
        Run("named Backpack category is not interpreted as an identity", () => {
            Db.Rows.Add(Row("Thing", ContainerId.Backpack, ContainerId.Bank));
            Equal<int?>(null, Lookup().Matches[0].BagInstanceHint);
        });
        Run("non-bank location is retained without bank instructions", () => {
            Db.Rows.Add(Row("Thing", (ContainerId)12345, ContainerId.Inventory));
            var result = Lookup();
            True(!result.Matches[0].IsBank);
            Equal<int?>(null, result.Matches[0].BagInstanceHint);
            True(!Messages(result).Contains("Open your bank"));
        });
        Run("multiple indexed results retain QL and prefer exact names within page", () => {
            Db.Rows.Add(Row("Thing extra", ContainerId.Bank, ContainerId.Bank));
            Db.Rows.Add(Row("Thing", ContainerId.Bank, ContainerId.Bank));
            Db.Rows[1].ItemInfo.Ql = 200;
            Db.Total = 12;
            var result = Lookup();
            Equal("Thing", result.Matches[0].Name);
            Contains(Messages(result), "2 of 12");
            Contains(Messages(result), "QL 200");
            Contains(Messages(result), "select the desired copy manually");
        });
        Run("result output is bounded", () => {
            for (int i = 0; i < 30; i++) Db.Rows.Add(Row("Thing " + i, ContainerId.Bank, ContainerId.Bank));
            Db.Total = 30;
            Equal(10, Lookup().Matches.Count);
        });
        Run("visible bank exact match wins over partial across all bags", () => {
            var result = BankLookupSafety.SelectMatches(new[] { "Thing extra", "Thing" }, s => s, "Thing");
            Equal(1, result.Count);
            Equal("Thing", result[0]);
        });
        Run("duplicate exact bank matches remain ambiguous", () =>
            Equal(2, BankLookupSafety.SelectMatches(new[] { "Thing", "Thing" }, s => s, "Thing").Count));
        Run("multiple partial bank matches remain ambiguous", () =>
            Equal(2, BankLookupSafety.SelectMatches(new[] { "Thing A", "Thing B" }, s => s, "Thing").Count));
        Run("case-insensitive bank selection works", () =>
            Equal(1, BankLookupSafety.SelectMatches(new[] { "THING" }, s => s, "thing").Count));
        Run("full inventory sends no move", () => {
            int calls = 0;
            Contains(BankLookupSafety.RequestMove(true, 0, () => calls++), "Inventory is full");
            Equal(0, calls);
        });
        Run("closed source sends no move", () => {
            int calls = 0;
            Contains(BankLookupSafety.RequestMove(false, 5, () => calls++), "no transfer was sent");
            Equal(0, calls);
        });
        Run("visible source sends exactly one request without claiming arrival", () => {
            int calls = 0;
            Contains(BankLookupSafety.RequestMove(true, 5, () => calls++), "Check inventory to confirm");
            Equal(1, calls);
        });
        Run("failed move is not retried", () => {
            int calls = 0;
            string message = BankLookupSafety.RequestMove(true, 5, () => { calls++; throw new InvalidOperationException(); });
            Equal(1, calls);
            Contains(message, "no retry was sent");
        });
        Console.WriteLine($"{passed} passed; {failed} failed. Synthetic offline tests only; no game or real ItemFinder query was run.");
        Environment.ExitCode = failed == 0 ? 0 : 1;
    }

    private static MalisItemFinder.Database Db => MalisItemFinder.Main.Database;
    private static IndexLookupResult Lookup(string query = "Thing") => ItemFinderLookup.Read(FixtureAssembly, "Tester", query);
    private static EFDataAccessLibrary.Models.Slot Row(string name, ContainerId location, ContainerId root) =>
        new EFDataAccessLibrary.Models.Slot {
            ItemInfo = new EFDataAccessLibrary.Models.ItemInfo { Name = name, Ql = 100 },
            ItemContainer = new EFDataAccessLibrary.Models.ItemContainer { ContainerInstance = location, Root = root }
        };
    private static string Messages(IndexLookupResult result, params BankBagHint[] bags) =>
        string.Join("\n", ItemFinderLookup.Describe(result, "Thing", bags));
    private static void Run(string name, Action test)
    {
        MalisItemFinder.Main.Database = new MalisItemFinder.Database();
        MalisItemFinder.Main.MainWindow = new object();
        DatabaseProcessor.Busy = false;
        try { test(); passed++; Console.WriteLine("PASS " + name); }
        catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex.Message); }
    }
    private static void True(bool value) { if (!value) throw new Exception("Expected true."); }
    private static void Equal<T>(T expected, T actual) {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}; actual {actual}.");
    }
    private static void Contains(string value, string expected) { True(value != null && value.Contains(expected)); }
}
