using Mono.Cecil;
using System.Runtime.CompilerServices;

namespace CecilInspector.Core;

internal static class CecilFormatting
{
    private static readonly ConditionalWeakTable<TypeDefinition, CollisionInfo> CollisionCache = new();
    private static readonly Func<TypeReference, string> PlainLeafFormatter = PlainLeaf;
    private static readonly Func<TypeReference, string> ScopedLeafFormatter = ScopedLeaf;

    /// <summary>
    /// Canonical display form: nested types use '+', generic arguments are joined with ", ",
    /// and generic parameters are rendered positionally as !n (type) or !!n (method).
    /// </summary>
    public static string Type(TypeReference type) => Format(type, PlainLeafFormatter);

    public static string Method(MethodReference method)
    {
        var qualifyScopes = method is MethodDefinition definition && HasUnqualifiedCollision(definition);
        return FormatMethod(method, qualifyScopes ? ScopedLeafFormatter : ContextualFormatter(method));
    }

    public static string Property(PropertyDefinition property)
    {
        var parameters = FormatPropertyParameters(property.Parameters.Select(parameter => Type(parameter.ParameterType)));
        return $"{Type(property.DeclaringType)}::{property.Name}{parameters} : {Type(property.PropertyType)}";
    }

    public static string Property(PropertyDefinition property, MethodReference accessor)
    {
        var parameters = FormatPropertyParameters(
            property.Parameters.Select(parameter => ContextualType(parameter.ParameterType, accessor)));
        return $"{Type(accessor.DeclaringType)}::{property.Name}{parameters} : " +
               ContextualType(property.PropertyType, accessor);
    }

    /// <summary>
    /// Property symbol synthesized from an unresolvable accessor reference (get_/set_).
    /// </summary>
    public static string Property(MethodReference accessor)
    {
        var name = StripAccessorPrefix(accessor.Name);
        var isSetter = accessor.Name.StartsWith("set_", StringComparison.Ordinal);
        var propertyType = isSetter && accessor.Parameters.Count > 0
            ? accessor.Parameters[^1].ParameterType
            : accessor.ReturnType;
        var indexParameterCount = isSetter
            ? Math.Max(0, accessor.Parameters.Count - 1)
            : accessor.Parameters.Count;
        var indexParameters = accessor.Parameters
            .Take(indexParameterCount)
            .Select(parameter => ContextualType(parameter.ParameterType, accessor));
        return $"{Type(accessor.DeclaringType)}::{name}{FormatPropertyParameters(indexParameters)} : " +
               ContextualType(propertyType, accessor);
    }

    public static string Field(FieldReference field) =>
        $"{Type(field.DeclaringType)}::{field.Name} : {Type(field.FieldType)}";

    public static string MemberName(TypeReference declaringType, string name) =>
        $"{Type(declaringType)}::{name}";

    public static string Event(EventDefinition @event) =>
        $"{Type(@event.DeclaringType)}::{@event.Name} : {Type(@event.EventType)}";

    public static string Event(EventDefinition @event, MethodReference accessor) =>
        $"{Type(accessor.DeclaringType)}::{@event.Name} : {ContextualType(@event.EventType, accessor)}";

    /// <summary>
    /// Event symbol synthesized from an unresolvable accessor reference (add_/remove_/raise_).
    /// </summary>
    public static string Event(MethodReference accessor)
    {
        var eventType = accessor.Parameters.Count > 0
            ? $" : {ContextualType(accessor.Parameters[0].ParameterType, accessor)}"
            : string.Empty;
        return $"{Type(accessor.DeclaringType)}::{StripAccessorPrefix(accessor.Name)}{eventType}";
    }

    public static bool IsPropertyAccessorName(string name) =>
        name.StartsWith("get_", StringComparison.Ordinal) || name.StartsWith("set_", StringComparison.Ordinal);

    public static bool IsEventAccessorName(string name) =>
        name.StartsWith("add_", StringComparison.Ordinal) ||
        name.StartsWith("remove_", StringComparison.Ordinal) ||
        name.StartsWith("raise_", StringComparison.Ordinal);

    public static string StripAccessorPrefix(string name)
    {
        foreach (var prefix in new[] { "get_", "set_", "add_", "remove_", "raise_" })
        {
            if (name.StartsWith(prefix, StringComparison.Ordinal))
            {
                return name[prefix.Length..];
            }
        }

        return name;
    }

    public static string LogicalMemberName(string name)
    {
        var separator = name.LastIndexOf('.');
        var simpleName = separator >= 0 ? name[(separator + 1)..] : name;
        return StripAccessorPrefix(simpleName);
    }

    public static string ExplicitMemberName(string name)
    {
        var separator = name.LastIndexOf('.');
        return separator >= 0 ? name[(separator + 1)..] : name;
    }

    public static string TypeIdentity(TypeReference type) => ScopedType(type);

    private static string MethodName(MethodReference method)
    {
        var genericArity = method.GenericParameters.Count;
        if (genericArity == 0 && method is GenericInstanceMethod genericInstance)
        {
            genericArity = genericInstance.ElementMethod.GenericParameters.Count;
        }

        var name = genericArity > 0 ? $"{method.Name}`{genericArity}" : method.Name;
        if (method is GenericInstanceMethod instance && instance.GenericArguments.Count > 0)
        {
            // Generic arguments already belong to the caller's context: format them
            // structurally and never substitute them through the callee's parameters.
            name += $"<{string.Join(", ", instance.GenericArguments.Select(Type))}>";
        }

        return name;
    }

    private static string FormatPropertyParameters(IEnumerable<string> parameterTypes)
    {
        var parameters = parameterTypes.ToArray();
        return parameters.Length == 0 ? string.Empty : $"({string.Join(", ", parameters)})";
    }

    private static string FormatMethod(MethodReference method, Func<TypeReference, string> leaf)
    {
        var parameters = string.Join(", ", method.Parameters.Select(parameter => Format(parameter.ParameterType, leaf)));
        return $"{Type(method.DeclaringType)}::{MethodName(method)}({parameters}) : {Format(method.ReturnType, leaf)}";
    }

    private static bool HasUnqualifiedCollision(MethodDefinition method)
    {
        var collisions = CollisionCache.GetValue(method.DeclaringType, static type => new CollisionInfo(type));
        return collisions.Methods.Contains(method);
    }

    /// <summary>
    /// Single recursive core shared by every type formatter. Structural nodes (arrays, byref,
    /// pointers, modifiers, function pointers, generic instances) are rendered here; the
    /// <paramref name="leaf"/> strategy decides how named types and generic parameters appear.
    /// </summary>
    private static string Format(TypeReference type, Func<TypeReference, string> leaf) => type switch
    {
        ArrayType array => FormatArray(array, leaf),
        ByReferenceType reference => $"{Format(reference.ElementType, leaf)}&",
        PointerType pointer => $"{Format(pointer.ElementType, leaf)}*",
        OptionalModifierType modifier =>
            $"{Format(modifier.ElementType, leaf)} modopt({Format(modifier.ModifierType, leaf)})",
        RequiredModifierType modifier =>
            $"{Format(modifier.ElementType, leaf)} modreq({Format(modifier.ModifierType, leaf)})",
        SentinelType sentinel => $"{Format(sentinel.ElementType, leaf)} sentinel",
        PinnedType pinned => $"{Format(pinned.ElementType, leaf)} pinned",
        FunctionPointerType functionPointer => FormatFunctionPointer(functionPointer, leaf),
        GenericInstanceType generic =>
            $"{leaf(generic.ElementType)}<{string.Join(", ", generic.GenericArguments.Select(argument => Format(argument, leaf)))}>",
        _ => leaf(type),
    };

    private static string PlainLeaf(TypeReference type) => type switch
    {
        GenericParameter parameter => parameter.Type == GenericParameterType.Method
            ? $"!!{parameter.Position}"
            : $"!{parameter.Position}",
        _ => type.FullName.Replace('/', '+'),
    };

    private static string ScopedLeaf(TypeReference type) =>
        type is GenericParameter ? PlainLeaf(type) : $"{PlainLeaf(type)}@{ScopeIdentity(type)}";

    private static string ScopedType(TypeReference type) => Format(type, ScopedLeafFormatter);

    private static Func<TypeReference, string> ContextualFormatter(MethodReference context) =>
        leaf => ContextualLeaf(leaf, context);

    private static string ContextualType(TypeReference type, MethodReference context) =>
        Format(type, ContextualFormatter(context));

    /// <summary>
    /// Substitutes a generic parameter of the referenced member with the argument supplied by
    /// the reference. The argument lives in the caller's context, so it is formatted with
    /// <see cref="Type"/> and never substituted again; re-substitution can cycle
    /// (e.g. Swap&lt;B, A&gt;::M(Swap&lt;!1, !0&gt;)) and previously overflowed the stack.
    /// </summary>
    private static string ContextualLeaf(TypeReference type, MethodReference context) =>
        type is GenericParameter parameter && TryGetGenericArgument(parameter, context, out var argument)
            ? Type(argument)
            : PlainLeaf(type);

    private static bool TryGetGenericArgument(
        GenericParameter parameter,
        MethodReference context,
        out TypeReference argument)
    {
        if (parameter.Type == GenericParameterType.Method &&
            context is GenericInstanceMethod methodInstance &&
            parameter.Position < methodInstance.GenericArguments.Count)
        {
            argument = methodInstance.GenericArguments[parameter.Position];
            return true;
        }

        if (parameter.Type == GenericParameterType.Type &&
            context.DeclaringType is GenericInstanceType typeInstance &&
            parameter.Position < typeInstance.GenericArguments.Count)
        {
            argument = typeInstance.GenericArguments[parameter.Position];
            return true;
        }

        argument = null!;
        return false;
    }

    private static string FormatArray(ArrayType array, Func<TypeReference, string> leaf)
    {
        var element = Format(array.ElementType, leaf);
        return array.IsVector ? $"{element}[]" : $"{element}[{string.Join(",", array.Dimensions)}]";
    }

    private static string FormatFunctionPointer(FunctionPointerType functionPointer, Func<TypeReference, string> leaf)
    {
        var parameters = string.Join(", ",
            functionPointer.Parameters.Select(parameter => Format(parameter.ParameterType, leaf)));
        return $"method {Format(functionPointer.ReturnType, leaf)} *({parameters})";
    }

    private static string ScopeIdentity(TypeReference type) => type.Scope switch
    {
        AssemblyNameReference assembly => assembly.FullName,
        ModuleDefinition module when module.Assembly is not null => module.Assembly.Name.FullName,
        ModuleDefinition module => module.Name,
        _ => type.Scope?.Name ?? "unknown",
    };

    private sealed class CollisionInfo
    {
        public CollisionInfo(TypeDefinition type)
        {
            Methods = type.Methods
                .GroupBy(method => FormatMethod(method, PlainLeafFormatter), StringComparer.Ordinal)
                .Where(group => group.Count() > 1 &&
                                group.Select(method => FormatMethod(method, ScopedLeafFormatter))
                                    .Distinct(StringComparer.Ordinal)
                                    .Skip(1)
                                    .Any())
                .SelectMany(group => group)
                .ToHashSet();
        }

        public HashSet<MethodDefinition> Methods { get; }
    }
}
