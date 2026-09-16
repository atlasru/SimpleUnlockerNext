using System.Text.Json;
using System.Text.Json.Serialization;
using Unlocker.Core;

namespace Unlocker.Infrastructure;

/// <summary>Durable per-user journal. The path is never constructed from untrusted JSON.</summary>
public sealed class FileRecoveryJournal : IRecoveryJournal
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _directory;

    public FileRecoveryJournal(string? directory = null)
    {
        _directory = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SimpleUnlockerNext", "Recovery");
        Directory.CreateDirectory(_directory);
    }

    public void Save(RecoveryRecord record)
    {
        Validate(record);
        string target = FilePath(record.Id);
        string temporary = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, record, JsonOptions);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, target, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public RecoveryRecord Load(Guid id)
    {
        if (id == Guid.Empty) throw new ArgumentException("Empty backup identifier.", nameof(id));
        string path = FilePath(id);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var record = JsonSerializer.Deserialize<RecoveryRecord>(stream, JsonOptions)
            ?? throw new InvalidDataException("Empty recovery record.");
        Validate(record);
        if (record.Id != id)
            throw new InvalidDataException("Recovery record ID does not match its filename.");
        return record;
    }

    public IReadOnlyList<RecoveryRecord> List()
    {
        var results = new List<RecoveryRecord>();
        foreach (string filename in Directory.EnumerateFiles(_directory, "*.json"))
        {
            if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(filename), "N", out Guid id))
                throw new InvalidDataException("Unexpected file in recovery journal: " + Path.GetFileName(filename));
            results.Add(Load(id));
        }
        return results.OrderByDescending(record => record.CreatedUtc).ToArray();
    }

    private string FilePath(Guid id) => Path.Combine(_directory, id.ToString("N") + ".json");

    private static void Validate(RecoveryRecord record)
    {
        if (record.Id == Guid.Empty || !record.OriginalExists || record.OriginalDword != 1 ||
            record.CreatedUtc == default || !Enum.IsDefined(record.Status))
            throw new InvalidDataException("Recovery record failed validation.");
        RestrictionCatalog.Find(record.RuleId);
    }
}
