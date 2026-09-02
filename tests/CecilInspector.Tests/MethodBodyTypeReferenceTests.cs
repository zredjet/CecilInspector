using CecilInspector.Cli;
using CecilInspector.Core;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace CecilInspector.Tests;

/// <summary>
/// Type references that live in a method body without being an instruction operand: the
/// exception-handler table and the locals signature. Built with Cecil so the shapes are exact.
/// </summary>
public sealed class MethodBodyTypeReferenceTests
{
    [Fact]
    public void CatchClauseTypeIsFoundAtTheHandlerStart()
    {
        using var temp = new TempDirectory();
        var path = temp.File("Bodies.dll");
        var handlerOffset = WriteFixture(path);

        var result = Search(path, "Fixtures.CustomException", SearchKinds.Type);

        var hit = Assert.Single(result.Hits);
        Assert.Equal(HitScope.Reference, hit.Scope);
        Assert.Equal(handlerOffset, hit.IlOffset);
        Assert.Contains("::CatchOnly(", hit.Container, StringComparison.Ordinal);
    }

    [Fact]
    public void LocalVariableTypeIsFoundAtTheMethodStart()
    {
        using var temp = new TempDirectory();
        var path = temp.File("Bodies.dll");
        WriteFixture(path);

        var result = Search(path, "Fixtures.OnlyALocal", SearchKinds.Type);

        var hit = Assert.Single(result.Hits);
        Assert.Equal(0, hit.IlOffset);
        Assert.Contains("::LocalOnly(", hit.Container, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Cache")]
    [InlineData("Fixtures.Cache")]
    [InlineData("Fixtures.Cache`1")]
    public void GenericTypeDefinitionMatchesWithAndWithoutArity(string query)
    {
        using var temp = new TempDirectory();
        var path = temp.File("Bodies.dll");
        WriteFixture(path);

        var result = Search(path, query, SearchKinds.Type, SearchScope.Definitions);

        var hit = Assert.Single(result.Hits);
        Assert.Equal("Fixtures.Cache`1", hit.Symbol);
    }

    [Theory]
    [InlineData("Fixtures.Cache", "Fixtures.Cache`1")]
    [InlineData("Fixtures.Cache`1", "Fixtures.Cache`1")]
    [InlineData("Fixtures.Cache<System.Int32>", "Fixtures.Cache`1<System.Int32>")]
    [InlineData("Fixtures.Cache`1<System.Int32>", "Fixtures.Cache`1<System.Int32>")]
    public void GenericTypeReferenceMatchesWithAndWithoutArity(string query, string expectedSymbol)
    {
        using var temp = new TempDirectory();
        var path = temp.File("Bodies.dll");
        WriteFixture(path);

        var result = Search(path, query, SearchKinds.Type);

        // A closed generic reference expands to the instance and its open definition, so the
        // arity-free name selects the definition and the argument list selects the instance.
        var hit = Assert.Single(result.Hits);
        Assert.Equal(expectedSymbol, hit.Symbol);
        Assert.Contains("::LocalOnly(", hit.Container, StringComparison.Ordinal);
    }

    [Fact]
    public void NamespaceOfACatchTypeIsAlsoReported()
    {
        using var temp = new TempDirectory();
        var path = temp.File("Bodies.dll");
        WriteFixture(path);

        var result = Search(path, "Fixtures", SearchKinds.Namespace);

        Assert.Contains(result.Hits, hit => hit.Container?.Contains("::CatchOnly(", StringComparison.Ordinal) == true);
    }

    private static SearchResult Search(
        string path,
        string query,
        SearchKinds kinds,
        SearchScope scope = SearchScope.References) =>
        new AssemblySearcher().Search(new SearchOptions(
            path, query, kinds, scope, MatchMode.Exact, true, true, SymbolMode.Off, 100, null, []));

    /// <returns>The IL offset of the catch handler's first instruction.</returns>
    private static int WriteFixture(string path)
    {
        using var assembly = AssemblyDefinition.CreateAssembly(
            new AssemblyNameDefinition("Bodies", new Version(1, 0)), "Bodies", ModuleKind.Dll);
        var module = assembly.MainModule;
        var objectType = module.TypeSystem.Object;

        var exception = new TypeDefinition("Fixtures", "CustomException", TypeAttributes.Public | TypeAttributes.Class, objectType);
        var local = new TypeDefinition("Fixtures", "OnlyALocal", TypeAttributes.Public | TypeAttributes.Class, objectType);
        var cache = new TypeDefinition("Fixtures", "Cache`1", TypeAttributes.Public | TypeAttributes.Class, objectType);
        cache.GenericParameters.Add(new GenericParameter("T", cache));
        var host = new TypeDefinition("Fixtures", "Host", TypeAttributes.Public | TypeAttributes.Class, objectType);
        module.Types.Add(exception);
        module.Types.Add(local);
        module.Types.Add(cache);
        module.Types.Add(host);

        // try { nop; leave END } catch (CustomException) { pop; leave END } END: ret
        var catchOnly = new MethodDefinition("CatchOnly", MethodAttributes.Public | MethodAttributes.Static, module.TypeSystem.Void);
        var il = catchOnly.Body.GetILProcessor();
        var end = Instruction.Create(OpCodes.Ret);
        var tryStart = Instruction.Create(OpCodes.Nop);
        var handlerStart = Instruction.Create(OpCodes.Pop);
        il.Append(tryStart);
        il.Append(Instruction.Create(OpCodes.Leave, end));
        il.Append(handlerStart);
        il.Append(Instruction.Create(OpCodes.Leave, end));
        il.Append(end);
        catchOnly.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch)
        {
            CatchType = exception,
            TryStart = tryStart,
            TryEnd = handlerStart,
            HandlerStart = handlerStart,
            HandlerEnd = end,
        });
        host.Methods.Add(catchOnly);

        // Locals of OnlyALocal and Cache<int>; the body never mentions either type in an operand.
        var localOnly = new MethodDefinition("LocalOnly", MethodAttributes.Public | MethodAttributes.Static, module.TypeSystem.Void);
        var closedCache = new GenericInstanceType(cache);
        closedCache.GenericArguments.Add(module.TypeSystem.Int32);
        localOnly.Body.Variables.Add(new VariableDefinition(local));
        localOnly.Body.Variables.Add(new VariableDefinition(closedCache));
        localOnly.Body.InitLocals = true;
        localOnly.Body.Instructions.Add(Instruction.Create(OpCodes.Ldnull));
        localOnly.Body.Instructions.Add(Instruction.Create(OpCodes.Stloc_0));
        localOnly.Body.Instructions.Add(Instruction.Create(OpCodes.Ldnull));
        localOnly.Body.Instructions.Add(Instruction.Create(OpCodes.Stloc_1));
        localOnly.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        host.Methods.Add(localOnly);

        assembly.Write(path);
        return handlerStart.Offset;
    }
}
