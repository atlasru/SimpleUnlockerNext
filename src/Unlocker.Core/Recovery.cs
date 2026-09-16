namespace Unlocker.Core;

public enum RecoveryStatus
{
    Prepared,
    Applied,
    RolledBack,
    FailedRestored,
    RecoveryRequired
}

/// <summary>
/// Backup stores ONLY a trusted catalog ID, original DWORD and operation state.
/// Never use paths or registry keys supplied by a backup file to choose a target.
/// </summary>
public sealed record RecoveryRecord(
    Guid Id,
    string RuleId,
    bool OriginalExists,
    int? OriginalDword,
    DateTimeOffset CreatedUtc,
    RecoveryStatus Status,
    string? Detail = null);

public interface IRecoveryJournal
{
    void Save(RecoveryRecord record);
    RecoveryRecord Load(Guid id);
    IReadOnlyList<RecoveryRecord> List();
}

public sealed class RecoveryEngine(IPolicyRegistry registry, IRecoveryJournal journal)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Removes an explicitly selected HKCU DWORD restriction. No bulk repair.</summary>
    public async Task<RecoveryRecord> RepairAsync(string ruleId, CancellationToken cancellationToken = default)
    {
        var rule = RestrictionCatalog.Find(ruleId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var before = registry.Read(rule);
            if (!before.Exists || before.Dword != 1)
                throw new InvalidOperationException("The selected policy is not in the restricted state. Scan again.");

            var record = new RecoveryRecord(Guid.NewGuid(), rule.Id, true, before.Dword,
                DateTimeOffset.UtcNow, RecoveryStatus.Prepared);

            // A durable backup MUST exist before any system mutation.
            journal.Save(record);
            try
            {
                registry.Delete(rule);
                if (registry.Read(rule).Exists)
                    throw new InvalidOperationException("Verification failed: the registry value still exists.");

                var applied = record with { Status = RecoveryStatus.Applied };
                journal.Save(applied);
                return applied;
            }
            catch (Exception operationError)
            {
                try
                {
                    RestoreOriginal(rule, record);
                    journal.Save(record with { Status = RecoveryStatus.FailedRestored,
                        Detail = operationError.Message });
                }
                catch (Exception restoreError)
                {
                    // The original Prepared journal still exists even if this write fails.
                    try
                    {
                        journal.Save(record with { Status = RecoveryStatus.RecoveryRequired,
                            Detail = operationError.Message + " | Restore: " + restoreError.Message });
                    }
                    catch (Exception) { /* Keep original durable Prepared backup. */ }
                    throw new AggregateException("Repair failed, and automatic restoration failed. Manual recovery is required.",
                        operationError, restoreError);
                }

                throw new InvalidOperationException("Repair failed. Original value restored.", operationError);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<RecoveryRecord> RollbackAsync(Guid recordId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var record = journal.Load(recordId);
            if (record.Id != recordId || record.Id == Guid.Empty)
                throw new InvalidDataException("Invalid recovery record identifier.");
            var rule = RestrictionCatalog.Find(record.RuleId);
            if (record.Status is not (RecoveryStatus.Applied or RecoveryStatus.Prepared or RecoveryStatus.RecoveryRequired))
                throw new InvalidOperationException("This operation cannot be rolled back again.");
            if (!record.OriginalExists || record.OriginalDword != 1)
                throw new InvalidDataException("Unexpected backup data. Refusing registry modification.");

            // An unrelated actor may have changed the value. Never overwrite that change.
            var current = registry.Read(rule);
            if (current.Exists && current.Dword != record.OriginalDword)
                throw new InvalidOperationException("The policy changed after repair. Refusing to overwrite a newer value.");

            RestoreOriginal(rule, record);
            var restored = record with { Status = RecoveryStatus.RolledBack };
            journal.Save(restored);
            return restored;
        }
        finally
        {
            _gate.Release();
        }
    }

    public IReadOnlyList<RecoveryRecord> History() => journal.List();

    private void RestoreOriginal(RestrictionRule rule, RecoveryRecord record)
    {
        if (!record.OriginalExists || record.OriginalDword is not int value)
            throw new InvalidDataException("Original registry state is missing.");
        var current = registry.Read(rule);
        if (current.Exists && current.Dword != value)
            throw new InvalidOperationException("Policy changed concurrently; refusing to overwrite it.");
        if (!current.Exists)
            registry.WriteDword(rule, value);
        if (registry.Read(rule) != PolicyValue.FromDword(value))
            throw new InvalidOperationException("Original registry value was not restored.");
    }
}
