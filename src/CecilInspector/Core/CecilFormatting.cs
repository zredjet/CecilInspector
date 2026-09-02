using Mono.Cecil;
using System.Runtime.CompilerServices;

namespace CecilInspector.Core;

internal static class CecilFormatting
{
    private static readonly ConditionalWeakTable<TypeDefinition, CollisionInfo> CollisionCache = new();

    public static string Type(TypeReference type) => type switch
    {
        GenericParameter parameter => parameter.Type == GenericParameterType.Method
            ? $"!!{parameter.Position}"
            : $"!{parameter.Position}",
        _ => type.FullName.Replace('/', '+'),
    };

    public static string Method(MethodReference method)
    {
        var qualifyScopes = method is MethodDefinition definition && HasUnqualifiedCollision(definition);
        return FormatMethod(method, qualifyScopes ? ScopedType : type => ContextualType(type, method));
    }

    public static string Property(PropertyDefinition property)
    {
        var parameters = FormatPropertyParameters(property.Parameters.Select(parameter => parameter.ParameterType));
        return $"{Type(property.DeclaringType)}::{property.Name}{parameters} : {Type(property.PropertyType)}";
    }

    public static string Property(PropertyDefinition property, MethodReference accessor)
    {
        var parameters = FormatPropertyParameters(
            property.Parameters.Select(parameter => ContextualType(parameter.ParameterType, accessor)));
        return $"{Type(accessor.DeclaringType)}::{property.Name}{parameters} : " +
               ContextualType(property.PropertyType, accessor);
    }

    public static string Property(MethodReference accessor)
    {
        var name = StripAccessorPrefix(accessor.Name);
        var propertyType = accessor.Name.StartsWith("set_", StringComparison.Ordinal) && accessor.Parameters.Count > 0
            ? accessor.Parameters[^1].ParameterType
            : accessor.ReturnType;
        var indexParameterCount = accessor.Name.StartsWith("set_", StringComparison.Ordinal)
            ? Math.Max(0, accessor.Parameters.Count - 1)
            : accessor.Parameters.Count;
        var indexParameters = accessor.Parameters.Take(indexParameterCount).Select(parameter => parameter.ParameterType);
        return $"{Type(accessor.DeclaringType)}::{name}{FormatPropertyParameters(indexParameters)} : {Type(propertyType)}";
    }

    public static string Field(FieldReference field) =>
        $"{Type(field.DeclaringType)}::{field.Name} : {Type(field.FieldType)}";

    public static string MemberName(TypeReference declaringType, string name) =>
        $"{Type(declaringType)}::{name}";

    public static string Event(EventDefinition @event) =>
        $"{Type(@event.DeclaringType)}::{@event.Name} : {Type(@event.EventType)}";

    public static string Event(EventDefinition @event, MethodReference accessor) =>
        $"{Type(accessor.DeclaringType)}::{@event.Name} : {ContextualType(@event.EventType, accessor)}";

    public static string Event(MethodReference accessor)
    {
        var eventType = accessor.Parameters.Count > 0 ? $" : {Type(accessor.Parameters[0].ParameterType)}" : string.Empty;
        return $"{Type(accessor.DeclaringType)}::{StripAccessorPrefix(accessor.Name)}{eventType}";
    }

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

    private static string MethodName(MethodReference method, Func<TypeReference, string> formatType)
    {
        var genericArity = method.GenericParameters.Count;
        if (genericArity == 0 && method is GenericInstanceMethod genericInstance)
        {
            genericArity = genericInstance.ElementMethod.GenericParameters.Count;
        }

        var name = genericArity > 0 ? $"{method.Name}`{genericArity}" : method.Name;
        if (method is GenericInstanceMethod instance && instance.GenericArguments.Count > 0)
        {
            name += $"<{string.Join(", ", instance.GenericArguments.Select(formatType))}>";
        }

        return name;
    }

    private static string FormatPropertyParameters(IEnumerable<TypeReference> parameterTypes)
    {
        var parameters = parameterTypes.Select(Type).ToArray();
        return parameters.Length == 0 ? string.Empty : $"({string.Join(", ", parameters)})";
    }

    private static string FormatPropertyParameters(IEnumerable<string> parameterTypes)
    {
        var parameters = parameterTypes.ToArray();
        return parameters.Length == 0 ? string.Empty : $"({string.Join(", ", parameters)})";
    }

    private static string FormatMethod(MethodReference method, Func<TypeReference, string> formatType)
    {
        var parameters = string.Join(", ", method.Parameters.Select(parameter => formatType(parameter.ParameterType)));
        return $"{Type(method.DeclaringType)}::{MethodName(method, formatType)}({parameters}) : {formatType(method.ReturnType)}";
    }

    private static bool HasUnqualifiedCollision(MethodDefinition method)
    {
        var collisions = CollisionCache.GetValue(method.DeclaringType, static type => new CollisionInfo(type));
        return collisions.Methods.Contains(method);
    }

    private static string ContextualType(TypeReference type, MethodReference context)
    {
        if (type is GenericParameter parameter)
        {
            if (parameter.Type == GenericParameterType.Method &&
                context is GenericInstanceMethod methodInstance &&
                parameter.Position < methodInstance.GenericArguments.Count)
            {
                var argument = methodInstance.GenericArguments[parameter.Position];
                return IsSameParameter(parameter, argument) ? Type(parameter) : ContextualType(argument, context);
            }

            if (parameter.Type == GenericParameterType.Type &&
                context.DeclaringType is GenericInstanceType typeInstance &&
                parameter.Position < typeInstance.GenericArguments.Count)
            {
                var argument = typeInstance.GenericArguments[parameter.Position];
                return IsSameParameter(parameter, argument) ? Type(parameter) : ContextualType(argument, context);
            }

            return Type(parameter);
        }

        return type switch
        {
            ArrayType array => FormatArray(array, child => ContextualType(child, context)),
            ByReferenceType reference => $"{ContextualType(reference.ElementType, context)}&",
            PointerType pointer => $"{ContextualType(pointer.ElementType, context)}*",
            OptionalModifierType modifier =>
                $"{ContextualType(modifier.ElementType, context)} modopt({ContextualType(modifier.ModifierType, context)})",
            RequiredModifierType modifier =>
                $"{ContextualType(modifier.ElementType, context)} modreq({ContextualType(modifier.ModifierType, context)})",
            SentinelType sentinel => $"{ContextualType(sentinel.ElementType, context)} sentinel",
            PinnedType pinned => $"{ContextualType(pinned.ElementType, context)} pinned",
            FunctionPointerType functionPointer => FormatFunctionPointer(
                functionPointer,
                child => ContextualType(child, context)),
            GenericInstanceType generic =>
                $"{Type(generic.ElementType)}<{string.Join(", ", generic.GenericArguments.Select(argument => ContextualType(argument, context)))}>",
            _ => Type(type),
        };
    }

    private static bool IsSameParameter(GenericParameter parameter, TypeReference candidate) =>
        candidate is GenericParameter other &&
        other.Type == parameter.Type &&
        other.Position == parameter.Position;

    private static string ScopedType(TypeReference type)
    {
        if (type is GenericParameter)
        {
            return Type(type);
        }

        if (type is GenericInstanceType generic)
        {
            return $"{Type(generic.ElementType)}@{ScopeIdentity(generic.ElementType)}" +
                   $"<{string.Join(", ", generic.GenericArguments.Select(ScopedType))}>";
        }

        if (type is ArrayType array)
        {
            return FormatArray(array, ScopedType);
        }

        if (type is ByReferenceType reference)
        {
            return $"{ScopedType(reference.ElementType)}&";
        }

        if (type is PointerType pointer)
        {
            return $"{ScopedType(pointer.ElementType)}*";
        }

        if (type is OptionalModifierType optionalModifier)
        {
            return $"{ScopedType(optionalModifier.ElementType)} modopt({ScopedType(optionalModifier.ModifierType)})";
        }

        if (type is RequiredModifierType requiredModifier)
        {
            return $"{ScopedType(requiredModifier.ElementType)} modreq({ScopedType(requiredModifier.ModifierType)})";
        }

        if (type is SentinelType sentinel)
        {
            return $"{ScopedType(sentinel.ElementType)} sentinel";
        }

        if (type is PinnedType pinned)
        {
            return $"{ScopedType(pinned.ElementType)} pinned";
        }

        if (type is FunctionPointerType functionPointer)
        {
            return FormatFunctionPointer(functionPointer, ScopedType);
        }

        return $"{Type(type)}@{ScopeIdentity(type)}";
    }

    private static string FormatArray(ArrayType array, Func<TypeReference, string> formatType)
    {
        if (array.IsVector)
        {
            return $"{formatType(array.ElementType)}[]";
        }

        return $"{formatType(array.ElementType)}[{string.Join(",", array.Dimensions)}]";
    }

    private static string FormatFunctionPointer(
        FunctionPointerType functionPointer,
        Func<TypeReference, string> formatType)
    {
        var parameters = string.Join(", ", functionPointer.Parameters.Select(parameter => formatType(parameter.ParameterType)));
        return $"method {formatType(functionPointer.ReturnType)} *({parameters})";
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
                .GroupBy(method => FormatMethod(method, Type), StringComparer.Ordinal)
                .Where(group => group.Count() > 1 &&
                                group.Select(method => FormatMethod(method, ScopedType))
                                    .Distinct(StringComparer.Ordinal)
                                    .Skip(1)
                                    .Any())
                .SelectMany(group => group)
                .ToHashSet();
        }

        public HashSet<MethodDefinition> Methods { get; }
    }
}
