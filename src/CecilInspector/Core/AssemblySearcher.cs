using CecilInspector.Cli;
using Mono.Cecil;
using Mono.Cecil.Cil;
using System.Runtime.CompilerServices;

namespace CecilInspector.Core;

public sealed class AssemblySearcher
{
    private static readonly ConditionalWeakTable<TypeDefinition, AccessorMap> AccessorMaps = new();

    public SearchResult Search(SearchOptions options) =>
        Search(options, AssemblyFiles.DiscoverDetailed(options.InputPath, options.Recursive));

    /// <summary>
    /// Searches an already discovered input. Callers that need to validate every option before
    /// any long-running work (the CLI) discover and validate up front, then pass the result here.
    /// </summary>
    public SearchResult Search(
        SearchOptions options,
        AssemblyDiscoveryResult discovery,
        CancellationToken cancellationToken = default)
    {
        var referenceDirectories = CecilResolverFactory.ValidateReferencePaths(options.ReferencePaths);
        var files = discovery.Files;
        var matcher = new SearchMatcher(options);
        var hits = new SearchHitCollector(options.MaxResults);
        var errors = new List<ScanError>(discovery.Errors);
        var warnings = new List<ScanError>();
        var succeeded = 0;
        var withSymbols = 0;
        var symbolMode = EffectiveSymbolMode(options);
        using var frameworkResolver = CecilResolverFactory.CreateFrameworkResolver();
        var resolutions = new MemberResolutionCache(frameworkResolver);

        // Files are independent, so they are scanned in parallel and their outcomes merged in
        // input order, which keeps the report identical to a sequential run. Each file stages
        // its own hits, diagnostics and resolution failures; only the framework resolver is
        // shared (it locks), so memory grows with the degree of parallelism, not the file count.
        var outcomes = new FileOutcome[files.Count];
        var retained = new int[files.Count];
        Array.Fill(retained, -1);

        FileOutcome Scan(int index)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var outcome = ScanFile(
                files[index],
                RemainingCapacity(options.MaxResults, retained, index),
                options,
                matcher,
                symbolMode,
                referenceDirectories,
                discovery,
                frameworkResolver,
                resolutions,
                cancellationToken);
            Volatile.Write(ref retained[index], outcome.Hits.Hits.Count);
            return outcome;
        }

        var parallelism = EffectiveParallelism(options, files.Count);
        if (parallelism <= 1)
        {
            for (var index = 0; index < files.Count; index++)
            {
                outcomes[index] = Scan(index);
            }
        }
        else
        {
            try
            {
                Parallel.For(
                    0,
                    files.Count,
                    new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = cancellationToken },
                    index => outcomes[index] = Scan(index));
            }
            catch (AggregateException ex) when (ex.InnerExceptions.Count == 1)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerExceptions[0]).Throw();
            }
        }

        foreach (var outcome in outcomes)
        {
            hits.Merge(outcome.Hits);
            errors.AddRange(outcome.Errors);
            warnings.AddRange(outcome.Warnings);
            if (outcome.Succeeded)
            {
                succeeded++;
            }

            if (outcome.HasSymbols)
            {
                withSymbols++;
            }
        }

        return new SearchResult(
            hits.Hits,
            hits.TotalMatches,
            hits.Counts,
            errors,
            discovery.FileCount,
            succeeded,
            withSymbols,
            warnings);
    }

    /// <summary>
    /// The degree of parallelism: the explicit option, otherwise the processor count capped at
    /// 8 so peak memory (one image copy plus Cecil's objects per concurrent file) stays modest
    /// on large machines; never more than there are files.
    /// </summary>
    internal static int EffectiveParallelism(SearchOptions options, int fileCount)
    {
        var requested = options.Parallelism > 0 ? options.Parallelism : Math.Min(Environment.ProcessorCount, 8);
        return Math.Max(1, Math.Min(requested, fileCount));
    }

    /// <summary>
    /// How many hits a file's stage still needs to keep so the report can show them: the limit
    /// minus what earlier files retained. Files that are still running count as zero, which can
    /// only over-estimate (their hits are merged first and drop the excess), never lose a hit.
    /// </summary>
    private static int RemainingCapacity(int maxResults, int[] retained, int index)
    {
        var used = 0;
        for (var earlier = 0; earlier < index; earlier++)
        {
            var count = Volatile.Read(ref retained[earlier]);
            if (count > 0)
            {
                used += count;
            }
        }

        return Math.Max(0, maxResults - used);
    }

    private static FileOutcome ScanFile(
        string file,
        int capacity,
        SearchOptions options,
        SearchMatcher matcher,
        SymbolMode symbolMode,
        IReadOnlyList<string> referenceDirectories,
        AssemblyDiscoveryResult discovery,
        IdentityAwareAssemblyResolver frameworkResolver,
        MemberResolutionCache resolutions,
        CancellationToken cancellationToken)
    {
        // Hits are staged per file and merged whether or not the file completes: a failure
        // part-way through still reports what was found before it (the file is listed as an
        // error and the exit code says the result is incomplete). The stage only needs the
        // capacity the report can still show, so files past the --max-results limit do not
        // materialize containers and locations that would be dropped on merge.
        var fileHits = new SearchHitCollector(capacity);
        var errors = new List<ScanError>();
        var warnings = new List<ScanError>();
        var resolutionDiagnostics = new ResolutionDiagnostics(errors);
        var succeeded = false;
        var hasSymbols = false;
        using var resolver = CecilResolverFactory.Create(
            file, referenceDirectories, discovery.SearchDirectories, frameworkResolver);
        try
        {
            using (var module = CecilModuleReader.Read(file, symbolMode, resolver, out var symbolWarning))
            {
                if (symbolWarning is not null)
                {
                    warnings.Add(new ScanError(file, symbolWarning));
                }

                SearchModule(module, file, options, matcher, fileHits, resolutionDiagnostics, resolutions, cancellationToken);
                if (discovery.InputIsFile)
                {
                    SecondaryModules.ForEach(
                        module,
                        file,
                        (secondary, moduleFile) => SearchModule(
                            secondary, moduleFile, options, matcher, fileHits, resolutionDiagnostics, resolutions, cancellationToken),
                        errors.Add,
                        cancellationToken);
                }

                hasSymbols = module.HasSymbols;
            }

            succeeded = true;
        }
        catch (Exception ex) when (ExceptionPolicy.IsRecoverableAssemblyError(ex))
        {
            errors.Add(new ScanError(file, ex.Message, ex));
        }
        finally
        {
            resolutionDiagnostics.Flush();
        }

        return new FileOutcome(fileHits, errors, warnings, succeeded, hasSymbols);
    }

    private sealed record FileOutcome(
        SearchHitCollector Hits,
        List<ScanError> Errors,
        List<ScanError> Warnings,
        bool Succeeded,
        bool HasSymbols);

    private static void SearchModule(
        ModuleDefinition module,
        string file,
        SearchOptions options,
        SearchMatcher matcher,
        SearchHitCollector hits,
        ResolutionDiagnostics resolutionDiagnostics,
        MemberResolutionCache resolutions,
        CancellationToken cancellationToken)
    {
        var assemblyName = module.Assembly?.Name.FullName ?? module.Name;
        var seenNamespaces = new HashSet<string>(StringComparer.Ordinal);
        var scratch = new ReferenceScratch(file, options, resolutionDiagnostics, resolutions);
        foreach (var type in AllTypes(module.Types))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (options.Scope is SearchScope.Definitions or SearchScope.All)
            {
                SearchDefinitions(type, file, assemblyName, options, matcher, hits, seenNamespaces);
            }

            if (options.Scope is SearchScope.References or SearchScope.All)
            {
                SearchReferences(type, file, assemblyName, options, matcher, hits, scratch);
            }
        }
    }

    private static void SearchDefinitions(
        TypeDefinition type,
        string file,
        string assemblyName,
        SearchOptions options,
        SearchMatcher matcher,
        SearchHitCollector hits,
        HashSet<string> seenNamespaces)
    {
        if (options.Kinds.Includes(HitKind.Namespace) &&
            !string.IsNullOrEmpty(type.Namespace) &&
            seenNamespaces.Add(type.Namespace) &&
            matcher.IsMatch(type.Namespace))
        {
            AddDefinition(hits, file, assemblyName, HitKind.Namespace, type.Namespace, null);
        }

        // The declaring type renders identically for every member, so format it once per type.
        var declaringType = CecilFormatting.Type(type);

        if (options.Kinds.Includes(HitKind.Type) &&
            matcher.IsMatch(
                type.Name,
                type.FullName,
                declaringType,
                CecilFormatting.WithoutArity(type.Name),
                CecilFormatting.WithoutArity(declaringType)))
        {
            AddDefinition(hits, file, assemblyName, HitKind.Type, declaringType, null);
        }

        if (options.Kinds.Includes(HitKind.Field))
        {
            foreach (var field in type.Fields)
            {
                if (matcher.IsMemberMatch(
                        field.Name, field.Name, field.FullName, declaringType, () => CecilFormatting.Field(field, declaringType)))
                {
                    AddDefinition(hits, file, assemblyName, HitKind.Field,
                        CecilFormatting.Field(field, declaringType), null);
                }
            }
        }

        if (options.Kinds.Includes(HitKind.Property))
        {
            foreach (var property in type.Properties)
            {
                if (matcher.IsMemberMatch(
                        property.Name,
                        PropertyLogicalName(property),
                        property.FullName,
                        declaringType,
                        () => CecilFormatting.Property(property, declaringType)))
                {
                    AddDefinition(hits, file, assemblyName, HitKind.Property,
                        CecilFormatting.Property(property, declaringType),
                        () => DebugLocations.First(property.GetMethod) ?? DebugLocations.First(property.SetMethod));
                }
            }
        }

        if (options.Kinds.Includes(HitKind.Event))
        {
            foreach (var @event in type.Events)
            {
                if (matcher.IsMemberMatch(
                        @event.Name,
                        EventLogicalName(@event),
                        @event.FullName,
                        declaringType,
                        () => CecilFormatting.Event(@event, declaringType)))
                {
                    AddDefinition(hits, file, assemblyName, HitKind.Event,
                        CecilFormatting.Event(@event, declaringType),
                        () => FirstEventLocation(@event));
                }
            }
        }

        if (options.Kinds.Includes(HitKind.Method))
        {
            foreach (var method in type.Methods)
            {
                if (IsPropertyOrEventAccessor(method))
                {
                    continue;
                }

                if (matcher.IsMemberMatch(
                        method.Name,
                        MethodLogicalName(method),
                        method.FullName,
                        declaringType,
                        () => CecilFormatting.Method(method, declaringType)))
                {
                    AddDefinition(hits, file, assemblyName, HitKind.Method,
                        CecilFormatting.Method(method, declaringType), () => DebugLocations.First(method));
                }
            }
        }
    }

    private static void SearchReferences(
        TypeDefinition type,
        string file,
        string assemblyName,
        SearchOptions options,
        SearchMatcher matcher,
        SearchHitCollector hits,
        ReferenceScratch scratch)
    {
        var searchTypes = options.Kinds.Includes(HitKind.Type) || options.Kinds.Includes(HitKind.Namespace);
        var searchMembers = options.Kinds.Includes(HitKind.Method) ||
                            options.Kinds.Includes(HitKind.Property) ||
                            options.Kinds.Includes(HitKind.Event);
        var searchFields = options.Kinds.Includes(HitKind.Field);
        foreach (var method in type.Methods.Where(method => method.HasBody))
        {
            // One object per method carries the lazily rendered container and the sequence-point
            // mapper; per instruction nothing is allocated unless a hit is retained.
            var site = new MethodSite(method);
            var body = method.Body;
            foreach (var instruction in body.Instructions)
            {
                switch (instruction.Operand)
                {
                    case MethodReference target:
                        if (searchMembers)
                        {
                            SearchMethodReference(target, instruction, file, assemblyName, site, options, matcher, hits, scratch);
                        }

                        if (searchTypes)
                        {
                            SearchTypeTargets(scratch.TypesOf(target), instruction, file, assemblyName, site, options, matcher, hits);
                        }

                        break;
                    case FieldReference target:
                        if (searchFields)
                        {
                            var entry = scratch.EntryFor(target);
                            if (matcher.IsMatch(target.Name, entry.QualifiedName, entry.FullName, entry.Symbol))
                            {
                                AddReference(hits, file, assemblyName, HitKind.Field, entry.Symbol, site, instruction);
                            }
                        }

                        if (searchTypes)
                        {
                            SearchTypeTargets(scratch.TypesOf(target), instruction, file, assemblyName, site, options, matcher, hits);
                        }

                        break;
                    case TypeReference target:
                        if (searchTypes)
                        {
                            SearchTypeTargets(scratch.TypesOf(target), instruction, file, assemblyName, site, options, matcher, hits);
                        }

                        break;
                    case Mono.Cecil.CallSite callSite:
                        if (searchTypes)
                        {
                            scratch.Roots.Clear();
                            scratch.Roots.Add(callSite.ReturnType);
                            foreach (var parameter in callSite.Parameters)
                            {
                                scratch.Roots.Add(parameter.ParameterType);
                            }

                            SearchTypeTargets(scratch.ExpandRoots(), instruction, file, assemblyName, site, options, matcher, hits);
                        }

                        break;
                }
            }

            if (searchTypes)
            {
                // Types that appear only in the exception-handler table or in the locals
                // signature are not operands of any instruction: "catch (MyException)" with a
                // handler that ignores the object, or a local that is only assigned. They are
                // reported at the handler's first instruction and at the method's first
                // instruction respectively.
                foreach (var handler in body.ExceptionHandlers)
                {
                    if (handler.CatchType is not null && handler.HandlerStart is not null)
                    {
                        SearchTypeTargets(
                            scratch.TypesOf(handler.CatchType), handler.HandlerStart, file, assemblyName, site, options, matcher, hits);
                    }
                }

                if (body.HasVariables && body.Instructions.Count > 0)
                {
                    scratch.Roots.Clear();
                    foreach (var variable in body.Variables)
                    {
                        scratch.Roots.Add(variable.VariableType);
                    }

                    SearchTypeTargets(scratch.ExpandRoots(), body.Instructions[0], file, assemblyName, site, options, matcher, hits);
                }
            }

            // Nothing after this point needs this method's body or sequence points again.
            method.ReleaseBody();
        }
    }

    private static void SearchMethodReference(
        MethodReference target,
        Instruction instruction,
        string file,
        string assemblyName,
        MethodSite site,
        SearchOptions options,
        SearchMatcher matcher,
        SearchHitCollector hits,
        ReferenceScratch scratch)
    {
        var entry = scratch.CandidatesFor(target);
        foreach (var candidate in entry.Candidates)
        {
            if (options.Kinds.Includes(candidate.Kind) &&
                matcher.IsMatch(
                    candidate.MetadataName,
                    candidate.LogicalName,
                    candidate.QualifiedName,
                    candidate.LogicalQualifiedName,
                    entry.FullName,
                    candidate.Symbol))
            {
                AddReference(hits, file, assemblyName, candidate.Kind, candidate.Symbol, site, instruction);
            }
        }
    }

    /// <summary>
    /// Matches the types reachable from one operand (or handler, or locals signature). The
    /// targets were expanded and de-duplicated once per member by <see cref="ReferenceScratch"/>,
    /// including the @Assembly disambiguation of types whose unscoped names collide.
    /// </summary>
    private static void SearchTypeTargets(
        TypeTarget[] targets,
        Instruction instruction,
        string file,
        string assemblyName,
        MethodSite site,
        SearchOptions options,
        SearchMatcher matcher,
        SearchHitCollector hits)
    {
        var searchTypes = options.Kinds.Includes(HitKind.Type);
        var searchNamespaces = options.Kinds.Includes(HitKind.Namespace);
        foreach (var target in targets)
        {
            if (searchTypes &&
                target.FirstOfType &&
                matcher.IsMatch(
                    target.Name,
                    target.Names.FullName,
                    target.Names.Unscoped,
                    target.Symbol,
                    target.Names.NameWithoutArity,
                    target.Names.UnscopedWithoutArity))
            {
                AddReference(hits, file, assemblyName, HitKind.Type, target.Symbol, site, instruction);
            }

            if (searchNamespaces && target.Namespace is not null && matcher.IsMatch(target.Namespace))
            {
                AddReference(hits, file, assemblyName, HitKind.Namespace, target.Namespace, site, instruction);
            }
        }
    }

    private static IEnumerable<TypeReference> ExpandTypeReferences(List<TypeReference> roots, Stack<TypeReference> stack)
    {
        stack.Clear();
        for (var index = roots.Count - 1; index >= 0; index--)
        {
            stack.Push(roots[index]);
        }

        var visited = 0;
        while (stack.Count > 0)
        {
            if (++visited > 1_000_000)
            {
                throw new InvalidOperationException("型参照の展開数が安全上限を超えました。");
            }

            var type = stack.Pop();
            yield return type;
            switch (type)
            {
                case GenericInstanceType genericType:
                    PushReverse(stack, genericType.GenericArguments);
                    stack.Push(genericType.ElementType);
                    break;
                case OptionalModifierType optionalModifier:
                    stack.Push(optionalModifier.ElementType);
                    stack.Push(optionalModifier.ModifierType);
                    break;
                case RequiredModifierType requiredModifier:
                    stack.Push(requiredModifier.ElementType);
                    stack.Push(requiredModifier.ModifierType);
                    break;
                case FunctionPointerType functionPointer:
                    for (var index = functionPointer.Parameters.Count - 1; index >= 0; index--)
                    {
                        stack.Push(functionPointer.Parameters[index].ParameterType);
                    }

                    stack.Push(functionPointer.ReturnType);
                    break;
                case TypeSpecification specification:
                    stack.Push(specification.ElementType);
                    break;
            }
        }
    }

    /// <summary>
    /// Resolves a method reference to what it denotes (a method, or a property/event through
    /// MethodSemantics). Pure: the diagnostics for an unresolved dependency are returned, not
    /// recorded, so the result can be shared by every file that references the same member.
    /// </summary>
    private static MemberResolution ResolveMemberCandidates(MethodReference method, SearchOptions options)
    {
        if (method.DeclaringType.IsArray)
        {
            // Get/Set/Address/.ctor on an array type are pseudo-methods the runtime synthesizes;
            // there is no definition to resolve, so their dependency is not missing.
            return new MemberResolution(UnresolvedCandidates(method, options), false, null, null);
        }

        try
        {
            var definition = method.Resolve();
            if (definition is not null)
            {
                var owner = AccessorMaps.GetValue(definition.DeclaringType, static type => new AccessorMap(type))
                    .Find(definition);
                if (owner?.Property is { } property)
                {
                    var logicalName = PropertyLogicalName(property);
                    return new MemberResolution(
                        options.Kinds.Includes(HitKind.Property) ? [new MemberCandidate(
                            HitKind.Property,
                            property.Name,
                            logicalName,
                            CecilFormatting.MemberName(property.DeclaringType, property.Name),
                            CecilFormatting.MemberName(method.DeclaringType, logicalName),
                            CecilFormatting.Property(property, method))] : [],
                        false,
                        null,
                        definition.Module);
                }

                if (owner?.Event is { } @event)
                {
                    var logicalName = EventLogicalName(@event);
                    return new MemberResolution(
                        options.Kinds.Includes(HitKind.Event) ? [new MemberCandidate(
                            HitKind.Event,
                            @event.Name,
                            logicalName,
                            CecilFormatting.MemberName(@event.DeclaringType, @event.Name),
                            CecilFormatting.MemberName(method.DeclaringType, logicalName),
                            CecilFormatting.Event(@event, method))] : [],
                        false,
                        null,
                        definition.Module);
                }

                return new MemberResolution(
                    options.Kinds.Includes(HitKind.Method) ? [MethodCandidate(method, MethodLogicalName(definition))] : [],
                    false,
                    null,
                    definition.Module);
            }

            return new MemberResolution(UnresolvedCandidates(method, options), true, null, null);
        }
        catch (Exception ex) when (ExceptionPolicy.IsRecoverableAssemblyError(ex))
        {
            return new MemberResolution(UnresolvedCandidates(method, options), true, AssemblyResolutionDetail.Describe(ex), null);
        }
    }

    /// <param name="Unresolved">True when the dependency could not be resolved; the reference is then reported as incomplete.</param>
    /// <param name="Reason">The resolver's explanation of a failure, when it threw one.</param>
    /// <param name="DefinitionModule">The module the definition was found in, when it was.</param>
    private sealed record MemberResolution(
        IReadOnlyList<MemberCandidate> Candidates,
        bool Unresolved,
        string? Reason,
        ModuleDefinition? DefinitionModule);

    /// <summary>
    /// Run-wide cache of member resolutions that every file in a folder would repeat: members
    /// of framework assemblies (resolved through the shared framework resolver) and members of
    /// dependencies that cannot be resolved at all. Resolving walks the dependency's metadata
    /// under Cecil's module lock, which the files scanned in parallel would otherwise contend
    /// for on every reference to the same framework member. The key is the referencing file's
    /// folder, the dependency's identity and the reference's canonical symbol, which unlike
    /// Cecil's FullName renders generic parameters by position.
    /// </summary>
    private sealed class MemberResolutionCache(IdentityAwareAssemblyResolver frameworkResolver)
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, MemberResolution> _entries =
            new(StringComparer.Ordinal);

        public bool TryGet(string key, out MemberResolution resolution) => _entries.TryGetValue(key, out resolution!);

        public void Share(string key, MemberResolution resolution)
        {
            if (resolution.Unresolved ||
                (resolution.DefinitionModule is { } module && ReferenceEquals(module.AssemblyResolver, frameworkResolver)))
            {
                _entries.TryAdd(key, resolution);
            }
        }
    }

    /// <summary>
    /// Candidates for a reference whose definition cannot be resolved. Without MethodSemantics
    /// an accessor-looking name (get_/set_/add_/remove_/raise_) cannot be classified, so it is
    /// reported both as an ordinary method and as the property/event it most likely belongs to,
    /// which avoids false negatives when the dependency is missing.
    /// </summary>
    private static List<MemberCandidate> UnresolvedCandidates(MethodReference method, SearchOptions options)
    {
        var candidates = new List<MemberCandidate>(2);
        if (options.Kinds.Includes(HitKind.Method))
        {
            // A dotted name is an explicit interface implementation; matching its trailing
            // member name keeps the answer the same whether or not the dependency resolved.
            candidates.Add(MethodCandidate(method, CecilFormatting.ExplicitMemberName(method.Name)));
        }

        if (options.Kinds.Includes(HitKind.Property) && CecilFormatting.IsPropertyAccessorName(method.Name))
        {
            var logicalName = CecilFormatting.LogicalMemberName(method.Name);
            candidates.Add(new MemberCandidate(
                HitKind.Property,
                method.Name,
                logicalName,
                CecilFormatting.MemberName(method.DeclaringType, method.Name),
                CecilFormatting.MemberName(method.DeclaringType, logicalName),
                CecilFormatting.Property(method)));
        }

        if (options.Kinds.Includes(HitKind.Event) && CecilFormatting.IsEventAccessorName(method.Name))
        {
            var logicalName = CecilFormatting.LogicalMemberName(method.Name);
            candidates.Add(new MemberCandidate(
                HitKind.Event,
                method.Name,
                logicalName,
                CecilFormatting.MemberName(method.DeclaringType, method.Name),
                CecilFormatting.MemberName(method.DeclaringType, logicalName),
                CecilFormatting.Event(method)));
        }

        return candidates;
    }

    private static MemberCandidate MethodCandidate(MethodReference method, string logicalName)
    {
        return new MemberCandidate(
            HitKind.Method,
            method.Name,
            logicalName,
            CecilFormatting.MemberName(method.DeclaringType, method.Name),
            CecilFormatting.MemberName(method.DeclaringType, logicalName),
            CecilFormatting.Method(method));
    }

    private static string MethodLogicalName(MethodDefinition method) =>
        method.HasOverrides ? CecilFormatting.ExplicitMemberName(method.Name) : method.Name;

    private static string PropertyLogicalName(PropertyDefinition property) =>
        HasOverrides(property.GetMethod, property.SetMethod, property.OtherMethods)
            ? CecilFormatting.ExplicitMemberName(property.Name)
            : property.Name;

    private static string EventLogicalName(EventDefinition @event) =>
        HasOverrides(@event.AddMethod, @event.RemoveMethod, @event.OtherMethods.Append(@event.InvokeMethod))
            ? CecilFormatting.ExplicitMemberName(@event.Name)
            : @event.Name;

    private static bool HasOverrides(
        MethodDefinition? first,
        MethodDefinition? second,
        IEnumerable<MethodDefinition?> others) =>
        first?.HasOverrides == true || second?.HasOverrides == true || others.Any(method => method?.HasOverrides == true);

    private static bool IsPropertyOrEventAccessor(MethodDefinition method) =>
        method.IsGetter || method.IsSetter || method.IsAddOn || method.IsRemoveOn || method.IsFire;

    private static SourceLocation? FirstEventLocation(EventDefinition @event)
    {
        var location = DebugLocations.First(@event.AddMethod) ??
                       DebugLocations.First(@event.RemoveMethod) ??
                       DebugLocations.First(@event.InvokeMethod);
        if (location is not null)
        {
            return location;
        }

        foreach (var method in @event.OtherMethods)
        {
            location = DebugLocations.First(method);
            if (location is not null)
            {
                return location;
            }
        }

        return null;
    }

    private static void PushReverse(Stack<TypeReference> stack, Mono.Collections.Generic.Collection<TypeReference> types)
    {
        for (var index = types.Count - 1; index >= 0; index--)
        {
            stack.Push(types[index]);
        }
    }

    private static IEnumerable<TypeDefinition> AllTypes(IEnumerable<TypeDefinition> roots)
    {
        var stack = new Stack<TypeDefinition>(roots.Reverse());
        while (stack.Count > 0)
        {
            var type = stack.Pop();
            yield return type;
            for (var index = type.NestedTypes.Count - 1; index >= 0; index--)
            {
                stack.Push(type.NestedTypes[index]);
            }
        }
    }

    private static void AddDefinition(
        SearchHitCollector hits,
        string file,
        string assemblyName,
        HitKind kind,
        string symbol,
        Func<SourceLocation?>? getLocation) =>
        hits.Add(HitScope.Definition, kind, () => new SearchHit(
            file, assemblyName, HitScope.Definition, kind, symbol, null, getLocation?.Invoke(), null));

    private static void AddReference(
        SearchHitCollector hits,
        string file,
        string assemblyName,
        HitKind kind,
        string symbol,
        MethodSite site,
        Instruction instruction) =>
        hits.Add(HitScope.Reference, kind, () => new SearchHit(
            file, assemblyName, HitScope.Reference, kind, symbol, site.Container, site.LocationOf(instruction), instruction.Offset));

    /// <summary>
    /// Symbols are skipped for definition-only searches of namespaces, types and fields, whose
    /// definitions have no sequence points; the report says "symbols not read" in that case.
    /// </summary>
    internal static SymbolMode EffectiveSymbolMode(SearchOptions options)
    {
        if (options.SymbolMode != SymbolMode.Auto || options.Scope != SearchScope.Definitions)
        {
            return options.SymbolMode;
        }

        var needsDefinitionLocations =
            options.Kinds.Includes(HitKind.Method) ||
            options.Kinds.Includes(HitKind.Property) ||
            options.Kinds.Includes(HitKind.Event);
        return needsDefinitionLocations ? SymbolMode.Auto : SymbolMode.Off;
    }

    /// <summary>
    /// The per-method state a reference hit needs: the rendered container and the sequence-point
    /// mapper, both created on first use so a method without hits costs one small object.
    /// </summary>
    private sealed class MethodSite(MethodDefinition method)
    {
        private string? _container;
        private SequencePointMapper? _locations;

        public string Container => _container ??= CecilFormatting.Method(DebugLocations.DisplayMethod(method));

        public SourceLocation? LocationOf(Instruction instruction)
        {
            _locations ??= DebugLocations.CreateMapper(method);
            return _locations.ForInstruction(instruction);
        }
    }

    /// <summary>
    /// Per-module working state for reference searches. Everything derived from a member or
    /// type reference (candidate names, the expanded and de-duplicated set of types it
    /// mentions, rendered type names) is computed once per reference and reused for every
    /// instruction that carries it, so the per-instruction path is dictionary lookups on
    /// metadata tokens and string comparisons. Signature-created references (RID 0) are keyed
    /// by identity instead, because Cecil gives all of them the same table token.
    /// </summary>
    private sealed class ReferenceScratch
    {
        private readonly string _file;
        private readonly string _directory;
        private readonly SearchOptions _options;
        private readonly ResolutionDiagnostics _diagnostics;
        private readonly MemberResolutionCache _shared;

        private readonly Dictionary<uint, MethodCandidates> _methodsByToken = [];
        private readonly Dictionary<MethodReference, MethodCandidates> _methodsByReference = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<uint, FieldEntry> _fieldsByToken = [];
        private readonly Dictionary<FieldReference, FieldEntry> _fieldsByReference = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<uint, TypeTarget[]> _methodTypesByToken = [];
        private readonly Dictionary<MethodReference, TypeTarget[]> _methodTypesByReference = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<uint, TypeTarget[]> _fieldTypesByToken = [];
        private readonly Dictionary<FieldReference, TypeTarget[]> _fieldTypesByReference = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<uint, TypeTarget[]> _typeTargetsByToken = [];
        private readonly Dictionary<TypeReference, TypeTarget[]> _typeTargetsByReference = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<uint, TypeNames> _typeNamesByToken = [];
        private readonly Dictionary<TypeReference, TypeNames> _typeNamesByReference = new(ReferenceEqualityComparer.Instance);

        private readonly Stack<TypeReference> _expansionStack = new(16);
        private readonly List<(TypeReference Type, TypeNames Names)> _expanded = new(16);
        private readonly Dictionary<string, string> _firstIdentity = new(StringComparer.Ordinal);
        private readonly HashSet<string> _collisions = new(StringComparer.Ordinal);
        private readonly HashSet<string> _seenTypes = new(StringComparer.Ordinal);
        private readonly HashSet<string> _seenNamespaces = new(StringComparer.Ordinal);

        public ReferenceScratch(
            string file,
            SearchOptions options,
            ResolutionDiagnostics diagnostics,
            MemberResolutionCache shared)
        {
            _file = file;
            _directory = Path.GetDirectoryName(file) ?? string.Empty;
            _options = options;
            _diagnostics = diagnostics;
            _shared = shared;
        }

        /// <summary>Roots for an ad-hoc expansion (call sites, locals); fill, then call <see cref="ExpandRoots"/>.</summary>
        public List<TypeReference> Roots { get; } = new(8);

        public MethodCandidates CandidatesFor(MethodReference method) =>
            Cached(
                method,
                _methodsByToken,
                _methodsByReference,
                static (reference, scratch) => scratch.Resolve(reference),
                this);

        private MethodCandidates Resolve(MethodReference method)
        {
            MemberResolution? resolution = null;
            string? key = null;
            if (method.DeclaringType.GetElementType().Scope is AssemblyNameReference scope)
            {
                key = $"{_directory}\0{scope.FullName}\0{CecilFormatting.Method(method)}";
                _shared.TryGet(key, out resolution);
            }

            if (resolution is null)
            {
                resolution = ResolveMemberCandidates(method, _options);
                if (key is not null)
                {
                    _shared.Share(key, resolution);
                }
            }

            if (resolution.Unresolved)
            {
                _diagnostics.Add(_file, method, resolution.Reason);
            }

            return new MethodCandidates(resolution.Candidates, method.FullName);
        }

        public FieldEntry EntryFor(FieldReference field) =>
            Cached(
                field,
                _fieldsByToken,
                _fieldsByReference,
                static (reference, _) => new FieldEntry(
                    CecilFormatting.Field(reference),
                    CecilFormatting.MemberName(reference.DeclaringType, reference.Name),
                    reference.FullName),
                this);

        /// <summary>The declaring type, return type, parameter types and generic arguments of a method reference.</summary>
        public TypeTarget[] TypesOf(MethodReference method) =>
            Cached(
                method,
                _methodTypesByToken,
                _methodTypesByReference,
                static (reference, scratch) =>
                {
                    scratch.Roots.Clear();
                    scratch.Roots.Add(reference.DeclaringType);
                    scratch.Roots.Add(reference.ReturnType);
                    foreach (var parameter in reference.Parameters)
                    {
                        scratch.Roots.Add(parameter.ParameterType);
                    }

                    if (reference is GenericInstanceMethod genericMethod)
                    {
                        scratch.Roots.AddRange(genericMethod.GenericArguments);
                    }

                    return scratch.ExpandRoots();
                },
                this);

        /// <summary>The declaring type and field type of a field reference.</summary>
        public TypeTarget[] TypesOf(FieldReference field) =>
            Cached(
                field,
                _fieldTypesByToken,
                _fieldTypesByReference,
                static (reference, scratch) =>
                {
                    scratch.Roots.Clear();
                    scratch.Roots.Add(reference.DeclaringType);
                    scratch.Roots.Add(reference.FieldType);
                    return scratch.ExpandRoots();
                },
                this);

        public TypeTarget[] TypesOf(TypeReference type) =>
            Cached(
                type,
                _typeTargetsByToken,
                _typeTargetsByReference,
                static (reference, scratch) =>
                {
                    scratch.Roots.Clear();
                    scratch.Roots.Add(reference);
                    return scratch.ExpandRoots();
                },
                this);

        /// <summary>
        /// Expands <see cref="Roots"/> into every reachable type, keeps the first occurrence of
        /// each identity and of each namespace, and marks a type whose unscoped name collides
        /// with another scope in the same set so it is shown with its @Assembly identity.
        /// </summary>
        public TypeTarget[] ExpandRoots()
        {
            _expanded.Clear();
            _firstIdentity.Clear();
            _collisions.Clear();
            foreach (var type in ExpandTypeReferences(Roots, _expansionStack))
            {
                if (type is GenericParameter)
                {
                    continue;
                }

                var names = NamesFor(type);
                if (!_firstIdentity.TryAdd(names.Unscoped, names.Identity) &&
                    !string.Equals(_firstIdentity[names.Unscoped], names.Identity, StringComparison.Ordinal))
                {
                    _collisions.Add(names.Unscoped);
                }

                _expanded.Add((type, names));
            }

            _seenTypes.Clear();
            _seenNamespaces.Clear();
            var targets = new List<TypeTarget>(_expanded.Count);
            foreach (var (type, names) in _expanded)
            {
                var symbol = _collisions.Contains(names.Unscoped) ? names.Identity : names.Unscoped;
                var firstOfType = _seenTypes.Add(names.Identity);
                var @namespace = type.Namespace;
                var firstOfNamespace = !string.IsNullOrEmpty(@namespace) && _seenNamespaces.Add(@namespace);
                if (firstOfType || firstOfNamespace)
                {
                    targets.Add(new TypeTarget(type.Name, names, symbol, firstOfType, firstOfNamespace ? @namespace : null));
                }
            }

            return targets.ToArray();
        }

        private TypeNames NamesFor(TypeReference type) =>
            Cached(type, _typeNamesByToken, _typeNamesByReference, static (reference, _) => ComputeNames(reference), this);

        private static TypeNames ComputeNames(TypeReference type)
        {
            var unscoped = CecilFormatting.Type(type);
            return new TypeNames(
                unscoped,
                CecilFormatting.TypeIdentity(type),
                type.FullName,
                CecilFormatting.WithoutArity(type.Name),
                CecilFormatting.WithoutArity(unscoped));
        }

        /// <summary>Looks a reference up by its row token, or by identity for a signature-created one (RID 0).</summary>
        private static TValue Cached<TReference, TValue>(
            TReference reference,
            Dictionary<uint, TValue> byToken,
            Dictionary<TReference, TValue> byReference,
            Func<TReference, ReferenceScratch, TValue> compute,
            ReferenceScratch scratch)
            where TReference : MemberReference
        {
            var token = reference.MetadataToken;
            if (token.RID != 0)
            {
                if (!byToken.TryGetValue(token.ToUInt32(), out var value))
                {
                    value = compute(reference, scratch);
                    byToken.Add(token.ToUInt32(), value);
                }

                return value;
            }

            if (!byReference.TryGetValue(reference, out var untokened))
            {
                untokened = compute(reference, scratch);
                byReference.Add(reference, untokened);
            }

            return untokened;
        }
    }

    /// <param name="Namespace">The namespace when this target is the first of its namespace in the set, otherwise null.</param>
    private sealed record TypeTarget(string Name, TypeNames Names, string Symbol, bool FirstOfType, string? Namespace);

    private sealed record MethodCandidates(IReadOnlyList<MemberCandidate> Candidates, string FullName);

    private sealed record FieldEntry(string Symbol, string QualifiedName, string FullName);

    private sealed record TypeNames(
        string Unscoped,
        string Identity,
        string FullName,
        string? NameWithoutArity,
        string? UnscopedWithoutArity);

    private sealed record MemberCandidate(
        HitKind Kind,
        string MetadataName,
        string LogicalName,
        string QualifiedName,
        string LogicalQualifiedName,
        string Symbol);

    private sealed record AccessorOwner(PropertyDefinition? Property, EventDefinition? Event);

    private sealed class AccessorMap
    {
        private readonly Dictionary<uint, AccessorOwner> _owners = [];

        public AccessorMap(TypeDefinition type)
        {
            foreach (var property in type.Properties)
            {
                Add(property.GetMethod, new AccessorOwner(property, null));
                Add(property.SetMethod, new AccessorOwner(property, null));
                foreach (var method in property.OtherMethods)
                {
                    Add(method, new AccessorOwner(property, null));
                }
            }

            foreach (var @event in type.Events)
            {
                Add(@event.AddMethod, new AccessorOwner(null, @event));
                Add(@event.RemoveMethod, new AccessorOwner(null, @event));
                Add(@event.InvokeMethod, new AccessorOwner(null, @event));
                foreach (var method in @event.OtherMethods)
                {
                    Add(method, new AccessorOwner(null, @event));
                }
            }
        }

        public AccessorOwner? Find(MethodDefinition method) =>
            _owners.GetValueOrDefault(method.MetadataToken.ToUInt32());

        private void Add(MethodDefinition? method, AccessorOwner owner)
        {
            if (method is not null)
            {
                _owners[method.MetadataToken.ToUInt32()] = owner;
            }
        }
    }

    /// <summary>
    /// Collects member references whose dependency could not be resolved and reports them as
    /// one warning per (file, dependency) with a member count, so a missing framework assembly
    /// yields a handful of lines instead of one per referenced member.
    /// </summary>
    private sealed class ResolutionDiagnostics(List<ScanError> errors)
    {
        private readonly Dictionary<string, DependencyFailure> _failures = new(StringComparer.Ordinal);

        public void Add(string file, MethodReference method, string? reason)
        {
            var dependency = method.DeclaringType.Scope switch
            {
                AssemblyNameReference assembly => assembly.FullName,
                ModuleReference module => module.Name,
                _ => method.DeclaringType.Scope?.Name ?? method.DeclaringType.FullName,
            };

            var key = $"{file}\0{dependency}";
            if (!_failures.TryGetValue(key, out var failure))
            {
                failure = new DependencyFailure(file, dependency, method.FullName, reason);
                _failures.Add(key, failure);
            }

            // Distinct references are counted by metadata token rather than by retaining every
            // full signature; a large assembly with no framework on disk has hundreds of
            // thousands of them.
            failure.MemberTokens.Add(method.MetadataToken.ToUInt32());
        }

        /// <summary>Emits the aggregated warnings collected so far; call once per analyzed file.</summary>
        public void Flush()
        {
            foreach (var failure in _failures.Values.OrderBy(item => item.Dependency, StringComparer.Ordinal))
            {
                var count = failure.MemberTokens.Count;
                var members = count == 1
                    ? $"メンバー '{failure.FirstMember}'"
                    : $"メンバー {count} 件（例: '{failure.FirstMember}'）";
                errors.Add(new ScanError(
                    failure.File,
                    $"依存先 '{failure.Dependency}' の{members}を解決できず、参照分類が不完全です" +
                    (failure.Reason is null ? "。" : $": {failure.Reason}")));
            }

            _failures.Clear();
        }

        private sealed record DependencyFailure(string File, string Dependency, string FirstMember, string? Reason)
        {
            public HashSet<uint> MemberTokens { get; } = [];
        }
    }
}
