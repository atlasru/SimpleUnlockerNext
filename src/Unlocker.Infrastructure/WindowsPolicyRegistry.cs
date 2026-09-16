using Microsoft.Win32;
using Unlocker.Core;

namespace Unlocker.Infrastructure;

/// <summary>
/// Intentionally scoped to HKEY_CURRENT_USER, 64-bit view, four allowlisted DWORDs.
/// No generic registry editor, HKLM writes, process manipulation, or elevation.
/// </summary>
public sealed class WindowsPolicyRegistry : IPolicyRegistry
{
    public PolicyValue Read(RestrictionRule rule)
    {
        ValidateRule(rule);
        using var hive = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        using var key = hive.OpenSubKey(rule.RegistrySubKey, writable: false);
        if (key is null || !key.GetValueNames().Contains(rule.ValueName, StringComparer.OrdinalIgnoreCase))
            return PolicyValue.Missing;
        if (key.GetValueKind(rule.ValueName) != RegistryValueKind.DWord)
            throw new NotSupportedException("The registry value is not REG_DWORD. No changes were made.");
        object? value = key.GetValue(rule.ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        return value is int dword
            ? PolicyValue.FromDword(dword)
            : throw new InvalidDataException("Cannot read REG_DWORD policy value.");
    }

    public void Delete(RestrictionRule rule)
    {
        ValidateRule(rule);
        using var hive = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        using var key = hive.OpenSubKey(rule.RegistrySubKey, writable: true)
            ?? throw new InvalidOperationException("The policy registry key disappeared.");
        key.DeleteValue(rule.ValueName, throwOnMissingValue: true);
        key.Flush();
    }

    public void WriteDword(RestrictionRule rule, int value)
    {
        ValidateRule(rule);
        if (value != 1)
            throw new ArgumentOutOfRangeException(nameof(value), "Only restoring a backed-up restricted value is supported.");
        using var hive = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        using var key = hive.CreateSubKey(rule.RegistrySubKey, writable: true)
            ?? throw new UnauthorizedAccessException("Unable to create the original registry key.");
        key.SetValue(rule.ValueName, value, RegistryValueKind.DWord);
        key.Flush();
    }

    private static void ValidateRule(RestrictionRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        if (!RestrictionCatalog.All.Any(known => known == rule))
            throw new ArgumentException("The requested registry path is not allowlisted.", nameof(rule));
    }
}
