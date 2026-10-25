using System.Reflection.PortableExecutable;

namespace Ildump;

/// <summary>
/// Prints the PE section table and the CLR header of an executable, which is what maps a
/// manifest's RVA to the file offset where an embedded payload actually lives.
/// </summary>
internal static class Sections
{
    public static int Run(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("usage: ildump sections <peFile>");
            return 2;
        }

        var path = Path.GetFullPath(args[0]);
        using var fs = File.OpenRead(path);
        using var pe = new PEReader(fs);

        var headers = pe.PEHeaders;
        Console.WriteLine($"file   {path} ({fs.Length:N0} bytes)");
        Console.WriteLine($"machine {headers.CoffHeader.Machine}  subsystem {headers.PEHeader?.Subsystem}");
        Console.WriteLine($"isDll {headers.IsDll}  corHeader {(headers.CorHeader is null ? "none" : "present")}");
        Console.WriteLine("sections:");
        Console.WriteLine("  name          vaddr     vsize     rawPtr    rawSize   flags");

        foreach (var s in headers.SectionHeaders)
        {
            Console.WriteLine(
                $"  {s.Name,-12}  0x{s.VirtualAddress:X8}  0x{s.VirtualSize:X8}  0x{s.PointerToRawData:X8}  0x{s.SizeOfRawData:X8}  0x{(int)s.SectionCharacteristics:X8}");
        }

        if (headers.PEHeader is { } ph)
        {
            Console.WriteLine($"image size 0x{ph.SizeOfImage:X8}, headers 0x{ph.SizeOfHeaders:X8}");
        }

        return 0;
    }
}
