using System.Text;
using Source2Surf.Timer.Modules.Zone;
using SteamDatabase.ValvePak;
using ValveKeyValue;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.Serialization.KeyValues;

// Lists the workshop maps that give the Timer no start or end zone of their own: ZoneModule zones a map's
// trigger_multiples by name, through ZoneMatcher. Zones drawn in game live in the database and aren't looked at.
//
//   dotnet run --project tools/MapZoneScanner -- <steamapps/workshop/content/730> [map prefix, default bhop]

if (args.Length is 0 or > 2 || !Directory.Exists(args[0]))
{
    Console.Error.WriteLine("usage: MapZoneScanner <steamapps/workshop/content/730> [map prefix, default bhop]");

    return 1;
}

Console.OutputEncoding = Encoding.UTF8;

var prefix = args.Length > 1 ? args[1] : "bhop";
var dirs   = Directory.GetDirectories(args[0]);
var items  = new ItemScan?[dirs.Length];

Parallel.For(0, dirs.Length, i => items[i] = Scanner.Scan(dirs[i], prefix));

var scanned = items.OfType<ItemScan>().ToList();

var maps = scanned.SelectMany(x => x.Maps)
                  .OrderBy(x => x.Map, StringComparer.OrdinalIgnoreCase)
                  .ThenBy(x => x.WorkshopId, StringComparer.Ordinal)
                  .ToList();

var subMaps = scanned.Sum(x => x.SubMaps);

Console.Write($"{dirs.Length} workshop items, {maps.Count} maps starting with {prefix}");
Console.WriteLine(subMaps > 0 ? $" ({subMaps} more are skyboxes or prefabs another map loads, counted in that map)." : ".");
Console.WriteLine("Only the maps' own zones count: zones drawn in game are in the database.");

Section("No start, no end", maps.Where(m => !m.HasStart && !m.HasEnd));
Section("No start",         maps.Where(m => !m.HasStart && m.HasEnd));
Section("No end",           maps.Where(m => m.HasStart  && !m.HasEnd));

Console.WriteLine($"\nStart and end: {maps.Count(m => m.HasStart && m.HasEnd)} maps.");

var problems = scanned.Where(x => x.Problem is not null).OrderBy(x => x.WorkshopId, StringComparer.Ordinal).ToList();

if (problems.Count > 0)
{
    Console.WriteLine($"\n== Not scanned ({problems.Count}); they may hold {prefix} maps too ==");

    foreach (var item in problems)
    {
        Console.WriteLine($"  {item.WorkshopId,-12}{item.Problem}");
    }
}

return 0;

static void Section(string title, IEnumerable<MapScan> maps)
{
    var list = maps.ToList();

    Console.WriteLine($"\n== {title} ({list.Count}) ==");

    foreach (var map in list)
    {
        var hints = map.Hints.Count switch
        {
            0    => "",
            <= 6 => "zone-like names: " + string.Join(", ", map.Hints),
            _    => "zone-like names: " + string.Join(", ", map.Hints.Take(6)) + $" and {map.Hints.Count - 6} more",
        };

        Console.WriteLine($"  {map.WorkshopId,-12}{map.Map,-36}{hints}".TrimEnd());
    }
}

internal sealed record MapScan(string WorkshopId, string Map, bool HasStart, bool HasEnd, List<string> Hints);

// SubMaps: prefix maps another map of the item loads. Problem: why the item wasn't scanned.
internal sealed record ItemScan(string WorkshopId, List<MapScan> Maps, int SubMaps, string? Problem);

// A map's named triggers and info_targets, and the maps it loads (a skybox_reference's targetmapname, a prefab's).
internal sealed record MapEntities(List<(string Class, string Name)> Named, HashSet<string> Loads);

internal static class Scanner
{
    private static readonly string[] ZoneWords = ["start", "end", "finish", "zone"];

    // Null when the item has no map with the prefix.
    public static ItemScan? Scan(string dir, string prefix)
    {
        var id = Path.GetFileName(dir);

        var vpk = Directory.EnumerateFiles(dir, "*_dir.vpk").FirstOrDefault()
                  ?? Directory.EnumerateFiles(dir, "*.vpk").FirstOrDefault(f => !IsChunk(f));

        if (vpk is null)
        {
            var reason = Directory.EnumerateFiles(dir, "*_legacy.bin").Any()
                ? "a CS:GO item (_legacy.bin), which CS2 can't load"
                : "no vpk; not downloaded yet?";

            return new ItemScan(id, [], 0, reason);
        }

        try
        {
            using var package = new Package();
            package.Read(vpk);

            // Each map is a vpk inside the item's: maps/<name>.vpk, its skybox or prefabs anywhere under maps/.
            var entries = new Dictionary<string, PackageEntry>(StringComparer.OrdinalIgnoreCase);

            foreach (var entry in package.Entries?.GetValueOrDefault("vpk") ?? [])
            {
                if (entry.GetFullPath().StartsWith("maps/", StringComparison.OrdinalIgnoreCase))
                {
                    entries.TryAdd(KeyOf(entry.GetFullPath()), entry);
                }
            }

            if (!entries.Keys.Any(k => IsCandidate(k, prefix)))
            {
                return null;
            }

            var maps = entries.ToDictionary(x => x.Key, x => Read(package, x.Value), StringComparer.OrdinalIgnoreCase);

            // A map another one loads is part of that map, not one of its own.
            var loaded = maps.SelectMany(x => x.Value.Loads.Where(l => !l.Equals(x.Key, StringComparison.OrdinalIgnoreCase)))
                             .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var candidates = maps.Keys.Where(k => IsCandidate(k, prefix)).ToList();

            var scans = candidates.Where(k => !loaded.Contains(k))
                                  .Select(k => Summarize(id, k, maps))
                                  .ToList();

            return new ItemScan(id, scans, candidates.Count - scans.Count, null);
        }
        catch (Exception e)
        {
            return new ItemScan(id, [], 0, $"{e.GetType().Name}: {e.Message}");
        }
    }

    private static bool IsChunk(string path)
        => Path.GetFileNameWithoutExtension(path) is var name
           && name.Length > 4
           && name[^4] == '_'
           && name[^3..].All(char.IsAsciiDigit);

    // Changelevel names: maps/<name>.vpk, not the skyboxes and prefabs in maps/<folder>/.
    private static bool IsCandidate(string key, string prefix)
        => !key.Contains('/') && key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

    // maps/prefabs/x.vpk or maps\prefabs\x.vmap: prefabs/x.
    private static string KeyOf(string path)
    {
        path = path.Replace('\\', '/');

        if (path.StartsWith("maps/", StringComparison.OrdinalIgnoreCase))
        {
            path = path[5..];
        }

        return Path.ChangeExtension(path, null);
    }

    private static MapEntities Read(Package package, PackageEntry entry)
    {
        using var stream = package.GetMemoryMappedStreamIfPossible(entry);
        using var map    = new Package();
        map.SetFileName(entry.GetFullPath());
        map.Read(stream);

        var named = new List<(string Class, string Name)>();
        var loads = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // The map's lumps, and the child lumps its point_templates spawn from.
        foreach (var lumpEntry in map.Entries?.GetValueOrDefault("vents_c") ?? [])
        {
            map.ReadEntry(lumpEntry, out var bytes);

            using var resource = new Resource { FileName = lumpEntry.GetFullPath() };
            resource.Read(new MemoryStream(bytes));

            if (resource.DataBlock is not EntityLump lump)
            {
                continue;
            }

            foreach (var entity in lump.GetEntities())
            {
                var classname = entity.GetStringProperty("classname", "");

                // Names from compiled prefabs start with [PR#], which the server drops when it spawns the entity:
                // [PR#]map_start is map_start to ZoneModule.
                var name = entity.FriendlyTargetName;

                // info_targets too: some maps mark their zones' corners with them (start_left, end_right).
                if ((classname.StartsWith("trigger_", StringComparison.Ordinal) || classname == "info_target")
                    && !string.IsNullOrWhiteSpace(name))
                {
                    named.Add((classname, name));
                }

                foreach (var (_, value) in entity.Children)
                {
                    if (value.ValueType == KVValueType.String
                        && (string) value is var text
                        && text.EndsWith(".vmap", StringComparison.OrdinalIgnoreCase))
                    {
                        loads.Add(KeyOf(text));
                    }
                }
            }
        }

        return new MapEntities(named, loads);
    }

    // The map with every map it loads.
    private static MapScan Summarize(string id, string key, Dictionary<string, MapEntities> maps)
    {
        var named = new List<(string Class, string Name)>();
        var seen  = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { key };
        var queue = new Queue<string>([key]);

        while (queue.TryDequeue(out var part))
        {
            named.AddRange(maps[part].Named);

            foreach (var load in maps[part].Loads)
            {
                if (maps.ContainsKey(load) && seen.Add(load))
                {
                    queue.Enqueue(load);
                }
            }
        }

        var zones = named.Where(t => t.Class == "trigger_multiple").Select(t => t.Name).ToList();

        // What a map zoned under other names, or by other means, has: names ZoneMatcher doesn't take, with the
        // classname unless it's a trigger_multiple. Quoted when they hold a space, as in "map_end ".
        var hints = named.Where(t => !(t.Class == "trigger_multiple" && IsZone(t.Name))
                                     && ZoneWords.Any(w => t.Name.Contains(w, StringComparison.OrdinalIgnoreCase)))
                         .Select(t => (t.Class == "trigger_multiple" ? "" : t.Class + ":")
                                      + (t.Name.Any(char.IsWhiteSpace) ? $"\"{t.Name}\"" : t.Name))
                         .Distinct(StringComparer.Ordinal)
                         .Order(StringComparer.OrdinalIgnoreCase)
                         .ToList();

        return new MapScan(id, key, zones.Any(ZoneMatcher.IsStartZone), zones.Any(ZoneMatcher.IsEndZone), hints);
    }

    private static bool IsZone(string name)
        => ZoneMatcher.IsStartZone(name)
           || ZoneMatcher.IsEndZone(name)
           || ZoneMatcher.IsBonusStartZone(name, out _)
           || ZoneMatcher.IsBonusEndZone(name, out _)
           || ZoneMatcher.IsStageZone(name, out _)
           || ZoneMatcher.IsCheckpointZone(name, out _)
           || ZoneMatcher.IsBonusCheckpointZone(name, out _, out _);
}
