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
        var resolutionDiagnostics = new ResolutionDiagnostics(errors);
        var succeeded = 0;
        var withSymbols = 0;
        var symbolMode = EffectiveSymbolMode(options);

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var resolver = CecilResolverFactory.Create(file, referenceDirectories, discovery.SearchDirectories);

            // Hits are staged per file and merged whether or not the file completes: a failure
            // part-way through still reports what was found before it (the file is listed as an
            // error and the exit code says the result is incomplete). The stage only needs the
            // capacity the report can still show, so files past the --max-results limit do not
            // materialize containers and locations that would be dropped on merge.
            var fileHits = new SearchHitCollector(hits.RemainingCapacity);
            try
            {
                var fileHasSymbols = false;
                using (var module = CecilModuleReader.Read(file, symbolMode, resolver, out var symbolWarning))
                {
                    if (symbolWarning is not null)
                    {
                        warnings.Add(new ScanError(file, symbolWarning));
                    }

                    SearchModule(module, file, options, matcher, fileHits, resolutionDiagnostics, cancellationToken);
                    if (discovery.InputIsFile)
                    {
                        SecondaryModules.ForEach(
                            module,
                            file,
                            (secondary, moduleFile) => SearchModule(
                                secondary, moduleFile, options, matcher, fileHits, resolutionDiagnostics, cancellationToken),
                            errors.Add,
                            cancellationToken);
                    }

                    fileHasSymbols = module.HasSymbols;
                }

                succeeded++;
                if (fileHasSymbols)
                {
                    withSymbols++;
                }
            }
            catch (Exception ex) when (ExceptionPolicy.IsRecoverableAssemblyError(ex))
            {
                errors.Add(new ScanError(file, ex.Message, ex));
            }
            finally
            {
                hits.Merge(fileHits);
                resolutionDiagnostics.Flush();
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

    private static void SearchModule(
        ModuleDefinition module,
        string file,
        SearchOptions options,
        SearchMatcher matcher,
        SearchHitCollector hits,
        ResolutionDiagnostics resolutionDiagnostics,
        CancellationToken cancellationToken)
    {
        var assemblyName = module.Assembly?.Name.FullName ?? module.Name;
        var seenNamespaces = new HashSet<string>(StringComparer.Ordinal);
        var scratch = new ReferenceScratch(file, options, resolutionDiagnostics);
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
            Add(hits, file, assemblyName, HitScope.Definition, HitKind.Namespace, type.Namespace, null, null, null);
        }

        // The declaring type renders identically for every member, so format it once per type.
        var declaringType = CecilFormatting.Type(type);

        if (options.Kinds.Includes(HitKind.Type) && matcher.IsMatch(type.Name, type.FullName, declaringType))
        {
            Add(hits, file, assemblyName, HitScope.Definition, HitKind.Type, declaringType, null, null, null);
        }

        if (options.Kinds.Includes(HitKind.Field))
        {
            foreach (var field in type.Fields)
            {
                var symbol = CecilFormatting.Field(field, declaringType);
                if (matcher.IsMatch(
                        field.Name,
                        CecilFormatting.MemberName(declaringType, field.Name),
                        field.FullName,
                        symbol))
                {
                    Add(hits, file, assemblyName, HitScope.Definition, HitKind.Field, symbol, null, null, null);
                }
            }
        }

        if (options.Kinds.Includes(HitKind.Property))
        {
            foreach (var property in type.Properties)
            {
                var symbol = CecilFormatting.Property(property, declaringType);
                var logicalName = PropertyLogicalName(property);
                if (matcher.IsMatch(
                        property.Name,
                        logicalName,
                        CecilFormatting.MemberName(declaringType, property.Name),
                        LogicalMemberName(declaringType, property.Name, logicalName),
                        property.FullName,
                        symbol))
                {
                    Add(hits, file, assemblyName, HitScope.Definition, HitKind.Property, symbol, null,
                        () => DebugLocations.First(property.GetMethod) ?? DebugLocations.First(property.SetMethod), null);
                }
            }
        }

        if (options.Kinds.Includes(HitKind.Event))
        {
            foreach (var @event in type.Events)
            {
                var symbol = CecilFormatting.Event(@event, declaringType);
                var logicalName = EventLogicalName(@event);
                if (matcher.IsMatch(
                        @event.Name,
                        logicalName,
                        CecilFormatting.MemberName(declaringType, @event.Name),
                        LogicalMemberName(declaringType, @event.Name, logicalName),
                        @event.FullName,
                        symbol))
                {
                    Add(hits, file, assemblyName, HitScope.Definition, HitKind.Event, symbol, null,
                        () => FirstEventLocation(@event), null);
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

                var symbol = CecilFormatting.Method(method, declaringType);
                var logicalName = MethodLogicalName(method);
                if (matcher.IsMatch(
                        method.Name,
                        logicalName,
                        CecilFormatting.MemberName(declaringType, method.Name),
                        LogicalMemberName(declaringType, method.Name, logicalName),
                        method.FullName,
                        symbol))
                {
                    Add(hits, file, assemblyName, HitScope.Definition, HitKind.Method,
                        symbol, null, () => DebugLocations.First(method), null);
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
        foreach (var method in type.Methods.Where(method => method.HasBody))
        {
            string? container = null;
            string GetContainer() => container ??= CecilFormatting.Method(DebugLocations.DisplayMethod(method));
            SequencePointMapper? locations = null;
            foreach (var instruction in method.Body.Instructions)
            {
                if (instruction.Operand is not (MethodReference or FieldReference or TypeReference or Mono.Cecil.CallSite))
                {
                    continue;
                }

                SourceLocation? location = null;
                var locationResolved = false;
                SourceLocation? GetLocation()
                {
                    if (!locationResolved)
                    {
                        locations ??= DebugLocations.CreateMapper(method);
                        location = locations.ForInstruction(instruction);
                        locationResolved = true;
                    }

                    return location;
                }

                switch (instruction.Operand)
                {
                    case MethodReference target:
                        SearchMethodReference(target, instruction, file, assemblyName,
                            GetContainer, GetLocation, options, matcher, hits, scratch);
                        break;
                    case FieldReference target:
                        SearchFieldReference(target, instruction, file, assemblyName,
                            GetContainer, GetLocation, options, matcher, hits, scratch);
                        break;
                    case TypeReference target:
                        if (searchTypes)
                        {
                            scratch.Roots.Clear();
                            scratch.Roots.Add(target);
                            SearchTypeReferences(instruction, file, assemblyName,
                                GetContainer, GetLocation, options, matcher, hits, scratch);
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

                            SearchTypeReferences(instruction, file, assemblyName,
                                GetContainer, GetLocation, options, matcher, hits, scratch);
                        }

                        break;
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
        Func<string> getContainer,
        Func<SourceLocation?> getLocation,
        SearchOptions options,
        SearchMatcher matcher,
        SearchHitCollector hits,
        ReferenceScratch scratch)
    {
        if (options.Kinds.Includes(HitKind.Method) ||
            options.Kinds.Includes(HitKind.Property) ||
            options.Kinds.Includes(HitKind.Event))
        {
            foreach (var candidate in scratch.CandidatesFor(target))
            {
                if (options.Kinds.Includes(candidate.Kind) &&
                    matcher.IsMatch(
                        candidate.MetadataName,
                        candidate.LogicalName,
                        candidate.QualifiedName,
                        candidate.LogicalQualifiedName,
                        target.FullName,
                        candidate.Symbol))
                {
                    Add(hits, file, assemblyName, HitScope.Reference, candidate.Kind,
                        candidate.Symbol, getContainer, getLocation, instruction.Offset);
                }
            }
        }

        if (options.Kinds.Includes(HitKind.Type) || options.Kinds.Includes(HitKind.Namespace))
        {
            var roots = scratch.Roots;
            roots.Clear();
            roots.Add(target.DeclaringType);
            roots.Add(target.ReturnType);
            foreach (var parameter in target.Parameters)
            {
                roots.Add(parameter.ParameterType);
            }

            if (target is GenericInstanceMethod genericMethod)
            {
                roots.AddRange(genericMethod.GenericArguments);
            }

            SearchTypeReferences(instruction, file, assemblyName, getContainer, getLocation, options, matcher, hits, scratch);
        }
    }

    private static void SearchFieldReference(
        FieldReference target,
        Instruction instruction,
        string file,
        string assemblyName,
        Func<string> getContainer,
        Func<SourceLocation?> getLocation,
        SearchOptions options,
        SearchMatcher matcher,
        SearchHitCollector hits,
        ReferenceScratch scratch)
    {
        if (options.Kinds.Includes(HitKind.Field))
        {
            var symbol = scratch.SymbolFor(target);
            if (matcher.IsMatch(
                    target.Name,
                    CecilFormatting.MemberName(target.DeclaringType, target.Name),
                    target.FullName,
                    symbol))
            {
                Add(hits, file, assemblyName, HitScope.Reference, HitKind.Field,
                    symbol, getContainer, getLocation, instruction.Offset);
            }
        }

        if (options.Kinds.Includes(HitKind.Type) || options.Kinds.Includes(HitKind.Namespace))
        {
            scratch.Roots.Clear();
            scratch.Roots.Add(target.DeclaringType);
            scratch.Roots.Add(target.FieldType);
            SearchTypeReferences(instruction, file, assemblyName, getContainer, getLocation, options, matcher, hits, scratch);
        }
    }

    /// <summary>
    /// Searches every type reachable from <see cref="ReferenceScratch.Roots"/> for one instruction.
    /// A type whose unscoped name collides with another scope in the same instruction is shown
    /// with its @Assembly identity so the two remain distinguishable.
    /// </summary>
    private static void SearchTypeReferences(
        Instruction instruction,
        string file,
        string assemblyName,
        Func<string> getContainer,
        Func<SourceLocation?> getLocation,
        SearchOptions options,
        SearchMatcher matcher,
        SearchHitCollector hits,
        ReferenceScratch scratch)
    {
        var searchTypes = options.Kinds.Includes(HitKind.Type);
        var searchNamespaces = options.Kinds.Includes(HitKind.Namespace);
        if (!searchTypes && !searchNamespaces)
        {
            return;
        }

        scratch.BeginInstruction();
        foreach (var target in ExpandTypeReferences(scratch.Roots, scratch.ExpansionStack))
        {
            if (target is GenericParameter)
            {
                continue;
            }

            var unscoped = CecilFormatting.Type(target);
            var identity = CecilFormatting.TypeIdentity(target);
            if (scratch.FirstIdentity.TryAdd(unscoped, identity))
            {
                // first occurrence
            }
            else if (!string.Equals(scratch.FirstIdentity[unscoped], identity, StringComparison.Ordinal))
            {
                scratch.Collisions.Add(unscoped);
            }

            scratch.Targets.Add((target, unscoped, identity));
        }

        foreach (var (target, unscoped, identity) in scratch.Targets)
        {
            var symbol = scratch.Collisions.Contains(unscoped) ? identity : unscoped;
            if (searchTypes &&
                scratch.SeenTypes.Add(identity) &&
                matcher.IsMatch(target.Name, target.FullName, unscoped, symbol))
            {
                Add(hits, file, assemblyName, HitScope.Reference, HitKind.Type,
                    symbol, getContainer, getLocation, instruction.Offset);
            }

            if (searchNamespaces &&
                !string.IsNullOrEmpty(target.Namespace) &&
                scratch.SeenNamespaces.Add(target.Namespace) &&
                matcher.IsMatch(target.Namespace))
            {
                Add(hits, file, assemblyName, HitScope.Reference, HitKind.Namespace,
                    target.Namespace, getContainer, getLocation, instruction.Offset);
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

    private static List<MemberCandidate> ResolveMemberCandidates(
        MethodReference method,
        SearchOptions options,
        string file,
        ResolutionDiagnostics diagnostics)
    {
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
                    return options.Kinds.Includes(HitKind.Property) ? [new MemberCandidate(
                        HitKind.Property,
                        property.Name,
                        logicalName,
                        CecilFormatting.MemberName(property.DeclaringType, property.Name),
                        CecilFormatting.MemberName(method.DeclaringType, logicalName),
                        CecilFormatting.Property(property, method))] : [];
                }

                if (owner?.Event is { } @event)
                {
                    var logicalName = EventLogicalName(@event);
                    return options.Kinds.Includes(HitKind.Event) ? [new MemberCandidate(
                        HitKind.Event,
                        @event.Name,
                        logicalName,
                        CecilFormatting.MemberName(@event.DeclaringType, @event.Name),
                        CecilFormatting.MemberName(method.DeclaringType, logicalName),
                        CecilFormatting.Event(@event, method))] : [];
                }

                return options.Kinds.Includes(HitKind.Method)
                    ? [MethodCandidate(method, MethodLogicalName(definition))]
                    : [];
            }

            diagnostics.Add(file, method, null);
        }
        catch (Exception ex) when (ExceptionPolicy.IsRecoverableAssemblyError(ex))
        {
            diagnostics.Add(file, method, ex);
        }

        return UnresolvedCandidates(method, options);
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
            candidates.Add(MethodCandidate(method, method.Name));
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

    /// <summary>The qualified logical name, or null when it would duplicate the metadata name.</summary>
    private static string? LogicalMemberName(string declaringType, string metadataName, string logicalName) =>
        string.Equals(metadataName, logicalName, StringComparison.Ordinal)
            ? null
            : CecilFormatting.MemberName(declaringType, logicalName);

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

    private static void Add(
        SearchHitCollector hits,
        string file,
        string assemblyName,
        HitScope scope,
        HitKind kind,
        string symbol,
        Func<string>? getContainer,
        Func<SourceLocation?>? getLocation,
        int? ilOffset) =>
        hits.Add(scope, kind, () => new SearchHit(
            file,
            assemblyName,
            scope,
            kind,
            symbol,
            getContainer?.Invoke(),
            getLocation?.Invoke(),
            ilOffset));

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
    /// Per-module working state for reference searches: caches keyed by Cecil's interned
    /// reference objects (the same MemberRef/TypeRef instance recurs across instructions), and
    /// reusable collections so the per-instruction type walk allocates nothing steady-state.
    /// </summary>
    private sealed class ReferenceScratch(string file, SearchOptions options, ResolutionDiagnostics diagnostics)
    {
        private readonly Dictionary<MethodReference, IReadOnlyList<MemberCandidate>> _methodCandidates =
            new(ReferenceEqualityComparer.Instance);

        private readonly Dictionary<FieldReference, string> _fieldSymbols = new(ReferenceEqualityComparer.Instance);

        public List<TypeReference> Roots { get; } = new(8);

        public Stack<TypeReference> ExpansionStack { get; } = new(16);

        public List<(TypeReference Type, string Unscoped, string Identity)> Targets { get; } = new(16);

        public Dictionary<string, string> FirstIdentity { get; } = new(StringComparer.Ordinal);

        public HashSet<string> Collisions { get; } = new(StringComparer.Ordinal);

        public HashSet<string> SeenTypes { get; } = new(StringComparer.Ordinal);

        public HashSet<string> SeenNamespaces { get; } = new(StringComparer.Ordinal);

        public IReadOnlyList<MemberCandidate> CandidatesFor(MethodReference method)
        {
            if (!_methodCandidates.TryGetValue(method, out var candidates))
            {
                candidates = ResolveMemberCandidates(method, options, file, diagnostics);
                _methodCandidates.Add(method, candidates);
            }

            return candidates;
        }

        public string SymbolFor(FieldReference field)
        {
            if (!_fieldSymbols.TryGetValue(field, out var symbol))
            {
                symbol = CecilFormatting.Field(field);
                _fieldSymbols.Add(field, symbol);
            }

            return symbol;
        }

        public void BeginInstruction()
        {
            Targets.Clear();
            FirstIdentity.Clear();
            Collisions.Clear();
            SeenTypes.Clear();
            SeenNamespaces.Clear();
        }
    }

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

        public void Add(string file, MethodReference method, Exception? exception)
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
                failure = new DependencyFailure(file, dependency, method.FullName, exception?.Message);
                _failures.Add(key, failure);
            }

            failure.Members.Add(method.FullName);
        }

        /// <summary>Emits the aggregated warnings collected so far; call once per analyzed file.</summary>
        public void Flush()
        {
            foreach (var failure in _failures.Values.OrderBy(item => item.Dependency, StringComparer.Ordinal))
            {
                var count = failure.Members.Count;
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
            public HashSet<string> Members { get; } = new(StringComparer.Ordinal);
        }
    }
}
