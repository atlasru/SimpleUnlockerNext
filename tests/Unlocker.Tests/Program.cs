using Unlocker.Core;
using Unlocker.Infrastructure;

namespace Unlocker.Tests;

internal static class Program
{
    private static int _passed;

    private static async Task<int> Main()
    {
        var tests = new (string Name, Func<Task> Test)[]
        {
            ("scan: clean registry", ScanClean),
            ("scan: restricted policy", ScanRestricted),
            ("scan: unreadable rule does not break other rules", ScanUnreadable),
            ("repair: journal precedes mutation", BackupBeforeMutation),
            ("repair: restores missing state", RepairAndRollback),
            ("repair: rejects non-restricted policy", RejectNonRestricted),
            ("repair: refuses unlisted policy", RejectUnknown),
            ("repair: backup failure prevents mutation", BackupFailurePreventsMutation),
            ("repair: operation failure restores original", FailureRestoresOriginal),
            ("repair: failed restoration preserves backup", FailedRestorationKeepsBackup),
            ("rollback: refuses changed policy", RefuseConcurrentChange),
            ("rollback: no double rollback", RejectDoubleRollback),
            ("journal: persists and validates records", JournalRoundTrip),
            ("journal: rejects inconsistent file identifier", JournalRejectsMismatchedId),
            ("journal: corrupted data is not silently ignored", JournalRejectsCorruption),
            ("repair: sequential concurrent requests", ConcurrentRepair)
        };

        foreach (var (name, test) in tests)
        {
            try
            {
                await test();
                Console.WriteLine("PASS " + name);
                _passed++;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("FAIL " + name + ": " + ex);
            }
        }

        Console.WriteLine($"RESULT {_passed}/{tests.Length} passed");
        return _passed == tests.Length ? 0 : 1;
    }

    private static Task ScanClean()
    {
        var results = RestrictionScanner.Scan(new FakeRegistry());
        Check(results.Count == 4 && results.All(x => x.State == RestrictionState.NotConfigured));
        return Task.CompletedTask;
    }

    private static Task ScanRestricted()
    {
        var registry = Restricted();
        var results = RestrictionScanner.Scan(registry);
        Check(results.Single(r => r.Rule.Id == "task-manager").State == RestrictionState.Restricted);
        return Task.CompletedTask;
    }

    private static Task ScanUnreadable()
    {
        var registry = Restricted();
        registry.FailReadRule = "registry-editor";
        var results = RestrictionScanner.Scan(registry);
        Check(results.Count == 4 && results.Single(r => r.Rule.Id == "registry-editor").State == RestrictionState.Unknown);
        Check(results.Single(r => r.Rule.Id == "registry-editor").Diagnostic is not null);
        Check(results.Single(r => r.Rule.Id == "task-manager").State == RestrictionState.Restricted);
        return Task.CompletedTask;
    }

    private static async Task BackupBeforeMutation()
    {
        var registry = Restricted();
        var journal = new FakeJournal();
        registry.BeforeDelete = () => Check(journal.Records.Values.Single().Status == RecoveryStatus.Prepared);
        await new RecoveryEngine(registry, journal).RepairAsync("task-manager");
        Check(registry.DeleteCount == 1);
    }

    private static async Task RepairAndRollback()
    {
        var registry = Restricted();
        var journal = new FakeJournal();
        var engine = new RecoveryEngine(registry, journal);
        var result = await engine.RepairAsync("task-manager");
        Check(result.Status == RecoveryStatus.Applied && !registry.Read(Rule).Exists);
        var rollback = await engine.RollbackAsync(result.Id);
        Check(rollback.Status == RecoveryStatus.RolledBack);
        Check(registry.Read(Rule) == PolicyValue.FromDword(1));
    }

    private static async Task RejectNonRestricted()
    {
        var registry = new FakeRegistry();
        var journal = new FakeJournal();
        await Throws<InvalidOperationException>(() => new RecoveryEngine(registry, journal).RepairAsync("task-manager"));
        Check(journal.Records.Count == 0);
    }

    private static async Task RejectUnknown()
    {
        var registry = Restricted();
        await Throws<ArgumentException>(() => new RecoveryEngine(registry, new FakeJournal()).RepairAsync("other-key"));
        Check(registry.DeleteCount == 0);
    }

    private static async Task BackupFailurePreventsMutation()
    {
        var registry = Restricted();
        var journal = new FakeJournal { FailNextSave = true };
        await Throws<IOException>(() => new RecoveryEngine(registry, journal).RepairAsync("task-manager"));
        Check(registry.Read(Rule) == PolicyValue.FromDword(1) && registry.DeleteCount == 0);
    }

    private static async Task FailureRestoresOriginal()
    {
        var registry = Restricted();
        registry.FailAfterDelete = true;
        var journal = new FakeJournal();
        await Throws<InvalidOperationException>(() => new RecoveryEngine(registry, journal).RepairAsync("task-manager"));
        Check(registry.Read(Rule) == PolicyValue.FromDword(1));
        Check(journal.Records.Values.Single().Status == RecoveryStatus.FailedRestored);
    }

    private static async Task FailedRestorationKeepsBackup()
    {
        var registry = Restricted();
        registry.FailAfterDelete = true;
        registry.FailWrite = true;
        var journal = new FakeJournal();
        await Throws<AggregateException>(() => new RecoveryEngine(registry, journal).RepairAsync("task-manager"));
        Check(journal.Records.Values.Single().Status == RecoveryStatus.RecoveryRequired);
        Check(!registry.Read(Rule).Exists);
    }

    private static async Task RefuseConcurrentChange()
    {
        var registry = Restricted();
        var engine = new RecoveryEngine(registry, new FakeJournal());
        var applied = await engine.RepairAsync("task-manager");
        registry.Values[Rule.Id] = PolicyValue.FromDword(0);
        await Throws<InvalidOperationException>(() => engine.RollbackAsync(applied.Id));
        Check(registry.Read(Rule) == PolicyValue.FromDword(0));
    }

    private static async Task RejectDoubleRollback()
    {
        var registry = Restricted();
        var engine = new RecoveryEngine(registry, new FakeJournal());
        var applied = await engine.RepairAsync("task-manager");
        await engine.RollbackAsync(applied.Id);
        await Throws<InvalidOperationException>(() => engine.RollbackAsync(applied.Id));
    }

    private static Task JournalRoundTrip()
    {
        var dir = Path.Combine(Path.GetTempPath(), "unlocker-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var journal = new FileRecoveryJournal(dir);
            var entry = new RecoveryRecord(Guid.NewGuid(), Rule.Id, true, 1,
                DateTimeOffset.UtcNow, RecoveryStatus.Prepared);
            journal.Save(entry);
            Check(journal.Load(entry.Id) == entry);
            Check(new FileRecoveryJournal(dir).List().Single() == entry);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        return Task.CompletedTask;
    }

    private static Task JournalRejectsMismatchedId()
    {
        var dir = Path.Combine(Path.GetTempPath(), "unlocker-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var journal = new FileRecoveryJournal(dir);
            var entry = new RecoveryRecord(Guid.NewGuid(), Rule.Id, true, 1,
                DateTimeOffset.UtcNow, RecoveryStatus.Prepared);
            journal.Save(entry);
            var original = Path.Combine(dir, entry.Id.ToString("N") + ".json");
            File.Move(original, Path.Combine(dir, Guid.NewGuid().ToString("N") + ".json"));
            try { journal.List(); throw new Exception("Expected validation failure."); }
            catch (InvalidDataException) { /* success */ }
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        return Task.CompletedTask;
    }

    private static Task JournalRejectsCorruption()
    {
        var dir = Path.Combine(Path.GetTempPath(), "unlocker-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var journal = new FileRecoveryJournal(dir);
            File.WriteAllText(Path.Combine(dir, Guid.NewGuid().ToString("N") + ".json"), "not-json");
            try { journal.List(); throw new Exception("Expected parse failure."); }
            catch (System.Text.Json.JsonException) { /* success */ }
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        return Task.CompletedTask;
    }

    private static async Task ConcurrentRepair()
    {
        var registry = Restricted();
        var journal = new FakeJournal();
        var engine = new RecoveryEngine(registry, journal);
        var operations = await Task.WhenAll(Task.Run(async () => await TryRepair(engine)),
            Task.Run(async () => await TryRepair(engine)));
        Check(operations.Count(success => success) == 1);
        Check(registry.DeleteCount == 1 && journal.Records.Count == 1);
    }

    private static async Task<bool> TryRepair(RecoveryEngine engine)
    {
        try { await engine.RepairAsync("task-manager"); return true; }
        catch (InvalidOperationException) { return false; }
    }

    private static RestrictionRule Rule => RestrictionCatalog.Find("task-manager");
    private static FakeRegistry Restricted()
    {
        var fake = new FakeRegistry();
        fake.Values[Rule.Id] = PolicyValue.FromDword(1);
        return fake;
    }

    private static void Check(bool condition)
    {
        if (!condition) throw new Exception("Assertion failed.");
    }

    private static async Task Throws<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new Exception("Expected exception: " + typeof(T).Name);
    }

    private sealed class FakeRegistry : IPolicyRegistry
    {
        public Dictionary<string, PolicyValue> Values { get; } = new();
        public Action? BeforeDelete { get; set; }
        public bool FailAfterDelete { get; set; }
        public bool FailWrite { get; set; }
        public string? FailReadRule { get; set; }
        public int DeleteCount { get; private set; }

        public PolicyValue Read(RestrictionRule rule)
        {
            if (rule.Id == FailReadRule) throw new UnauthorizedAccessException("Access denied (test).");
            return Values.GetValueOrDefault(rule.Id, PolicyValue.Missing);
        }

        public void Delete(RestrictionRule rule)
        {
            BeforeDelete?.Invoke();
            DeleteCount++;
            Values.Remove(rule.Id);
            if (FailAfterDelete) throw new IOException("Simulated post-write failure.");
        }

        public void WriteDword(RestrictionRule rule, int value)
        {
            if (FailWrite) throw new IOException("Simulated restore failure.");
            Values[rule.Id] = PolicyValue.FromDword(value);
        }
    }

    private sealed class FakeJournal : IRecoveryJournal
    {
        public Dictionary<Guid, RecoveryRecord> Records { get; } = new();
        public bool FailNextSave { get; set; }

        public void Save(RecoveryRecord record)
        {
            if (FailNextSave) { FailNextSave = false; throw new IOException("Simulated disk failure."); }
            Records[record.Id] = record;
        }

        public RecoveryRecord Load(Guid id) => Records[id];
        public IReadOnlyList<RecoveryRecord> List() => Records.Values.OrderByDescending(r => r.CreatedUtc).ToArray();
    }
}
