using Mono.Cecil;
using Mono.Cecil.Cil;

namespace CecilInspector.Core;

internal static class DebugLocations
{
    private const int HiddenLine = 0xFEEFEE;

    public static SourceLocation? First(MethodDefinition? method)
    {
        if (method is null)
        {
            return null;
        }

        var direct = FirstDirect(method);
        if (direct is not null)
        {
            return direct;
        }

        var moveNext = ResolveStateMachineMoveNext(method);
        return moveNext is null ? null : FirstDirect(moveNext);
    }

    public static MethodDefinition DisplayMethod(MethodDefinition method) =>
        method.DebugInformation.StateMachineKickOffMethod ?? method;

    public static SequencePointMapper CreateMapper(MethodDefinition method) => new(method);

    internal static bool IsVisible(SequencePoint sequencePoint) =>
        sequencePoint.StartLine != HiddenLine && sequencePoint.StartLine > 0 && sequencePoint.Document is not null;

    internal static SourceLocation ToLocation(SequencePoint sequencePoint) =>
        new(sequencePoint.Document.Url, sequencePoint.StartLine, sequencePoint.StartColumn);

    private static SourceLocation? FirstDirect(MethodDefinition method)
    {
        if (!method.DebugInformation.HasSequencePoints)
        {
            return null;
        }

        return method.DebugInformation.SequencePoints
            .Where(IsVisible)
            .Select(ToLocation)
            .FirstOrDefault();
    }

    private static MethodDefinition? ResolveStateMachineMoveNext(MethodDefinition method)
    {
        try
        {
            var attribute = method.CustomAttributes.FirstOrDefault(candidate =>
                candidate.AttributeType.FullName is
                    "System.Runtime.CompilerServices.AsyncStateMachineAttribute" or
                    "System.Runtime.CompilerServices.IteratorStateMachineAttribute");
            if (attribute is null || attribute.ConstructorArguments.Count == 0 ||
                attribute.ConstructorArguments[0].Value is not TypeReference stateMachineType)
            {
                return null;
            }

            return stateMachineType.Resolve()?.Methods.FirstOrDefault(candidate => candidate.Name == "MoveNext");
        }
        catch (Exception ex) when (ExceptionPolicy.IsRecoverableAssemblyError(ex))
        {
            // Decoding the attribute or resolving the state machine needs dependencies that
            // may be missing; a definition without a location is better than a failed file.
            return null;
        }
    }
}

internal sealed class SequencePointMapper
{
    private readonly SequencePoint[] _sequencePoints;
    private int _cursor = -1;
    private int _lastInstructionOffset = -1;

    public SequencePointMapper(MethodDefinition method)
    {
        _sequencePoints = method.DebugInformation.HasSequencePoints
            ? method.DebugInformation.SequencePoints.Where(DebugLocations.IsVisible).OrderBy(point => point.Offset).ToArray()
            : [];
    }

    public SourceLocation? ForInstruction(Instruction instruction)
    {
        if (_sequencePoints.Length == 0)
        {
            return null;
        }

        if (instruction.Offset < _lastInstructionOffset)
        {
            _cursor = FindLastAtOrBefore(instruction.Offset);
        }
        else
        {
            while (_cursor + 1 < _sequencePoints.Length &&
                   _sequencePoints[_cursor + 1].Offset <= instruction.Offset)
            {
                _cursor++;
            }
        }

        _lastInstructionOffset = instruction.Offset;
        return _cursor < 0 ? null : DebugLocations.ToLocation(_sequencePoints[_cursor]);
    }

    private int FindLastAtOrBefore(int offset)
    {
        var low = 0;
        var high = _sequencePoints.Length - 1;
        var result = -1;
        while (low <= high)
        {
            var middle = low + ((high - low) / 2);
            if (_sequencePoints[middle].Offset <= offset)
            {
                result = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return result;
    }
}
