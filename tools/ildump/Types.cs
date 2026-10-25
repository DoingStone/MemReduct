using System.Reflection.PortableExecutable;

namespace Ildump;

/// <summary>
/// Lists every type and method in an assembly, so a caller can confirm what a build
/// actually contains without guessing at names.
/// </summary>
internal static class Types
{
    public static int Run(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("usage: ildump types <assembly> [filter]");
            return 2;
        }

        var path = Path.GetFullPath(args[0]);
        var filter = args.Length > 1 ? args[1] : null;

        using var fs = File.OpenRead(path);
        using var pe = new PEReader(fs);
        if (!pe.HasMetadata)
        {
            Console.Error.WriteLine("no metadata in " + path);
            return 3;
        }

        var md = pe.GetMetadataReader();
        var count = 0;

        foreach (var th in md.TypeDefinitions)
        {
            var td = md.GetTypeDefinition(th);
            var full = md.GetString(td.Namespace) + "." + md.GetString(td.Name);
            if (filter is not null && !full.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Console.WriteLine(full);
            foreach (var mh in td.GetMethods())
            {
                var m = md.GetMethodDefinition(mh);
                Console.WriteLine($"    {md.GetString(m.Name)}  body=0x{m.RelativeVirtualAddress:X}");
                count++;
            }
        }

        Console.WriteLine($"{count} method(s) in {path}");
        return 0;
    }
}
