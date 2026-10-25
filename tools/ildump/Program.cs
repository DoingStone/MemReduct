using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

// Dumps a method body's IL from an assembly, plus the #US user-string heap entries it
// references, so a built binary can be compared against its source.
// Usage: ildump <assembly> <TypeFullName> <MethodName>

var subcommand = args.Length > 1 ? args[0] : string.Empty;

if (subcommand == "bundle")
{
    return Ildump.Bundle.Run(args[1..]);
}

if (subcommand == "scan")
{
    return Ildump.Scan.Run(args[1..]);
}

if (subcommand == "ascii")
{
    return Ildump.Ascii.Run(args[1..]);
}

if (subcommand == "hex")
{
    return Ildump.Hex.Run(args[1..]);
}

if (subcommand == "sections")
{
    return Ildump.Sections.Run(args[1..]);
}

if (subcommand == "types")
{
    return Ildump.Types.Run(args[1..]);
}

if (args.Length < 3)
{
    Console.Error.WriteLine("usage: ildump <assembly> <TypeFullName> <MethodName>");
    Console.Error.WriteLine("       ildump bundle <exe> [nameFilter]");
    Console.Error.WriteLine("       ildump scan <file> <literal> [contextBytes]");
    return 2;
}

var path = Path.GetFullPath(args[0]);
var typeName = args[1];
var methodName = args[2];

using var stream = File.OpenRead(path);
using var pe = new PEReader(stream);
var md = pe.GetMetadataReader();

var handles = new List<MethodDefinitionHandle>();
foreach (var th in md.TypeDefinitions)
{
    var td = md.GetTypeDefinition(th);
    var full = md.GetString(td.Namespace) + "." + md.GetString(td.Name);
    if (!string.Equals(full, typeName, StringComparison.Ordinal))
    {
        continue;
    }

    foreach (var mh in td.GetMethods())
    {
        var m = md.GetMethodDefinition(mh);
        if (md.GetString(m.Name) == methodName)
        {
            handles.Add(mh);
        }
    }
}

if (handles.Count == 0)
{
    Console.Error.WriteLine($"no method {typeName}.{methodName} in {path}");
    return 3;
}

var opcodes = new OpCode[0x200];
foreach (var f in typeof(System.Reflection.Emit.OpCodes).GetFields(
             System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
{
    var op = (System.Reflection.Emit.OpCode)f.GetValue(null)!;
    opcodes[(op.Size == 1 ? op.Value : 0x100 | op.Value)] = op;
}

foreach (var mh in handles)
{
    var m = md.GetMethodDefinition(mh);
    Console.WriteLine($"=== {path}");
    Console.WriteLine($"=== {typeName}.{md.GetString(m.Name)}  sig-params={m.GetParameters().Count}  body-offset=0x{m.RelativeVirtualAddress:X}");

    var body = pe.GetMethodBody(m.RelativeVirtualAddress);
    var il = body.GetILBytes()!;
    var i = 0;

    while (i < il.Length)
    {
        var start = i;
        var b = il[i++];
        var op = opcodes[b == 0xFE ? 0x100 | il[i++] : b];

        string operand;
        switch (op.OperandType)
        {
            case System.Reflection.Emit.OperandType.InlineNone:
                operand = string.Empty;
                break;
            case System.Reflection.Emit.OperandType.ShortInlineI:
                operand = ((sbyte)il[i++]).ToString();
                break;
            case System.Reflection.Emit.OperandType.ShortInlineVar:
                operand = il[i++].ToString();
                break;
            case System.Reflection.Emit.OperandType.InlineVar:
                operand = BitConverter.ToUInt16(il, i).ToString();
                i += 2;
                break;
            case System.Reflection.Emit.OperandType.InlineI:
                operand = BitConverter.ToInt32(il, i).ToString();
                i += 4;
                break;
            case System.Reflection.Emit.OperandType.InlineI8:
                operand = BitConverter.ToInt64(il, i).ToString();
                i += 8;
                break;
            case System.Reflection.Emit.OperandType.ShortInlineR:
                operand = BitConverter.ToSingle(il, i).ToString("R");
                i += 4;
                break;
            case System.Reflection.Emit.OperandType.InlineR:
                operand = BitConverter.ToDouble(il, i).ToString("R");
                i += 8;
                break;
            case System.Reflection.Emit.OperandType.ShortInlineBrTarget:
                {
                    var d = (sbyte)il[i++];
                    operand = $"IL_{i + d:X4}";
                    break;
                }
            case System.Reflection.Emit.OperandType.InlineBrTarget:
                {
                    var d = BitConverter.ToInt32(il, i);
                    i += 4;
                    operand = $"IL_{i + d:X4}";
                    break;
                }
            case System.Reflection.Emit.OperandType.InlineSwitch:
                {
                    var n = BitConverter.ToInt32(il, i);
                    i += 4;
                    var targets = new List<string>();
                    var baseOffset = i + (4 * n);
                    for (var k = 0; k < n; k++)
                    {
                        targets.Add($"IL_{baseOffset + BitConverter.ToInt32(il, i):X4}");
                        i += 4;
                    }
                    operand = string.Join(", ", targets);
                    break;
                }
            case System.Reflection.Emit.OperandType.InlineString:
                {
                    var h = MetadataTokens.UserStringHandle(BitConverter.ToInt32(il, i));
                    i += 4;
                    operand = "\"" + md.GetUserString(h).Replace("\n", "\\n") + "\"";
                    break;
                }
            case System.Reflection.Emit.OperandType.InlineField:
                {
                    var h = MetadataTokens.FieldDefinitionHandle(BitConverter.ToInt32(il, i));
                    i += 4;
                    var f = md.GetFieldDefinition(h);
                    operand = md.GetString(f.Name);
                    break;
                }
            case System.Reflection.Emit.OperandType.InlineMethod:
                {
                    var tok = BitConverter.ToInt32(il, i);
                    i += 4;
                    operand = DescribeMethod(md, tok);
                    break;
                }
            case System.Reflection.Emit.OperandType.InlineType:
                {
                    var tok = BitConverter.ToInt32(il, i);
                    i += 4;
                    operand = DescribeType(md, tok);
                    break;
                }
            case System.Reflection.Emit.OperandType.InlineTok:
                {
                    var tok = BitConverter.ToInt32(il, i);
                    i += 4;
                    operand = "tok(0x" + tok.ToString("X8") + ")";
                    break;
                }
            case System.Reflection.Emit.OperandType.InlineSig:
                {
                    var tok = BitConverter.ToInt32(il, i);
                    i += 4;
                    operand = "sig(0x" + tok.ToString("X8") + ")";
                    break;
                }
            default:
                operand = "?";
                break;
        }

        Console.WriteLine($"  IL_{start:X4}: {op.Name,-12} {operand}");
    }
}

return 0;

static string DescribeMethod(MetadataReader md, int token)
{
    var handle = MetadataTokens.EntityHandle(token);
    switch (handle.Kind)
    {
        case HandleKind.MethodDefinition:
            {
                var m = md.GetMethodDefinition((MethodDefinitionHandle)handle);
                var t = md.GetTypeDefinition(m.GetDeclaringType());
                return $"{md.GetString(t.Name)}::{md.GetString(m.Name)}";
            }
        case HandleKind.MemberReference:
            {
                var mr = md.GetMemberReference((MemberReferenceHandle)handle);
                var parent = mr.Parent;
                var owner = parent.Kind == HandleKind.TypeReference
                    ? md.GetString(md.GetTypeReference((TypeReferenceHandle)parent).Name)
                    : parent.Kind.ToString();
                return $"{owner}::{md.GetString(mr.Name)}";
            }
        case HandleKind.MethodSpecification:
            {
                var ms = md.GetMethodSpecification((MethodSpecificationHandle)handle);
                return "spec(" + DescribeMethod(md, MetadataTokens.GetToken(ms.Method)) + ")";
            }
        default:
            return handle.Kind.ToString();
    }
}

static string DescribeType(MetadataReader md, int token)
{
    var handle = MetadataTokens.EntityHandle(token);
    if (handle.Kind == HandleKind.TypeReference)
    {
        var tr = md.GetTypeReference((TypeReferenceHandle)handle);
        return md.GetString(tr.Namespace) + "." + md.GetString(tr.Name);
    }

    if (handle.Kind == HandleKind.TypeDefinition)
    {
        var td = md.GetTypeDefinition((TypeDefinitionHandle)handle);
        return md.GetString(td.Namespace) + "." + md.GetString(td.Name);
    }

    return handle.Kind.ToString();
}
