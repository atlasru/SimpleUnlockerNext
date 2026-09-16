using System.Collections.ObjectModel;

namespace Unlocker.Core;

/// <summary>Known per-user policy locations. A restriction is NOT proof of malware.</summary>
public sealed record RestrictionRule(
    string Id,
    string Title,
    string Description,
    string RegistrySubKey,
    string ValueName);

public enum RestrictionState
{
    NotConfigured,
    Allowed,
    Restricted,
    Unknown
}

/// <summary>The first milestone intentionally supports only REG_DWORD policies.</summary>
public readonly record struct PolicyValue(bool Exists, int? Dword)
{
    public static PolicyValue Missing => new(false, null);
    public static PolicyValue FromDword(int value) => new(true, value);
}

public sealed record RestrictionFinding(
    RestrictionRule Rule,
    RestrictionState State,
    PolicyValue CurrentValue,
    string? Diagnostic = null);

public static class RestrictionCatalog
{
    private static readonly IReadOnlyList<RestrictionRule> Rules =
        new ReadOnlyCollection<RestrictionRule>(new List<RestrictionRule>
        {
            new("task-manager", "Диспетчер задач", "Политика запрещает запуск диспетчера задач.",
                @"Software\Microsoft\Windows\CurrentVersion\Policies\System", "DisableTaskMgr"),
            new("registry-editor", "Редактор реестра", "Политика запрещает запуск редактора реестра.",
                @"Software\Microsoft\Windows\CurrentVersion\Policies\System", "DisableRegistryTools"),
            new("command-prompt", "Командная строка", "Политика запрещает запуск командной строки.",
                @"Software\Policies\Microsoft\Windows\System", "DisableCMD"),
            new("control-panel", "Панель управления", "Политика запрещает доступ к панели управления.",
                @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer", "NoControlPanel")
        });

    public static IReadOnlyList<RestrictionRule> All => Rules;

    public static RestrictionRule Find(string id) =>
        Rules.FirstOrDefault(rule => string.Equals(rule.Id, id, StringComparison.Ordinal))
        ?? throw new ArgumentException("Unknown restriction identifier.", nameof(id));
}

public interface IPolicyRegistry
{
    PolicyValue Read(RestrictionRule rule);
    void Delete(RestrictionRule rule);
    void WriteDword(RestrictionRule rule, int value);
}

public static class RestrictionScanner
{
    public static IReadOnlyList<RestrictionFinding> Scan(IPolicyRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return RestrictionCatalog.All.Select(rule =>
        {
            try
            {
                var value = registry.Read(rule);
                var state = !value.Exists ? RestrictionState.NotConfigured
                    : value.Dword == 1 ? RestrictionState.Restricted
                    : value.Dword == 0 ? RestrictionState.Allowed
                    : RestrictionState.Unknown;
                return new RestrictionFinding(rule, state, value);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or
                                       NotSupportedException or System.Security.SecurityException)
            {
                // Report one unreadable policy without losing the rest of the scan.
                return new RestrictionFinding(rule, RestrictionState.Unknown,
                    PolicyValue.Missing, ex.Message);
            }
        }).ToArray();
    }
}
