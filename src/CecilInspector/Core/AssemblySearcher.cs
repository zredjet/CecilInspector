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
    public SearchResult Search(SearchOptions options, AssemblyDiscoveryResult discovery)
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
            using var resolver = CecilResolverFactory.Create(file, referenceDirectories, discovery.SearchDirectories);
            try
            {
                var fileHits = new SearchHitCollector(options.MaxResults);
                var fileHasSymbols = false;
                using (var module = CecilModuleReader.Read(file, symbolMode, resolver, out var symbolWarning))
                {
                    if (symbolWarning is not null)
                    {
                        warnings.Add(new ScanError(file, symbolWarning));
                    }

                    SearchModule(module, file, options, matcher, fileHits, resolutionDiagnostics);
                    if (files.Count == 1 && module.Assembly is not null)
                    {
                        SearchSecondaryModules(module, file, options, matcher, fileHits, resolutionDiagnostics, errors);
                    }

                    fileHasSymbols = module.HasSymbols;
                }

                hits.Merge(fileHits);
                succeeded++;
                if (fileHasSymbols)
                {
                    withSymbols++;
                }
            }
            catch (Exception ex) when (ExceptionPolicy.IsRecoverableAssemblyError(ex))
            {
                errors.Add(new ScanError(file, ex.Message));
            }
            finally
            {
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

    /// <summary>
    /// Searches the secondary netmodules of a multi-module assembly. A missing or broken
    /// netmodule is reported as a warning for that module only; hits already collected from
    /// the manifest module are kept.
    /// </summary>
    private static void SearchSecondaryModules(
        ModuleDefinition manifestModule,
        string file,
        SearchOptions options,
        SearchMatcher matcher,
        SearchHitCollector hits,
        ResolutionDiagnostics resolutionDiagnostics,
        List<ScanError> errors)
    {
        ModuleDefinition[] secondaryModules;
        try
        {
            secondaryModules = manifestModule.Assembly.Modules.Where(candidate => candidate != manifestModule).ToArray();
        }
        catch (Exception ex) when (ExceptionPolicy.IsRecoverableAssemblyError(ex))
        {
            errors.Add(new ScanError(file, $"secondary netmoduleを読み込めません: {ex.Message}"));
            return;
        }

        foreach (var secondaryModule in secondaryModules)
        {
            var moduleFile = secondaryModule.FileName ?? file;
            try
            {
                SearchModule(secondaryModule, moduleFile, options, matcher, hits, resolutionDiagnostics);
            }
            catch (Exception ex) when (ExceptionPolicy.IsRecoverableAssemblyError(ex))
            {
                errors.Add(new ScanError(moduleFile, ex.Message));
            }
        }
    }

    private static void SearchModule(
        ModuleDefinition module,
        string file,
        SearchOptions options,
        SearchMatcher matcher,
        SearchHitCollector hits,
        ResolutionDiagnostics resolutionDiagnostics)
    {
        var assemblyName = module.Assembly?.Name.FullName ?? module.Name;
        var seenNamespaces = new HashSet<string>(StringComparer.Ordinal);
        foreach (var type in AllTypes(module.Types))
        {
            if (options.Scope is SearchScope.Definitions or SearchScope.All)
            {
                SearchDefinitions(type, file, assemblyName, options, matcher, hits, seenNamespaces);
            }

            if (options.Scope is SearchScope.References or SearchScope.All)
            {
                SearchReferences(type, file, assemblyName, options, matcher, hits, resolutionDiagnostics);
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

        if (options.Kinds.Includes(HitKind.Type))
        {
            var typeSymbol = CecilFormatting.Type(type);
            if (matcher.IsMatch(type.Name, type.FullName, typeSymbol))
            {
                Add(hits, file, assemblyName, HitScope.Definition, HitKind.Type, typeSymbol, null, null, null);
            }
        }

        if (options.Kinds.Includes(HitKind.Field))
        {
            foreach (var field in type.Fields)
            {
                var symbol = CecilFormatting.Field(field);
                if (matcher.IsMatch(
                        field.Name,
                        CecilFormatting.MemberName(field.DeclaringType, field.Name),
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
                var symbol = CecilFormatting.Property(property);
                var logicalName = PropertyLogicalName(property);
                if (matcher.IsMatch(
                        property.Name,
                        logicalName,
                        CecilFormatting.MemberName(property.DeclaringType, property.Name),
                        CecilFormatting.MemberName(property.DeclaringType, logicalName),
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
                var symbol = CecilFormatting.Event(@event);
                var logicalName = EventLogicalName(@event);
                if (matcher.IsMatch(
                        @event.Name,
                        logicalName,
                        CecilFormatting.MemberName(@event.DeclaringType, @event.Name),
                        CecilFormatting.MemberName(@event.DeclaringType, logicalName),
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
                var symbol = CecilFormatting.Method(method);
                var logicalName = MethodLogicalName(method);
                if (!IsPropertyOrEventAccessor(method) && matcher.IsMatch(
                        method.Name,
                        logicalName,
                        CecilFormatting.MemberName(method.DeclaringType, method.Name),
                        CecilFormatting.MemberName(method.DeclaringType, logicalName),
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
        ResolutionDiagnostics resolutionDiagnostics)
    {
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
                            GetContainer, GetLocation, options, matcher, hits, resolutionDiagnostics);
                        break;
                    case FieldReference target:
                        SearchFieldReference(target, instruction, file, assemblyName,
                            GetContainer, GetLocation, options, matcher, hits);
                        break;
                    case TypeReference target:
                        if (options.Kinds.Includes(HitKind.Type) || options.Kinds.Includes(HitKind.Namespace))
                        {
                            SearchTypeReferences([target], instruction, file, assemblyName,
                                GetContainer, GetLocation, options, matcher, hits);
                        }

                        break;
                    case Mono.Cecil.CallSite callSite:
                        if (options.Kinds.Includes(HitKind.Type) || options.Kinds.Includes(HitKind.Namespace))
                        {
                            SearchTypeReferences(
                                callSite.Parameters.Select(parameter => parameter.ParameterType).Prepend(callSite.ReturnType),
                                instruction, file, assemblyName, GetContainer, GetLocation, options, matcher, hits);
                        }

                        break;
                }
            }
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
        ResolutionDiagnostics resolutionDiagnostics)
    {
        if (options.Kinds.Includes(HitKind.Method) ||
            options.Kinds.Includes(HitKind.Property) ||
            options.Kinds.Includes(HitKind.Event))
        {
            foreach (var candidate in ResolveMemberCandidates(target, options, file, resolutionDiagnostics))
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
            var referencedTypes = new List<TypeReference>(target.Parameters.Count + 4)
            {
                target.DeclaringType,
                target.ReturnType,
            };
            referencedTypes.AddRange(target.Parameters.Select(parameter => parameter.ParameterType));
            if (target is GenericInstanceMethod genericMethod)
            {
                referencedTypes.AddRange(genericMethod.GenericArguments);
            }

            SearchTypeReferences(referencedTypes, instruction, file, assemblyName,
                getContainer, getLocation, options, matcher, hits);
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
        SearchHitCollector hits)
    {
        var symbol = CecilFormatting.Field(target);
        if (options.Kinds.Includes(HitKind.Field) && matcher.IsMatch(
                target.Name,
                CecilFormatting.MemberName(target.DeclaringType, target.Name),
                target.FullName,
                symbol))
        {
            Add(hits, file, assemblyName, HitScope.Reference, HitKind.Field,
                symbol, getContainer, getLocation, instruction.Offset);
        }

        if (options.Kinds.Includes(HitKind.Type) || options.Kinds.Includes(HitKind.Namespace))
        {
            SearchTypeReferences([target.DeclaringType, target.FieldType], instruction, file, assemblyName,
                getContainer, getLocation, options, matcher, hits);
        }
    }

    private static void SearchTypeReferences(
        IEnumerable<TypeReference> roots,
        Instruction instruction,
        string file,
        string assemblyName,
        Func<string> getContainer,
        Func<SourceLocation?> getLocation,
        SearchOptions options,
        SearchMatcher matcher,
        SearchHitCollector hits)
    {
        if (!options.Kinds.Includes(HitKind.Type) && !options.Kinds.Includes(HitKind.Namespace))
        {
            return;
        }

        var targets = ExpandTypeReferences(roots)
            .Where(target => target is not GenericParameter)
            .ToArray();
        var scopedNames = targets
            .GroupBy(CecilFormatting.Type, StringComparer.Ordinal)
            .Where(group => group.Select(CecilFormatting.TypeIdentity).Distinct(StringComparer.Ordinal).Skip(1).Any())
            .Select(group => group.Key)
            .ToHashSet(StringComparer.Ordinal);
        var seenTypes = new HashSet<string>(StringComparer.Ordinal);
        var seenNamespaces = new HashSet<string>(StringComparer.Ordinal);
        foreach (var target in targets)
        {
            var unscopedSymbol = CecilFormatting.Type(target);
            var identity = CecilFormatting.TypeIdentity(target);
            var symbol = scopedNames.Contains(unscopedSymbol) ? identity : unscopedSymbol;
            if (options.Kinds.Includes(HitKind.Type) &&
                seenTypes.Add(identity) &&
                matcher.IsMatch(target.Name, target.FullName, unscopedSymbol, symbol))
            {
                Add(hits, file, assemblyName, HitScope.Reference, HitKind.Type,
                    symbol, getContainer, getLocation, instruction.Offset);
            }

            if (options.Kinds.Includes(HitKind.Namespace) &&
                !string.IsNullOrEmpty(target.Namespace) &&
                seenNamespaces.Add(target.Namespace) &&
                matcher.IsMatch(target.Namespace))
            {
                Add(hits, file, assemblyName, HitScope.Reference, HitKind.Namespace,
                    target.Namespace, getContainer, getLocation, instruction.Offset);
            }
        }
    }

    private static IEnumerable<TypeReference> ExpandTypeReferences(IEnumerable<TypeReference> roots)
    {
        var stack = new Stack<TypeReference>(roots.Reverse());
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
                    PushReverse(stack, functionPointer.Parameters.Select(parameter => parameter.ParameterType));
                    stack.Push(functionPointer.ReturnType);
                    break;
                case TypeSpecification specification:
                    stack.Push(specification.ElementType);
                    break;
            }
        }
    }

    private static IReadOnlyList<MemberCandidate> ResolveMemberCandidates(
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
    private static IReadOnlyList<MemberCandidate> UnresolvedCandidates(MethodReference method, SearchOptions options)
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

    private static void PushReverse(Stack<TypeReference> stack, IEnumerable<TypeReference> types)
    {
        foreach (var type in types.Reverse())
        {
            stack.Push(type);
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

    private static SymbolMode EffectiveSymbolMode(SearchOptions options)
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
