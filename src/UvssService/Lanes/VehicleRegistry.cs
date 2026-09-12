using System.Text.Json;

namespace UvssService.Lanes;

public enum VehicleClassification { Normal, Whitelist, Blacklist }

public record VehicleRegistryEntry(
    string LicensePlate,
    string OwnerName,
    VehicleClassification Classification,
    string Notes)
{
    public long Id { get; init; }
}

/// <summary>Master list of known vehicles (whitelist/blacklist/normal), used
/// by LaneWorkerHostedService to decide each pass's admission status and by
/// the Vehicle Registry page to manage it. Persisted to its own small JSON
/// file (separate from ScanEvent, which now lives in MySQL) since this is
/// reference data an operator builds up over time, not a per-pass scan
/// record.</summary>
public class VehicleRegistryStore
{
    private readonly object _lock = new();
    private readonly List<VehicleRegistryEntry> _entries = new();
    private readonly string _filePath;
    private long _nextId = 1;

    public event Action? Changed;

    public VehicleRegistryStore(string filePath)
    {
        _filePath = filePath;
        Load();
    }

    public IReadOnlyList<VehicleRegistryEntry> All
    {
        get { lock (_lock) { return _entries.OrderByDescending(e => e.Id).ToList(); } }
    }

    public VehicleRegistryEntry? FindByPlate(string plate)
    {
        if (string.IsNullOrWhiteSpace(plate))
        {
            return null;
        }
        var normalized = Normalize(plate);
        lock (_lock)
        {
            return _entries.FirstOrDefault(e => Normalize(e.LicensePlate) == normalized);
        }
    }

    public void Add(string licensePlate, string ownerName, VehicleClassification classification, string notes)
    {
        lock (_lock)
        {
            _entries.Add(new VehicleRegistryEntry(licensePlate.Trim(), ownerName.Trim(), classification, notes.Trim())
            {
                Id = _nextId++,
            });
            Save();
        }
        Changed?.Invoke();
    }

    public void Remove(long id)
    {
        lock (_lock)
        {
            _entries.RemoveAll(e => e.Id == id);
            Save();
        }
        Changed?.Invoke();
    }

    // Plate comparison ignores spaces/dashes/case -- "KA 01 AB 1234",
    // "ka-01-ab-1234" and "KA01AB1234" are all the same plate to a registry
    // lookup, same as how ANPR itself normalizes plate text.
    private static string Normalize(string plate) =>
        new(plate.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private void Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return;
            }
            var json = File.ReadAllText(_filePath);
            var loaded = JsonSerializer.Deserialize<List<VehicleRegistryEntry>>(json);
            if (loaded == null)
            {
                return;
            }
            _entries.AddRange(loaded);
            _nextId = _entries.Count > 0 ? _entries.Max(e => e.Id) + 1 : 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Vehicle registry: failed to load '{_filePath}': {ex.Message}");
        }
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            var json = JsonSerializer.Serialize(_entries, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_filePath, json);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Vehicle registry: failed to save '{_filePath}': {ex.Message}");
        }
    }
}
