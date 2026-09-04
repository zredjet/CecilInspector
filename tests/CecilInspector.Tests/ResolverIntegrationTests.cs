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
        using var temp = new TempDirectory();
        var root = temp.Path;
        var app = Path.Combine(root, "app");
        var lib = Path.Combine(root, "lib");
        Directory.CreateDirectory(app);
        Directory.CreateDirectory(lib);
        CreateModelAssembly(Path.Combine(lib, "Model.dll"), "FetchValue", "Logical");
        CreateCallerAssembly(Path.Combine(app, "Caller.dll"), "FetchValue");

        var options = new SearchOptions(
            root,
            "Logical",
            SearchKinds.Property,
            SearchScope.References,
            MatchMode.Exact,
            IgnoreCase: true,
            Recursive: true,
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

    [Fact]
    public void PrefersDependencyNextToTargetOverEarlierSiblingDirectory()
    {
        using var temp = new TempDirectory();
        var root = temp.Path;
        var earlier = Path.Combine(root, "0-other");
        var app = Path.Combine(root, "z-app");
        Directory.CreateDirectory(earlier);
        Directory.CreateDirectory(app);
        CreateModelAssembly(Path.Combine(earlier, "Model.dll"), "FetchValue", null);
        CreateModelAssembly(Path.Combine(app, "Model.dll"), "FetchValue", "Logical");
        CreateCallerAssembly(Path.Combine(app, "Caller.dll"), "FetchValue");

        var options = new SearchOptions(
            root,
            "Logical",
            SearchKinds.Property,
            SearchScope.References,
            MatchMode.Exact,
            IgnoreCase: true,
            Recursive: true,
            SymbolMode.Off,
            100,
            null,
            []);

        var result = new AssemblySearcher().Search(options);

        var hit = Assert.Single(result.Hits);
        Assert.Equal(Path.Combine(app, "Caller.dll"), hit.AssemblyPath);
        Assert.Equal(HitKind.Property, hit.Kind);
    }

    [Fact]
    public void MissingDependencyIsReportedWhenMemberSemanticsCannotBeResolved()
    {
        using var temp = new TempDirectory();
        var root = temp.Path;
        var caller = Path.Combine(root, "Caller.dll");
        CreateCallerAssembly(caller, "FetchValue");
        var options = new SearchOptions(
            caller,
            "Logical",
            SearchKinds.Property,
            SearchScope.References,
            MatchMode.Exact,
            IgnoreCase: true,
            Recursive: true,
            SymbolMode.Off,
            100,
            null,
            []);

        var result = new AssemblySearcher().Search(options);

        // "FetchValue" carries no accessor prefix, so without MethodSemantics nothing can
        // classify it as a property; the unresolved-accessor fallback does not apply.
        Assert.Empty(result.Hits);
        Assert.Equal(1, result.FilesSucceeded);
        var error = Assert.Single(result.Errors);
        Assert.Contains("分類が不完全", error.Message, StringComparison.Ordinal);
        Assert.Contains("Model", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SkipsAdjacentWrongVersionAndResolvesMatchingReferencePathAssembly()
    {
        using var temp = new TempDirectory();
        var root = temp.Path;
        var app = Path.Combine(root, "app");
        var reference = Path.Combine(root, "reference");
        Directory.CreateDirectory(app);
        Directory.CreateDirectory(reference);
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
            IgnoreCase: true,
            Recursive: true,
            SymbolMode.Off,
            100,
            null,
            [reference]);

        var result = new AssemblySearcher().Search(options);

        var hit = Assert.Single(result.Hits);
        Assert.Equal(HitKind.Property, hit.Kind);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void NewerAdjacentCopyBindsLikeARedirectAndOlderOneIsExplained()
    {
        using var temp = new TempDirectory();
        var app = temp.CreateSubdirectory("app");
        var caller = Path.Combine(app, "Caller.dll");
        CreateCallerAssembly(caller, "FetchValue", new Version(2, 0, 0, 0));

        CreateModelAssembly(Path.Combine(app, "Model.dll"), "FetchValue", "Logical", new Version(3, 0, 0, 0));
        var newer = Search(caller, "Logical", SearchKinds.Property);
        Assert.Single(newer.Hits);
        Assert.Empty(newer.Errors);

        File.Delete(Path.Combine(app, "Model.dll"));
        CreateModelAssembly(Path.Combine(app, "Model.dll"), "FetchValue", "Logical", new Version(1, 0, 0, 0));
        var older = Search(caller, "Logical", SearchKinds.Property);
        Assert.Empty(older.Hits);
        var error = Assert.Single(older.Errors);
        Assert.Contains("Model.dll は Version=1.0.0.0 で要求 2.0.0.0 より古いです", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnresolvableGetAccessorYieldsMethodAndPropertyCandidates()
    {
        using var temp = new TempDirectory();
        var root = temp.Path;
        var caller = Path.Combine(root, "Caller.dll");
        CreateCallerAssembly(caller, "get_Logical");

        var property = Search(caller, "Logical", SearchKinds.Property);
        var propertyHit = Assert.Single(property.Hits);
        Assert.Equal(HitKind.Property, propertyHit.Kind);
        Assert.Equal("Fixtures.Model::Logical : System.Int32", propertyHit.Symbol);

        var method = Search(caller, "get_Logical", SearchKinds.Method);
        var methodHit = Assert.Single(method.Hits);
        Assert.Equal(HitKind.Method, methodHit.Kind);

        var all = Search(caller, "Logical", SearchKinds.All, MatchMode.Contains);
        Assert.Equal(2, all.Hits.Count);
        Assert.Contains(all.Hits, hit => hit.Kind == HitKind.Method);
        Assert.Contains(all.Hits, hit => hit.Kind == HitKind.Property);
        var error = Assert.Single(all.Errors);
        Assert.Contains("分類が不完全", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnresolvableAddAccessorYieldsEventCandidate()
    {
        using var temp = new TempDirectory();
        var root = temp.Path;
        var caller = Path.Combine(root, "Caller.dll");
        CreateCallerAssembly(caller, "add_Changed", handlerParameter: true);

        var result = Search(caller, "Changed", SearchKinds.Event);

        var hit = Assert.Single(result.Hits);
        Assert.Equal(HitKind.Event, hit.Kind);
        Assert.Equal("Fixtures.Model::Changed : System.EventHandler", hit.Symbol);
        Assert.Empty(Search(caller, "Changed", SearchKinds.Property).Hits);
    }

    [Fact]
    public void UnresolvableRaiseAccessorYieldsEventWithoutAType()
    {
        using var temp = new TempDirectory();
        var caller = Path.Combine(temp.Path, "Caller.dll");
        CreateCallerAssembly(caller, "raise_Changed", handlerParameter: true);

        var result = Search(caller, "Changed", SearchKinds.Event);

        var hit = Assert.Single(result.Hits);
        Assert.Equal("Fixtures.Model::Changed", hit.Symbol);
    }

    [Fact]
    public void UnresolvableExplicitInterfaceImplementationMatchesItsMemberName()
    {
        using var temp = new TempDirectory();
        var caller = Path.Combine(temp.Path, "Caller.dll");
        CreateCallerAssembly(caller, "System.IDisposable.Dispose");

        var simple = Search(caller, "Dispose", SearchKinds.Method);
        var qualified = Search(caller, "Fixtures.Model::Dispose", SearchKinds.Method);
        var metadata = Search(caller, "System.IDisposable.Dispose", SearchKinds.Method);

        Assert.Single(simple.Hits);
        Assert.Single(qualified.Hits);
        Assert.Single(metadata.Hits);
    }

    [Theory]
    [InlineData(0.30)]
    [InlineData(0.60)]
    [InlineData(0.90)]
    public void CorruptCandidateNextToTargetIsSkippedInFavourOfReferencePath(double keepRatio)
    {
        using var temp = new TempDirectory();
        var app = temp.CreateSubdirectory("app");
        var reference = temp.CreateSubdirectory("reference");
        CreateModelAssembly(Path.Combine(reference, "Model.dll"), "FetchValue", "Logical");
        var good = File.ReadAllBytes(Path.Combine(reference, "Model.dll"));
        File.WriteAllBytes(Path.Combine(app, "Model.dll"), good[..(int)(good.Length * keepRatio)]);
        var caller = Path.Combine(app, "Caller.dll");
        CreateCallerAssembly(caller, "FetchValue");
        var options = new SearchOptions(
            caller, "Logical", SearchKinds.Property, SearchScope.References, MatchMode.Exact,
            true, true, SymbolMode.Off, 100, null, [reference]);

        var result = new AssemblySearcher().Search(options);

        var hit = Assert.Single(result.Hits);
        Assert.Equal(HitKind.Property, hit.Kind);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void ArrayPseudoMethodsAreNotReportedAsUnresolvedDependencies()
    {
        using var temp = new TempDirectory();
        var caller = Path.Combine(temp.Path, "Caller.dll");
        CreateArrayCallerAssembly(caller);

        var result = Search(caller, "Get", SearchKinds.Method);

        Assert.Empty(result.Errors);
        var hit = Assert.Single(result.Hits);
        Assert.StartsWith("System.Int32[", hit.Symbol, StringComparison.Ordinal);
        Assert.Contains("]::Get(System.Int32, System.Int32) : System.Int32", hit.Symbol, StringComparison.Ordinal);
    }

    [Fact]
    public void UnresolvedMembersAreAggregatedPerDependency()
    {
        using var temp = new TempDirectory();
        var root = temp.Path;
        var caller = Path.Combine(root, "Caller.dll");
        CreateCallerAssembly(caller, "get_Logical", secondGetterName: "get_Other");

        var result = Search(caller, "Nothing", SearchKinds.All, MatchMode.Contains);

        var error = Assert.Single(result.Errors);
        Assert.Contains("Model", error.Message, StringComparison.Ordinal);
        Assert.Contains("2 件", error.Message, StringComparison.Ordinal);
        Assert.Contains("分類が不完全", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FailedResolutionIsNotProbedAgain()
    {
        using var temp = new TempDirectory();
        var root = temp.Path;
        using var resolver = CecilResolverFactory.Create(Path.Combine(root, "Target.dll"), [], [root]);
        var missing = new AssemblyNameReference("DoesNotExist", new Version(1, 0, 0, 0));

        Assert.Throws<AssemblyResolutionException>(() => resolver.Resolve(missing));
        var probesAfterFirstAttempt = resolver.ProbeCount;
        Assert.Throws<AssemblyResolutionException>(() => resolver.Resolve(missing));

        Assert.True(probesAfterFirstAttempt > 0);
        Assert.Equal(probesAfterFirstAttempt, resolver.ProbeCount);
    }

    [Fact]
    public void SameFileIsLoadedOnceForDifferentCompatibleVersions()
    {
        using var temp = new TempDirectory();
        var root = temp.Path;
        CreateModelAssembly(Path.Combine(root, "Model.dll"), "FetchValue", null, new Version(2, 0, 0, 0));
        using var resolver = CecilResolverFactory.Create(Path.Combine(root, "Target.dll"), [], [root]);

        var newer = resolver.Resolve(new AssemblyNameReference("Model", new Version(2, 0, 0, 0)));
        var older = resolver.Resolve(new AssemblyNameReference("Model", new Version(1, 0, 0, 0)));
        var error = Assert.Throws<AssemblyResolutionException>(() =>
            resolver.Resolve(new AssemblyNameReference("Model", new Version(3, 0, 0, 0))));

        Assert.Same(newer, older);
        Assert.Contains("Version=2.0.0.0 で要求 3.0.0.0 より古い", AssemblyResolutionDetail.Describe(error), StringComparison.Ordinal);
    }

    [Fact]
    public void SearchDirectoriesExcludeCecilRelativeDefaults()
    {
        using var temp = new TempDirectory();
        var root = temp.Path;
        var reference = Path.Combine(root, "reference");
        Directory.CreateDirectory(reference);
        using var resolver = CecilResolverFactory.Create(Path.Combine(root, "Target.dll"), [reference], [root]);

        var directories = resolver.GetSearchDirectories();

        // Input-derived directories come first; framework probe directories follow them.
        Assert.Equal([root, reference], directories.Take(2));
        Assert.DoesNotContain(".", directories);
        Assert.DoesNotContain("bin", directories);
        Assert.All(directories, directory => Assert.True(Path.IsPathRooted(directory), directory));
    }

    private static SearchResult Search(
        string input,
        string query,
        SearchKinds kinds,
        MatchMode match = MatchMode.Exact) =>
        new AssemblySearcher().Search(new SearchOptions(
            input, query, kinds, SearchScope.References, match, IgnoreCase: true, Recursive: true, SymbolMode.Off, 100, null, []));

    /// <summary>A caller of the multi-dimensional array pseudo-method int[,]::Get(int, int).</summary>
    private static void CreateArrayCallerAssembly(string path)
    {
        using var assembly = AssemblyDefinition.CreateAssembly(
            new AssemblyNameDefinition("Caller", new Version(1, 0, 0, 0)), "Caller", ModuleKind.Dll);
        var module = assembly.MainModule;
        var matrix = new ArrayType(module.TypeSystem.Int32, 2);
        var get = new MethodReference("Get", module.TypeSystem.Int32, matrix) { HasThis = true };
        get.Parameters.Add(new ParameterDefinition(module.TypeSystem.Int32));
        get.Parameters.Add(new ParameterDefinition(module.TypeSystem.Int32));

        var type = new TypeDefinition("Fixtures", "Caller", TypeAttributes.Public | TypeAttributes.Class);
        module.Types.Add(type);
        var method = new MethodDefinition("Call", MethodAttributes.Public | MethodAttributes.Static, module.TypeSystem.Int32);
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldnull));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4_0));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4_0));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Call, get));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        type.Methods.Add(method);
        assembly.Write(path);
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

    private static void CreateCallerAssembly(
        string path,
        string getterName,
        Version? modelVersion = null,
        bool handlerParameter = false,
        string? secondGetterName = null)
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
        var getter = new MethodReference(
            getterName,
            handlerParameter ? module.TypeSystem.Void : module.TypeSystem.Int32,
            modelType)
        {
            HasThis = true,
        };
        if (handlerParameter)
        {
            getter.Parameters.Add(new ParameterDefinition(module.ImportReference(typeof(EventHandler))));
        }

        var type = new TypeDefinition("Fixtures", "Caller", TypeAttributes.Public | TypeAttributes.Class);
        module.Types.Add(type);
        var method = new MethodDefinition(
            "Call",
            MethodAttributes.Public | MethodAttributes.Static,
            module.TypeSystem.Int32);
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldnull));
        if (handlerParameter)
        {
            method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldnull));
        }

        method.Body.Instructions.Add(Instruction.Create(OpCodes.Callvirt, getter));
        if (handlerParameter)
        {
            method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4_0));
        }

        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        type.Methods.Add(method);

        if (secondGetterName is not null)
        {
            var second = new MethodReference(secondGetterName, module.TypeSystem.Int32, modelType) { HasThis = true };
            var other = new MethodDefinition(
                "CallOther",
                MethodAttributes.Public | MethodAttributes.Static,
                module.TypeSystem.Int32);
            other.Body.Instructions.Add(Instruction.Create(OpCodes.Ldnull));
            other.Body.Instructions.Add(Instruction.Create(OpCodes.Callvirt, second));
            other.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
            type.Methods.Add(other);
        }

        assembly.Write(path);
    }
}
