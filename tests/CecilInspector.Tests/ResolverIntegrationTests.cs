using CecilInspector.Cli;
using CecilInspector.Core;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace CecilInspector.Tests;

public sealed class ResolverIntegrationTests
{
    [Fact]
    public void ResolvesPropertySemanticsAcrossSiblingDirectories()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cecil-inspector-{Guid.NewGuid():N}");
        var app = Path.Combine(root, "app");
        var lib = Path.Combine(root, "lib");
        Directory.CreateDirectory(app);
        Directory.CreateDirectory(lib);
        try
        {
            CreateModelAssembly(Path.Combine(lib, "Model.dll"), "FetchValue", "Logical");
            CreateCallerAssembly(Path.Combine(app, "Caller.dll"), "FetchValue");

            var options = new SearchOptions(
                root,
                "Logical",
                SearchKinds.Property,
                SearchScope.References,
                MatchMode.Exact,
                true,
                true,
                SymbolMode.Off,
                100,
                null,
                []);

            var result = new AssemblySearcher().Search(options);

            var hit = Assert.Single(result.Hits);
            Assert.Equal(HitKind.Property, hit.Kind);
            Assert.Contains("::Logical", hit.Symbol, StringComparison.Ordinal);
            Assert.DoesNotContain("FetchValue", hit.Symbol, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void PrefersDependencyNextToTargetOverEarlierSiblingDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cecil-inspector-{Guid.NewGuid():N}");
        var earlier = Path.Combine(root, "0-other");
        var app = Path.Combine(root, "z-app");
        Directory.CreateDirectory(earlier);
        Directory.CreateDirectory(app);
        try
        {
            CreateModelAssembly(Path.Combine(earlier, "Model.dll"), "FetchValue", null);
            CreateModelAssembly(Path.Combine(app, "Model.dll"), "FetchValue", "Logical");
            CreateCallerAssembly(Path.Combine(app, "Caller.dll"), "FetchValue");

            var options = new SearchOptions(
                root,
                "Logical",
                SearchKinds.Property,
                SearchScope.References,
                MatchMode.Exact,
                true,
                true,
                SymbolMode.Off,
                100,
                null,
                []);

            var result = new AssemblySearcher().Search(options);

            var hit = Assert.Single(result.Hits);
            Assert.Equal(Path.Combine(app, "Caller.dll"), hit.AssemblyPath);
            Assert.Equal(HitKind.Property, hit.Kind);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void MissingDependencyIsReportedWhenMemberSemanticsCannotBeResolved()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cecil-inspector-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var caller = Path.Combine(root, "Caller.dll");
        try
        {
            CreateCallerAssembly(caller, "FetchValue");
            var options = new SearchOptions(
                caller,
                "Logical",
                SearchKinds.Property,
                SearchScope.References,
                MatchMode.Exact,
                true,
                true,
                SymbolMode.Off,
                100,
                null,
                []);

            var result = new AssemblySearcher().Search(options);

            Assert.Empty(result.Hits);
            Assert.Equal(1, result.FilesSucceeded);
            var error = Assert.Single(result.Errors);
            Assert.Contains("分類が不完全", error.Message, StringComparison.Ordinal);
            Assert.Contains("Model", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void SkipsAdjacentWrongVersionAndResolvesMatchingReferencePathAssembly()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cecil-inspector-{Guid.NewGuid():N}");
        var app = Path.Combine(root, "app");
        var reference = Path.Combine(root, "reference");
        Directory.CreateDirectory(app);
        Directory.CreateDirectory(reference);
        try
        {
            CreateModelAssembly(
                Path.Combine(app, "Model.dll"), "FetchValue", null, new Version(1, 0, 0, 0));
            CreateModelAssembly(
                Path.Combine(reference, "Model.dll"), "FetchValue", "Logical", new Version(2, 0, 0, 0));
            var caller = Path.Combine(app, "Caller.dll");
            CreateCallerAssembly(caller, "FetchValue", new Version(2, 0, 0, 0));

            var options = new SearchOptions(
                caller,
                "Logical",
                SearchKinds.Property,
                SearchScope.References,
                MatchMode.Exact,
                true,
                true,
                SymbolMode.Off,
                100,
                null,
                [reference]);

            var result = new AssemblySearcher().Search(options);

            var hit = Assert.Single(result.Hits);
            Assert.Equal(HitKind.Property, hit.Kind);
            Assert.Empty(result.Errors);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static void CreateModelAssembly(
        string path,
        string getterName,
        string? propertyName,
        Version? version = null)
    {
        using var assembly = AssemblyDefinition.CreateAssembly(
            new AssemblyNameDefinition("Model", version ?? new Version(1, 0, 0, 0)),
            "Model",
            ModuleKind.Dll);
        var module = assembly.MainModule;
        var type = new TypeDefinition("Fixtures", "Model", TypeAttributes.Public | TypeAttributes.Class);
        module.Types.Add(type);

        var getter = new MethodDefinition(
            getterName,
            MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName,
            module.TypeSystem.Int32);
        getter.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4_7));
        getter.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        type.Methods.Add(getter);

        if (propertyName is not null)
        {
            var property = new PropertyDefinition(propertyName, PropertyAttributes.None, module.TypeSystem.Int32)
            {
                GetMethod = getter,
            };
            type.Properties.Add(property);
        }
        assembly.Write(path);
    }

    private static void CreateCallerAssembly(string path, string getterName, Version? modelVersion = null)
    {
        using var assembly = AssemblyDefinition.CreateAssembly(
            new AssemblyNameDefinition("Caller", new Version(1, 0, 0, 0)),
            "Caller",
            ModuleKind.Dll);
        var module = assembly.MainModule;
        var modelAssembly = new AssemblyNameReference(
            "Model",
            modelVersion ?? new Version(1, 0, 0, 0));
        module.AssemblyReferences.Add(modelAssembly);
        var modelType = new TypeReference("Fixtures", "Model", module, modelAssembly);
        var getter = new MethodReference(getterName, module.TypeSystem.Int32, modelType)
        {
            HasThis = true,
        };

        var type = new TypeDefinition("Fixtures", "Caller", TypeAttributes.Public | TypeAttributes.Class);
        module.Types.Add(type);
        var method = new MethodDefinition(
            "Call",
            MethodAttributes.Public | MethodAttributes.Static,
            module.TypeSystem.Int32);
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldnull));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Callvirt, getter));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        type.Methods.Add(method);
        assembly.Write(path);
    }
}
