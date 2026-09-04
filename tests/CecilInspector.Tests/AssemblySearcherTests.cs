using CecilInspector.Cli;
using CecilInspector.Core;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace CecilInspector.Tests;

public sealed class AssemblySearcherTests
{
    private static readonly string ThisAssembly = typeof(AssemblySearcherTests).Assembly.Location;

    [Fact]
    public void FindsMethodDefinitionWithPdbLocation()
    {
        var result = Search("EstimateTarget", SearchKinds.Method, SearchScope.Definitions, MatchMode.Exact);

        var hit = Assert.Single(result.Hits, hit => hit.Symbol.Contains("::EstimateTarget(", StringComparison.Ordinal));
        Assert.Equal(HitScope.Definition, hit.Scope);
        Assert.NotNull(hit.Location);
        Assert.True(hit.Location.Line > 0);
    }

    [Fact]
    public void FindsPropertyDefinition()
    {
        var result = Search("EstimateProperty", SearchKinds.Property, SearchScope.Definitions, MatchMode.Exact);

        var hit = Assert.Single(result.Hits);
        Assert.Equal(HitKind.Property, hit.Kind);
    }

    [Fact]
    public void FindsPropertyReferenceByResolvedAccessorSemantics()
    {
        var result = Search("EstimateProperty", SearchKinds.Property, SearchScope.References, MatchMode.Exact);

        var hit = Assert.Single(result.Hits);
        Assert.Equal(HitKind.Property, hit.Kind);
        Assert.Contains("PropertyCaller", hit.Container, StringComparison.Ordinal);
    }

    [Fact]
    public void FindsMethodReferenceAndCallingSourceLine()
    {
        var result = Search("EstimateTarget", SearchKinds.Method, SearchScope.References, MatchMode.Exact);

        var hit = Assert.Single(result.Hits, candidate =>
            candidate.Container?.Contains("::CallTarget(", StringComparison.Ordinal) == true);
        Assert.Contains("::CallTarget(", hit.Container, StringComparison.Ordinal);
        Assert.NotNull(hit.Location);
        Assert.NotNull(hit.IlOffset);
    }

    [Fact]
    public void InvalidRegexIsReportedAsQueryError()
    {
        var options = Options("[", SearchKinds.All, SearchScope.Definitions, MatchMode.Regex);

        // SearchQueryException is an ArgumentException that Program maps to exit code 1, the
        // same as the CLI's own validation of the pattern.
        Assert.Throws<SearchQueryException>(() => new AssemblySearcher().Search(options));
    }

    [Fact]
    public void RegexTimeoutIsReportedAsSearchQueryError()
    {
        // The lookahead is unsupported by the non-backtracking engine, which forces the
        // timeout-guarded backtracking fallback that this test exercises. A 64-character
        // namespace makes the single match exceed 250 ms on any machine.
        using var temp = new TempDirectory();
        var assembly = temp.File("LongNamespace.dll");
        GeneratedAssemblies.WriteTypeInNamespace(assembly, new string('a', 64));
        var options = Options("^(?=.)(.+)+Z$", SearchKinds.Namespace, SearchScope.Definitions, MatchMode.Regex) with
        {
            InputPath = assembly,
            SymbolMode = SymbolMode.Off,
        };

        Assert.Throws<SearchQueryException>(() => new AssemblySearcher().Search(options));
    }

    [Fact]
    public void CatastrophicPatternCompletesUnderNonBacktrackingEngine()
    {
        var options = Options("^(.+)+Z$", SearchKinds.Namespace, SearchScope.Definitions, MatchMode.Regex) with
        {
            SymbolMode = SymbolMode.Off,
        };

        var result = new AssemblySearcher().Search(options);

        Assert.Equal(0, result.TotalMatches);
    }

    [Fact]
    public void AutoSymbolsFallsBackWhenPdbIsCorrupt()
    {
        using var temp = new TempDirectory();
        var directory = temp.Path;
        var assembly = Path.Combine(directory, "Target.dll");
        var pdb = Path.Combine(directory, "Target.pdb");
        File.Copy(ThisAssembly, assembly);
        File.WriteAllBytes(pdb, "BSJB"u8.ToArray());
        var options = Options("EstimateTarget", SearchKinds.Method, SearchScope.Definitions, MatchMode.Exact) with
        {
            InputPath = assembly,
            SymbolMode = SymbolMode.Auto,
        };

        var result = new AssemblySearcher().Search(options);

        Assert.Single(result.Hits);
        Assert.Equal(1, result.FilesSucceeded);
        Assert.Equal(0, result.FilesWithSymbols);
        Assert.Empty(result.Errors);
        var warning = Assert.Single(result.Warnings);
        Assert.Equal(assembly, warning.FilePath);
        Assert.Contains("シンボルなしで解析", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RequiredSymbolsReportsMismatchedPdb()
    {
        using var temp = new TempDirectory();
        var directory = temp.Path;
        var assembly = Path.Combine(directory, "Target.dll");
        var pdb = Path.Combine(directory, "Target.pdb");
        File.Copy(ThisAssembly, assembly);
        var otherAssembly = typeof(AssemblySearcher).Assembly.Location;
        File.Copy(Path.ChangeExtension(otherAssembly, ".pdb"), pdb);
        var options = Options("EstimateTarget", SearchKinds.Method, SearchScope.Definitions, MatchMode.Exact) with
        {
            InputPath = assembly,
            SymbolMode = SymbolMode.Required,
        };

        var result = new AssemblySearcher().Search(options);

        Assert.Equal(0, result.FilesSucceeded);
        var error = Assert.Single(result.Errors);
        Assert.Contains("matching", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AutoSymbolsSkipsPdbWhenSelectedDefinitionsHaveNoLocations()
    {
        var options = Options("SearchFixture", SearchKinds.Type, SearchScope.Definitions, MatchMode.Exact) with
        {
            SymbolMode = SymbolMode.Auto,
        };

        var result = new AssemblySearcher().Search(options);

        Assert.Single(result.Hits);
        Assert.Equal(0, result.FilesWithSymbols);
    }

    [Fact]
    public void FindsAllOverloadsForDeclaringTypeAndMethodName()
    {
        var result = Search(
            "CecilInspector.Tests.SearchFixture::Save",
            SearchKinds.Method,
            SearchScope.Definitions,
            MatchMode.Exact);

        Assert.Equal(2, result.TotalMatches);
        Assert.Contains(result.Hits, hit => hit.Symbol.Contains("Save(System.String)", StringComparison.Ordinal));
        Assert.Contains(result.Hits, hit => hit.Symbol.Contains("Save(System.Int32)", StringComparison.Ordinal));
    }

    [Fact]
    public void ExactQualifiedPropertyNameFindsProperty()
    {
        var result = Search(
            "CecilInspector.Tests.SearchFixture::EstimateProperty",
            SearchKinds.Property,
            SearchScope.Definitions,
            MatchMode.Exact);

        Assert.Single(result.Hits);
    }

    [Fact]
    public void ExactSearchAcceptsPreviouslyDisplayedSignature()
    {
        var initial = Search("EstimateTarget", SearchKinds.Method, SearchScope.Definitions, MatchMode.Exact);
        var signature = Assert.Single(initial.Hits).Symbol;

        var repeated = Search(signature, SearchKinds.Method, SearchScope.Definitions, MatchMode.Exact);

        Assert.Single(repeated.Hits);
    }

    [Fact]
    public void GenericMethodAritiesAreDistinct()
    {
        var result = Search("GenericArity", SearchKinds.Method, SearchScope.Definitions, MatchMode.Exact);

        Assert.Equal(2, result.TotalMatches);
        Assert.Contains(result.Hits, hit => hit.Symbol.Contains("GenericArity`1", StringComparison.Ordinal));
        Assert.Contains(result.Hits, hit => hit.Symbol.Contains("GenericArity`2", StringComparison.Ordinal));
    }

    [Fact]
    public void IndexerOverloadsIncludeIndexParameterTypes()
    {
        var result = Search("Item", SearchKinds.Property, SearchScope.Definitions, MatchMode.Exact);

        Assert.Contains(result.Hits, hit => hit.Symbol.Contains("Item(System.Int32)", StringComparison.Ordinal));
        Assert.Contains(result.Hits, hit => hit.Symbol.Contains("Item(System.String)", StringComparison.Ordinal));
    }

    [Fact]
    public void OrdinaryGetPrefixedMethodRemainsAMethodReference()
    {
        var result = Search("get_ManualValue", SearchKinds.Method, SearchScope.References, MatchMode.Exact);

        var hit = Assert.Single(result.Hits);
        Assert.Equal(HitKind.Method, hit.Kind);
    }

    [Fact]
    public void OrdinaryGetPrefixedMethodDoesNotMatchSyntheticLogicalName()
    {
        var result = Search("ManualValue", SearchKinds.Method, SearchScope.All, MatchMode.Exact);

        Assert.Empty(result.Hits);
    }

    [Fact]
    public void PropertyAccessorIsNotReportedAsMethodReference()
    {
        var result = Search("EstimateProperty", SearchKinds.Method, SearchScope.References, MatchMode.Exact);

        Assert.Empty(result.Hits);
    }

    [Fact]
    public void FindsTypeUsedAsGenericMethodArgument()
    {
        var result = Search("OnlyReferencedType", SearchKinds.Type, SearchScope.References, MatchMode.Exact);

        Assert.NotEmpty(result.Hits);
        Assert.Contains(result.Hits, hit => hit.Container?.Contains("GenericCaller", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void GenericMethodReferencesPreserveClosedTypeArguments()
    {
        var result = Search("GenericSink", SearchKinds.Method, SearchScope.References, MatchMode.Exact);

        Assert.Equal(2, result.TotalMatches);
        Assert.Contains(result.Hits, hit =>
            hit.Symbol.Contains("GenericSink`1<CecilInspector.Tests.OnlyReferencedType>", StringComparison.Ordinal));
        Assert.Contains(result.Hits, hit =>
            hit.Symbol.Contains("GenericSink`1<CecilInspector.Tests.SecondReferencedType>", StringComparison.Ordinal));
        Assert.Equal(2, result.Hits.Select(hit => hit.Symbol).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void GenericDeclaringTypeReferencesPreserveClosedTypeArguments()
    {
        var result = Search("Store", SearchKinds.Method, SearchScope.References, MatchMode.Exact);

        Assert.Equal(2, result.TotalMatches);
        Assert.Contains(result.Hits, hit =>
            hit.Symbol.Contains("GenericContainer`1<CecilInspector.Tests.OnlyReferencedType>::Store", StringComparison.Ordinal));
        Assert.Contains(result.Hits, hit =>
            hit.Symbol.Contains("GenericContainer`1<CecilInspector.Tests.SecondReferencedType>::Store", StringComparison.Ordinal));
        Assert.Equal(2, result.Hits.Select(hit => hit.Symbol).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void ClosedGenericTypeSpecificationIsSearchableAsATypeReference()
    {
        var result = Search(
            "CecilInspector.Tests.GenericContainer`1<CecilInspector.Tests.OnlyReferencedType>",
            SearchKinds.Type,
            SearchScope.References,
            MatchMode.Exact);

        Assert.Contains(result.Hits, hit => hit.Container?.Contains("ClosedGenericCaller", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void CallSiteTypesWithSameFullNameButDifferentScopesRemainDistinct()
    {
        using var temp = new TempDirectory();
        var directory = temp.Path;
        var assemblyPath = Path.Combine(directory, "CallSite.dll");
        using (var assembly = AssemblyDefinition.CreateAssembly(
                   new AssemblyNameDefinition("CallSite", new Version(1, 0)),
                   "CallSite",
                   ModuleKind.Dll))
        {
            var module = assembly.MainModule;
            var firstScope = new AssemblyNameReference("First.Contracts", new Version(1, 0));
            var secondScope = new AssemblyNameReference("Second.Contracts", new Version(1, 0));
            module.AssemblyReferences.Add(firstScope);
            module.AssemblyReferences.Add(secondScope);
            var first = new TypeReference("Shared", "Model", module, firstScope);
            var second = new TypeReference("Shared", "Model", module, secondScope);
            var callSite = new Mono.Cecil.CallSite(module.TypeSystem.Void);
            callSite.Parameters.Add(new ParameterDefinition(first));
            callSite.Parameters.Add(new ParameterDefinition(second));

            var type = new TypeDefinition("Fixtures", "Caller", TypeAttributes.Public | TypeAttributes.Class);
            module.Types.Add(type);
            var method = new MethodDefinition(
                "Call",
                MethodAttributes.Public | MethodAttributes.Static,
                module.TypeSystem.Void);
            method.Body.Instructions.Add(Instruction.Create(OpCodes.Calli, callSite));
            method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
            type.Methods.Add(method);
            assembly.Write(assemblyPath);
        }

        var result = new AssemblySearcher().Search(Options(
            "Shared.Model",
            SearchKinds.Type,
            SearchScope.References,
            MatchMode.Exact) with
        {
            InputPath = assemblyPath,
            SymbolMode = SymbolMode.Off,
        });

        Assert.Equal(2, result.TotalMatches);
        Assert.Equal(2, result.Hits.Select(hit => hit.Symbol).Distinct(StringComparer.Ordinal).Count());
        Assert.Contains(result.Hits, hit => hit.Symbol.Contains("@First.Contracts", StringComparison.Ordinal));
        Assert.Contains(result.Hits, hit => hit.Symbol.Contains("@Second.Contracts", StringComparison.Ordinal));
    }

    [Fact]
    public void ContextualFormattingSubstitutesGenericParametersInsideComplexTypeSpecifications()
    {
        using var temp = new TempDirectory();
        var directory = temp.Path;
        var assemblyPath = Path.Combine(directory, "ComplexTypes.dll");
        using (var assembly = AssemblyDefinition.CreateAssembly(
                   new AssemblyNameDefinition("ComplexTypes", new Version(1, 0)),
                   "ComplexTypes",
                   ModuleKind.Dll))
        {
            var module = assembly.MainModule;
            var contracts = new AssemblyNameReference("Contracts", new Version(1, 0));
            module.AssemblyReferences.Add(contracts);
            var openTarget = new TypeReference("Contracts", "GenericTarget`1", module, contracts);
            var genericParameter = new GenericParameter("T", openTarget);
            openTarget.GenericParameters.Add(genericParameter);
            var closedArgument = new TypeReference("Fixtures", "ClosedArgument", module, module);
            var closedTarget = new GenericInstanceType(openTarget);
            closedTarget.GenericArguments.Add(closedArgument);
            var marker = new TypeReference("Contracts", "Marker", module, contracts);

            var functionPointer = new FunctionPointerType
            {
                ReturnType = genericParameter,
            };
            functionPointer.Parameters.Add(new ParameterDefinition(new ArrayType(genericParameter, 2)));
            var target = new MethodReference("Consume", module.TypeSystem.Void, closedTarget);
            target.Parameters.Add(new ParameterDefinition(new RequiredModifierType(marker, genericParameter)));
            target.Parameters.Add(new ParameterDefinition(functionPointer));

            var type = new TypeDefinition("Fixtures", "Caller", TypeAttributes.Public | TypeAttributes.Class);
            module.Types.Add(type);
            var method = new MethodDefinition(
                "Call",
                MethodAttributes.Public | MethodAttributes.Static,
                module.TypeSystem.Void);
            method.Body.Instructions.Add(Instruction.Create(OpCodes.Call, target));
            method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
            type.Methods.Add(method);
            assembly.Write(assemblyPath);
        }

        var result = new AssemblySearcher().Search(Options(
            "Consume",
            SearchKinds.Method,
            SearchScope.References,
            MatchMode.Exact) with
        {
            InputPath = assemblyPath,
            SymbolMode = SymbolMode.Off,
        });

        var symbol = Assert.Single(result.Hits).Symbol;
        Assert.Contains("Fixtures.ClosedArgument modreq(Contracts.Marker)", symbol, StringComparison.Ordinal);
        Assert.Contains("method Fixtures.ClosedArgument *(Fixtures.ClosedArgument[,])", symbol, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("LogicalMethod", SearchKinds.Method, HitKind.Method)]
    [InlineData("LogicalProperty", SearchKinds.Property, HitKind.Property)]
    [InlineData("LogicalEvent", SearchKinds.Event, HitKind.Event)]
    public void ExplicitInterfaceDefinitionsMatchLogicalMemberName(
        string query,
        SearchKinds kind,
        HitKind expectedKind)
    {
        var result = Search(query, kind, SearchScope.Definitions, MatchMode.Exact);

        Assert.Equal(2, result.TotalMatches);
        var hit = Assert.Single(result.Hits, candidate =>
            candidate.Symbol.Contains(nameof(ExplicitSearchImplementation), StringComparison.Ordinal));
        Assert.Equal(expectedKind, hit.Kind);
        Assert.Contains($"{nameof(IExplicitSearchContract)}.{query}", hit.Symbol, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("LogicalMethod", SearchKinds.Method, HitKind.Method, 1)]
    [InlineData("LogicalProperty", SearchKinds.Property, HitKind.Property, 1)]
    [InlineData("LogicalEvent", SearchKinds.Event, HitKind.Event, 2)]
    public void InterfaceReferencesMatchLogicalMemberName(
        string query,
        SearchKinds kind,
        HitKind expectedKind,
        int expectedCount)
    {
        var result = Search(query, kind, SearchScope.References, MatchMode.Exact);

        Assert.Equal(expectedCount, result.TotalMatches);
        Assert.All(result.Hits, hit => Assert.Equal(expectedKind, hit.Kind));
        Assert.All(result.Hits, hit => Assert.Contains("CallExplicitMembers", hit.Container, StringComparison.Ordinal));
    }

    [Fact]
    public void AsyncDefinitionUsesStateMachineSourceLocation()
    {
        var result = Search("AsyncTarget", SearchKinds.Method, SearchScope.Definitions, MatchMode.Exact);

        Assert.NotNull(Assert.Single(result.Hits).Location);
    }

    [Fact]
    public void AsyncReferencesUseKickoffMethodAsContainer()
    {
        var result = Search("EstimateTarget", SearchKinds.Method, SearchScope.References, MatchMode.Exact);

        Assert.Contains(result.Hits, hit => hit.Container?.Contains("AsyncTarget", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(result.Hits, hit => hit.Container?.Contains("<AsyncTarget>d__", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void MaxResultsLimitsRetainedHitsButPreservesTotalCount()
    {
        var options = Options("Search", SearchKinds.Method, SearchScope.Definitions, MatchMode.Contains) with
        {
            MaxResults = 1,
        };

        var result = new AssemblySearcher().Search(options);

        Assert.Single(result.Hits);
        Assert.True(result.TotalMatches > result.Hits.Count);
        Assert.Equal(result.TotalMatches, result.Counts.Sum(count => count.Count));
    }

    [Fact]
    public void SelfReferencingGenericSwapReferenceTerminates()
    {
        var result = Search("Mirror", SearchKinds.Method, SearchScope.References, MatchMode.Exact);

        var hit = Assert.Single(result.Hits);
        Assert.Equal(
            "CecilInspector.Tests.Swap`2<!1, !0>::Mirror(CecilInspector.Tests.Swap`2<!0, !1>) : System.Void",
            hit.Symbol);
    }

    [Fact]
    public void SwappedGenericMethodArgumentsRenderPositionally()
    {
        var result = Search("SwapCallee", SearchKinds.Method, SearchScope.References, MatchMode.Exact);

        var hit = Assert.Single(result.Hits);
        Assert.Equal(
            "CecilInspector.Tests.GenericSwapFixture::SwapCallee`2<!!1, !!0>() : System.Void",
            hit.Symbol);
    }

    [Fact]
    public void MultiArgumentGenericSignaturesAgreeAcrossScopes()
    {
        const string expected =
            "CecilInspector.Tests.GenericSignatureFixture::TwoArgs() : System.Func`2<System.Int32, System.String>";

        var result = Search("TwoArgs", SearchKinds.Method, SearchScope.All, MatchMode.Exact);

        Assert.Equal(2, result.Hits.Count);
        Assert.All(result.Hits, hit => Assert.Equal(expected, hit.Symbol));
        Assert.Contains(result.Hits, hit => hit.Scope == HitScope.Definition);
        Assert.Contains(result.Hits, hit => hit.Scope == HitScope.Reference);

        var roundTrip = Search(expected, SearchKinds.Method, SearchScope.All, MatchMode.Exact);

        Assert.Equal(2, roundTrip.Hits.Count);
    }

    [Fact]
    public void GenericParameterInsideInstanceRendersPositional()
    {
        var definitions = Search("Wrap", SearchKinds.Method, SearchScope.Definitions, MatchMode.Exact);
        var references = Search("Wrap", SearchKinds.Method, SearchScope.References, MatchMode.Exact);

        var definition = Assert.Single(definitions.Hits);
        Assert.Equal(
            "CecilInspector.Tests.GenericSignatureFixture::Wrap`1(System.Collections.Generic.Dictionary`2<System.String, !!0>) : " +
            "System.Collections.Generic.List`1<!!0>",
            definition.Symbol);

        var reference = Assert.Single(references.Hits);
        Assert.Equal(
            "CecilInspector.Tests.GenericSignatureFixture::Wrap`1<System.Int32>(System.Collections.Generic.Dictionary`2<System.String, System.Int32>) : " +
            "System.Collections.Generic.List`1<System.Int32>",
            reference.Symbol);
    }

    [Theory]
    [InlineData(0.30)]
    [InlineData(0.60)]
    [InlineData(0.90)]
    [InlineData(0.97)]
    public void TruncatedImageIsReportedAsScanErrorNotCrash(double keepRatio)
    {
        using var temp = new TempDirectory();
        var directory = temp.Path;
        var bytes = File.ReadAllBytes(ThisAssembly);
        var truncated = Path.Combine(directory, "Truncated.dll");
        File.WriteAllBytes(truncated, bytes[..(int)(bytes.Length * keepRatio)]);
        var options = Options("a", SearchKinds.All, SearchScope.All, MatchMode.Contains) with
        {
            InputPath = truncated,
            SymbolMode = SymbolMode.Off,
        };

        var result = new AssemblySearcher().Search(options);

        // Up to 97% of the image still loses metadata tables, so the file must be reported as
        // an error (and never crash); only the last percent or so parses with partial hits.
        Assert.Equal(0, result.FilesSucceeded);
        // Cecil's BadImageFormatException carries an empty message; the reason is in the inner
        // exception and must reach the warning line.
        Assert.False(string.IsNullOrWhiteSpace(Assert.Single(result.Errors).Message));
        Assert.Equal(0, result.TotalMatches);
    }

    [Fact]
    public void NonRecursiveSearchIgnoresSubdirectories()
    {
        using var temp = new TempDirectory();
        temp.CopyAssembly("root.dll");
        File.Copy(ThisAssembly, Path.Combine(temp.CreateSubdirectory("nested"), "nested.dll"));
        var options = Options("EstimateTarget", SearchKinds.Method, SearchScope.Definitions, MatchMode.Exact) with
        {
            InputPath = temp.Path,
            Recursive = false,
            SymbolMode = SymbolMode.Off,
        };

        var result = new AssemblySearcher().Search(options);

        Assert.Equal(1, result.FilesDiscovered);
        Assert.Equal(temp.File("root.dll"), Assert.Single(result.Hits).AssemblyPath);
    }

    [Fact]
    public void CaseSensitiveSearchRequiresMatchingCase()
    {
        var insensitive = Search("estimatetarget", SearchKinds.Method, SearchScope.Definitions, MatchMode.Exact);
        var sensitive = new AssemblySearcher().Search(
            Options("estimatetarget", SearchKinds.Method, SearchScope.Definitions, MatchMode.Exact) with
            {
                IgnoreCase = false,
            });

        Assert.Single(insensitive.Hits);
        Assert.Empty(sensitive.Hits);
    }

    [Fact]
    public void FindsFieldDefinitionAndReferences()
    {
        var result = Search("EstimateCounter", SearchKinds.Field, SearchScope.All, MatchMode.Exact);

        Assert.All(result.Hits, hit => Assert.Equal(HitKind.Field, hit.Kind));
        Assert.All(result.Hits, hit =>
            Assert.Equal("CecilInspector.Tests.FieldFixture::EstimateCounter : System.Int32", hit.Symbol));
        Assert.Single(result.Hits, hit => hit.Scope == HitScope.Definition);
        Assert.Equal(2, result.Hits.Count(hit => hit.Scope == HitScope.Reference));
        Assert.All(result.Hits.Where(hit => hit.Scope == HitScope.Reference), hit =>
            Assert.Contains("::Bump(", hit.Container, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("EstimateTarget", MatchMode.Contains)]
    [InlineData("SearchFixture::EstimateTarget", MatchMode.Contains)]
    [InlineData("CecilInspector.Tests.SearchFixture::EstimateTarget", MatchMode.Exact)]
    [InlineData("EstimateTarget(System.Int32)", MatchMode.Contains)]
    [InlineData("CecilInspector.Tests.SearchFixture::EstimateTarget(System.Int32) : System.Int32", MatchMode.Exact)]
    [InlineData("EstimateTarget\\(System", MatchMode.Regex)]
    public void MethodDefinitionsMatchThroughNamesAndThroughTheFormattedSymbol(string query, MatchMode mode)
    {
        var result = Search(query, SearchKinds.Method, SearchScope.Definitions, mode);

        Assert.Single(result.Hits, hit => hit.Symbol == "CecilInspector.Tests.SearchFixture::EstimateTarget(System.Int32) : System.Int32");
    }

    [Theory]
    [InlineData("GenericArity`1", "GenericArity`1()")]
    [InlineData("GenericArity`2", "GenericArity`2()")]
    public void ArityQueriesStillReachTheFormattedSymbol(string query, string expectedFragment)
    {
        var result = Search(query, SearchKinds.Method, SearchScope.Definitions, MatchMode.Contains);

        Assert.Single(result.Hits, hit => hit.Symbol.Contains(expectedFragment, StringComparison.Ordinal));
    }

    [Fact]
    public void MaxResultsAppliesAcrossFilesWhileTotalsCountEverything()
    {
        using var temp = new TempDirectory();
        temp.CopyAssembly("First.dll");
        temp.CopyAssembly("Second.dll");
        var single = Search("Estimate", SearchKinds.Method, SearchScope.Definitions, MatchMode.Contains);
        var options = Options("Estimate", SearchKinds.Method, SearchScope.Definitions, MatchMode.Contains) with
        {
            InputPath = temp.Path,
            SymbolMode = SymbolMode.Off,
            MaxResults = single.TotalMatches + 1,
        };

        var result = new AssemblySearcher().Search(options);

        // Both files match; the retained hits stop one into the second file, the totals do not.
        Assert.Equal(2, result.FilesSucceeded);
        Assert.Equal(single.TotalMatches * 2, result.TotalMatches);
        Assert.Equal(single.TotalMatches + 1, result.Hits.Count);
        Assert.Equal(single.TotalMatches, result.Hits.Count(hit => hit.AssemblyPath == temp.File("First.dll")));
        Assert.Single(result.Hits, hit => hit.AssemblyPath == temp.File("Second.dll"));
    }

    [Fact]
    public void NamespaceDefinitionIsReportedOncePerModule()
    {
        var result = Search("CecilInspector.Tests", SearchKinds.Namespace, SearchScope.Definitions, MatchMode.Exact);

        var hit = Assert.Single(result.Hits);
        Assert.Equal(HitKind.Namespace, hit.Kind);
        Assert.Equal("CecilInspector.Tests", hit.Symbol);
        Assert.Null(hit.Location);
    }

    [Fact]
    public void RequiredSymbolsReportsMissingPdb()
    {
        using var temp = new TempDirectory();
        var assembly = temp.File("Target.dll");
        File.Copy(ThisAssembly, assembly);
        var options = Options("EstimateTarget", SearchKinds.Method, SearchScope.Definitions, MatchMode.Exact) with
        {
            InputPath = assembly,
            SymbolMode = SymbolMode.Required,
        };

        var result = new AssemblySearcher().Search(options);

        Assert.Equal(0, result.FilesSucceeded);
        var error = Assert.Single(result.Errors);
        Assert.Contains("PDBが見つかりません", error.Message, StringComparison.Ordinal);
    }

    private static SearchResult Search(string query, SearchKinds kinds, SearchScope scope, MatchMode match) =>
        new AssemblySearcher().Search(Options(query, kinds, scope, match));

    private static SearchOptions Options(string query, SearchKinds kinds, SearchScope scope, MatchMode match) =>
        new(ThisAssembly, query, kinds, scope, match, IgnoreCase: true, Recursive: true, SymbolMode.Required, 1000, null, []);
}

public sealed class SearchFixture
{
    public string EstimateProperty { get; set; } = string.Empty;

    public static int EstimateTarget(int value) => value + 1;

    public int CallTarget() => EstimateTarget(41);

    public int PropertyCaller() => EstimateProperty.Length;

    public void Save(string value) => _ = value;

    public void Save(int value) => _ = value;

    public void GenericArity<T>()
    {
    }

    public void GenericArity<T1, T2>()
    {
    }

    public string this[int index] => index.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public string this[string index] => index;

    public int get_ManualValue() => 42;

    public int CallManualGetter() => get_ManualValue();

    public void GenericCaller()
    {
        GenericSink<OnlyReferencedType>();
        GenericSink<SecondReferencedType>();
    }

    public void ClosedGenericCaller(
        GenericContainer<OnlyReferencedType> first,
        GenericContainer<SecondReferencedType> second)
    {
        first.Store(new OnlyReferencedType());
        second.Store(new SecondReferencedType());
    }

    public async Task<int> AsyncTarget()
    {
        await Task.Yield();
        return EstimateTarget(1);
    }

    private static void GenericSink<T>()
    {
    }
}

public sealed class OnlyReferencedType;

public sealed class SecondReferencedType;

public sealed class GenericContainer<T>
{
    public void Store(T value) => _ = value;
}

public interface IExplicitSearchContract
{
    void LogicalMethod();

    string LogicalProperty { get; }

    event EventHandler LogicalEvent;
}

public sealed class ExplicitSearchImplementation : IExplicitSearchContract
{
    void IExplicitSearchContract.LogicalMethod()
    {
    }

    string IExplicitSearchContract.LogicalProperty => string.Empty;

    event EventHandler IExplicitSearchContract.LogicalEvent
    {
        add { }
        remove { }
    }

    public static int CallExplicitMembers(IExplicitSearchContract target, EventHandler handler)
    {
        target.LogicalMethod();
        var length = target.LogicalProperty.Length;
        target.LogicalEvent += handler;
        target.LogicalEvent -= handler;
        return length;
    }
}

public sealed class Swap<A, B>
{
    public void Mirror(Swap<B, A> other) => other.Mirror(this);
}

public sealed class GenericSwapFixture
{
    public void SwapCaller<A, B>() => SwapCallee<B, A>();

    public void SwapCallee<X, Y>()
    {
    }
}

public sealed class GenericSignatureFixture
{
    public Func<int, string> TwoArgs() => null!;

    public List<T> Wrap<T>(Dictionary<string, T> input) => [];

    public void Invoke(GenericSignatureFixture fixture)
    {
        fixture.TwoArgs();
        fixture.Wrap<int>(null!);
    }
}

public static class FieldFixture
{
    public static int EstimateCounter;

    public static int Bump() => EstimateCounter = EstimateCounter + 1;
}
