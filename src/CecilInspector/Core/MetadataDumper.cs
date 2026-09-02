using CecilInspector.Cli;
using CecilInspector.Output;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace CecilInspector.Core;

public sealed class MetadataDumper
{
    public DumpResult Dump(
        DumpOptions options,
        IEnumerable<string> files,
        int filesDiscovered,
        IReadOnlyList<string> searchDirectories,
        IReadOnlyList<ScanError> discoveryErrors,
        TextWriter writer)
    {
        writer = new GuardedTextWriter(writer);
        var errors = new List<ScanError>(discoveryErrors);
        var succeeded = 0;
        var referenceDirectories = CecilResolverFactory.ValidateReferencePaths(options.ReferencePaths);

        foreach (var file in files)
        {
            using var resolver = CecilResolverFactory.Create(file, referenceDirectories, searchDirectories);
            try
            {
                using var module = CecilModuleReader.Read(file, options.SymbolMode, resolver);
                AppendModule(writer, module, file, options.IncludeIl);
                if (filesDiscovered == 1 && module.Assembly is not null)
                {
                    foreach (var secondaryModule in module.Assembly.Modules.Where(candidate => candidate != module))
                    {
                        AppendModule(writer, secondaryModule, secondaryModule.FileName, options.IncludeIl);
                    }
                }

                succeeded++;
            }
            catch (Exception ex) when (ExceptionPolicy.IsRecoverableAssemblyError(ex))
            {
                errors.Add(new ScanError(file, ex.Message));
                writer.WriteLine($"Incomplete assembly: {TextSanitizer.Escape(file)}");
            }
        }

        writer.WriteLine($"Summary: discovered={filesDiscovered}, succeeded={succeeded}, errors={errors.Count}");
        return new DumpResult(errors, filesDiscovered, succeeded);
    }

    private static void AppendModule(TextWriter writer, ModuleDefinition module, string file, bool includeIl)
    {
        writer.WriteLine($"Assembly: {TextSanitizer.Escape(module.Assembly?.Name.FullName ?? "(netmodule)")}");
        writer.WriteLine($"File: {TextSanitizer.Escape(file)}");
        writer.WriteLine($"Module: {TextSanitizer.Escape(module.Name)}");
        writer.WriteLine($"Runtime: {TextSanitizer.Escape(module.RuntimeVersion)}");
        writer.WriteLine($"Architecture: {module.Architecture}");
        writer.WriteLine($"Kind: {module.Kind}");
        writer.WriteLine($"Has symbols: {module.HasSymbols}");

        writer.WriteLine("Assembly references:");
        foreach (var reference in module.AssemblyReferences)
        {
            writer.WriteLine($"  {TextSanitizer.Escape(reference.FullName)}");
        }

        writer.WriteLine("Resources:");
        foreach (var resource in module.Resources)
        {
            writer.WriteLine($"  [{resource.ResourceType}] {TextSanitizer.Escape(resource.Name)}");
        }

        writer.WriteLine("Types:");
        AppendTypes(writer, module.Types, includeIl);

        writer.WriteLine();
    }

    private static void AppendTypes(TextWriter writer, IEnumerable<TypeDefinition> roots, bool includeIl)
    {
        var stack = new Stack<(TypeDefinition Type, int Depth)>(roots.Reverse().Select(type => (type, 1)));
        while (stack.Count > 0)
        {
            var (type, depth) = stack.Pop();
            AppendType(writer, type, Indent(depth), includeIl);
            for (var index = type.NestedTypes.Count - 1; index >= 0; index--)
            {
                stack.Push((type.NestedTypes[index], depth + 1));
            }
        }
    }

    private static void AppendType(TextWriter writer, TypeDefinition type, string indent, bool includeIl)
    {
        writer.WriteLine($"{indent}Type: {TextSanitizer.Escape(CecilFormatting.Type(type))} [{type.Attributes}]");
        if (type.BaseType is not null)
        {
            writer.WriteLine($"{indent}  Base: {TextSanitizer.Escape(CecilFormatting.Type(type.BaseType))}");
        }

        foreach (var @interface in type.Interfaces)
        {
            writer.WriteLine($"{indent}  Interface: {TextSanitizer.Escape(CecilFormatting.Type(@interface.InterfaceType))}");
        }

        foreach (var field in type.Fields)
        {
            writer.WriteLine($"{indent}  Field: {TextSanitizer.Escape(CecilFormatting.Field(field))} [{field.Attributes}]");
        }

        foreach (var property in type.Properties)
        {
            writer.WriteLine($"{indent}  Property: {TextSanitizer.Escape(CecilFormatting.Property(property))} [{property.Attributes}]");
        }

        foreach (var @event in type.Events)
        {
            writer.WriteLine($"{indent}  Event: {TextSanitizer.Escape(CecilFormatting.Event(@event))} [{@event.Attributes}]");
        }

        foreach (var method in type.Methods)
        {
            var location = DebugLocations.First(method);
            writer.WriteLine($"{indent}  Method: {TextSanitizer.Escape(CecilFormatting.Method(method))} [{method.Attributes}]" +
                             (location is null ? string.Empty : $" @ {TextSanitizer.Escape(location.ToString())}"));
            foreach (var parameter in method.Parameters)
            {
                writer.WriteLine($"{indent}    Parameter: {TextSanitizer.Escape(parameter.Name)} : " +
                                 $"{TextSanitizer.Escape(CecilFormatting.Type(parameter.ParameterType))} [{parameter.Attributes}]");
            }

            if (includeIl && method.HasBody)
            {
                var locations = DebugLocations.CreateMapper(method);
                foreach (var instruction in method.Body.Instructions)
                {
                    var operand = instruction.Operand is null
                        ? string.Empty
                        : $" {TextSanitizer.Escape(instruction.Operand.ToString())}";
                    var instructionLocation = locations.ForInstruction(instruction);
                    var source = instructionLocation is null
                        ? string.Empty
                        : $" // {TextSanitizer.Escape(instructionLocation.ToString())}";
                    writer.WriteLine($"{indent}    IL_{instruction.Offset:X4}: {instruction.OpCode}{operand}{source}");
                }
            }
        }

    }

    private static string Indent(int depth) => depth <= 40
        ? new string(' ', depth * 2)
        : $"{new string(' ', 80)}[{depth}] ";
}
