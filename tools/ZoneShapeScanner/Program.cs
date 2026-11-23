using System.Collections.Concurrent;
using System.Globalization;
using System.Numerics;
using System.Text;
using Source2Surf.Timer.Modules.Zone;
using SteamDatabase.ValvePak;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.Serialization.KeyValues;

// Lists the map zones that don't hold their inside: the trigger_multiples ZoneModule zones by name whose physics is
// shells around an empty inside, a mesh with open edges, or a thin plate. A player inside such a zone and touching none
// of its faces isn't in it, so a jump starts the timer and the landing restarts it. A closed mesh holds its inside. The
// check is the plugin's ZoneSolidity.
//
//   dotnet run -c Release --project tools/ZoneShapeScanner -- <steamapps/workshop/content/730> [prefixes, default surf,bhop]
//
// Writes every zone to zone-shapes.csv in the working folder and prints those that don't hold their inside.

if (args.Length is 0 or > 2 || !Directory.Exists(args[0]))
{
    Console.Error.WriteLine("Usage: ZoneShapeScanner <steamapps/workshop/content/730> [prefixes, comma separated, default surf,bhop]");

    return 1;
}

Console.OutputEncoding = Encoding.UTF8;

var prefixes = (args.Length > 1 ? args[1] : "surf,bhop").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
var dirs     = Directory.GetDirectories(args[0]);
var zones    = new ConcurrentBag<ZoneRow>();
var problems = new ConcurrentBag<string>();
var maps     = 0;

Parallel.ForEach(dirs, dir =>
{
    try
    {
        Interlocked.Add(ref maps, Scanner.Scan(dir, prefixes, zones));
    }
    catch (Exception e)
    {
        problems.Add($"{Path.GetFileName(dir)}: {e.GetType().Name}: {e.Message}");
    }
});

var rows = zones.OrderBy(r => r.Map, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Zone, StringComparer.OrdinalIgnoreCase).ToList();
var csv  = new StringBuilder("workshop_id,map,zone,kind,hulls,meshes,size_x,size_y,size_z,columns,hollow_pct,max_height,footprint_convex_pct,rebuildable\n");

foreach (var r in rows)
{
    var s = r.Report;
    var size = s.Max - s.Min;
    csv.AppendLine(string.Join(',', r.WorkshopId, r.Map, r.Zone, s.Kind, r.Hulls, r.Meshes, F(size.X), F(size.Y), F(size.Z), s.Columns,
                               F(s.HollowPercent), F(s.MaxHeight), F(s.FootprintConvexPercent), s.CanRebuildConvex));
}

File.WriteAllText("zone-shapes.csv", csv.ToString());

var bad = rows.Where(r => !r.Report.HoldsInside).ToList();

Console.WriteLine($"{dirs.Length} workshop items, {maps} maps, {rows.Count} zones; {bad.Count} zones on {bad.Select(r => r.Map).Distinct().Count()} maps don't hold their inside.");

foreach (var kind in bad.GroupBy(r => r.Report.Kind).OrderBy(g => g.Key))
{
    Console.WriteLine($"  {kind.Key}: {kind.Count()} zones on {kind.Select(r => r.Map).Distinct().Count()} maps, "
                      + $"{kind.Count(r => r.Report.CanRebuildConvex)} rebuildable as one convex hull");
}

Console.WriteLine();

foreach (var r in bad)
{
    var s    = r.Report;
    var size = s.Max - s.Min;
    Console.WriteLine($"{r.Map,-32} {r.Zone,-24} {s.Kind,-8} hulls {r.Hulls,2} meshes {r.Meshes,2}  {F(size.X)}x{F(size.Y)}x{F(size.Z)}"
                      + $"  hollow {F(s.HollowPercent)}%  footprint convex {F(s.FootprintConvexPercent)}%"
                      + (s.CanRebuildConvex ? "  rebuildable" : "") + $"  ({r.WorkshopId})");
}

foreach (var p in problems.Order(StringComparer.Ordinal))
{
    Console.WriteLine($"Not scanned: {p}");
}

return 0;

static string F(float v)
    => v.ToString("0.#", CultureInfo.InvariantCulture);

internal sealed record ZoneRow(string WorkshopId, string Map, string Zone, int Hulls, int Meshes, ZoneSolidityReport Report);

internal static class Scanner
{
    // How many prefix maps the item holds.
    public static int Scan(string dir, string[] prefixes, ConcurrentBag<ZoneRow> rows)
    {
        var id  = Path.GetFileName(dir);
        var vpk = Directory.EnumerateFiles(dir, "*_dir.vpk").FirstOrDefault()
                  ?? Directory.EnumerateFiles(dir, "*.vpk").FirstOrDefault(f => !IsChunk(f));

        if (vpk is null)
        {
            return 0;
        }

        using var package = new Package();
        package.Read(vpk);

        var count = 0;

        foreach (var entry in package.Entries?.GetValueOrDefault("vpk") ?? [])
        {
            var path = entry.GetFullPath();

            // maps/<name>.vpk; a map's skybox and prefabs in maps/<folder>/ are read with it, in its own lumps.
            if (!path.StartsWith("maps/", StringComparison.OrdinalIgnoreCase) || path[5..].Contains('/'))
            {
                continue;
            }

            var name = Path.GetFileNameWithoutExtension(path);

            if (!prefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            count++;

            using var stream = package.GetMemoryMappedStreamIfPossible(entry);
            using var map    = new Package();
            map.SetFileName(path);
            map.Read(stream);

            foreach (var (zone, model) in Zones(map))
            {
                if (Shape(map, model) is var (hulls, meshes) && hulls.Count + meshes.Count > 0)
                {
                    rows.Add(new ZoneRow(id, name, zone, hulls.Count, meshes.Count, ZoneSolidityCheck.Measure(hulls, meshes)));
                }
            }
        }

        return count;
    }

    private static bool IsChunk(string path)
        => Path.GetFileNameWithoutExtension(path) is var n && n.Length > 4 && n[^4] == '_' && n[^3..].All(char.IsAsciiDigit);

    private static bool IsZone(string name)
        => ZoneMatcher.IsStartZone(name)
           || ZoneMatcher.IsEndZone(name)
           || ZoneMatcher.IsBonusStartZone(name, out _)
           || ZoneMatcher.IsBonusEndZone(name, out _)
           || ZoneMatcher.IsStageZone(name, out _)
           || ZoneMatcher.IsCheckpointZone(name, out _)
           || ZoneMatcher.IsBonusCheckpointZone(name, out _, out _);

    // The zone triggers in the map's lumps, with their models.
    private static IEnumerable<(string Zone, string Model)> Zones(Package map)
    {
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
                // [PR#]map_start from a compiled prefab is map_start to the server.
                if (entity.GetStringProperty("classname", "") == "trigger_multiple"
                    && entity.FriendlyTargetName is { Length: > 0 } name
                    && IsZone(name)
                    && entity.GetStringProperty("model", "") is { Length: > 0 } model)
                {
                    yield return (name, model);
                }
            }
        }
    }

    private static (List<SolidHull>, List<SolidMesh>)? Shape(Package map, string model)
    {
        if (map.FindEntry(model.EndsWith("_c", StringComparison.Ordinal) ? model : model + "_c") is not { } entry)
        {
            return null;
        }

        map.ReadEntry(entry, out var bytes);

        using var resource = new Resource { FileName = entry.GetFullPath() };
        resource.Read(new MemoryStream(bytes));

        if (resource.DataBlock is not Model mdl || mdl.GetEmbeddedPhys() is not { } phys)
        {
            return null;
        }

        var hulls  = new List<SolidHull>();
        var meshes = new List<SolidMesh>();

        foreach (var part in phys.Parts)
        {
            foreach (var h in part.Shape.Hulls)
            {
                var planes = h.Shape.GetPlanes().ToArray().Select(p => new SolidPlane(p.Normal, p.Offset)).ToArray();
                hulls.Add(new SolidHull(planes, h.Shape.GetVertexPositions().ToArray()));
            }

            foreach (var m in part.Shape.Meshes)
            {
                var triangles = m.Shape.GetTriangles().ToArray().SelectMany(t => new[] { t.X, t.Y, t.Z }).ToArray();
                meshes.Add(new SolidMesh(m.Shape.GetVertices().ToArray(), triangles));
            }
        }

        return (hulls, meshes);
    }
}
