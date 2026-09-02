using CecilInspector.Cli;
using Mono.Cecil;
using Mono.Cecil.Cil;
using System.Runtime.ExceptionServices;

namespace CecilInspector.Core;

internal static class CecilModuleReader
{
    public static ModuleDefinition Read(string filePath, SymbolMode symbolMode, IAssemblyResolver resolver)
    {
        if (symbolMode == SymbolMode.Off)
        {
            return ReadCore(filePath, false, false, resolver);
        }

        try
        {
            var module = ReadCore(filePath, true, symbolMode == SymbolMode.Required, resolver);
            if (symbolMode == SymbolMode.Required && !module.HasSymbols)
            {
                module.Dispose();
                throw new SymbolsNotFoundException($"PDBが見つかりません: {filePath}");
            }

            return module;
        }
        catch (Exception firstException) when (
            symbolMode == SymbolMode.Auto && !ExceptionPolicy.IsFatal(firstException))
        {
            try
            {
                // The no-symbol read is also the discriminator: only an error caused by symbol
                // processing can succeed here. If the assembly itself is invalid, preserve the
                // original exception instead of replacing it with the retry's exception.
                return ReadCore(filePath, false, false, resolver);
            }
            catch (Exception retryException) when (!ExceptionPolicy.IsFatal(retryException))
            {
                ExceptionDispatchInfo.Capture(firstException).Throw();
                throw;
            }
        }
    }

    private static ModuleDefinition ReadCore(
        string filePath,
        bool readSymbols,
        bool throwIfNoSymbol,
        IAssemblyResolver resolver)
    {
        var parameters = new ReaderParameters
        {
            AssemblyResolver = resolver,
            InMemory = true,
            ReadingMode = ReadingMode.Immediate,
            ReadSymbols = readSymbols,
            ThrowIfSymbolsAreNotMatching = throwIfNoSymbol,
        };

        if (readSymbols)
        {
            parameters.SymbolReaderProvider = new DefaultSymbolReaderProvider(throwIfNoSymbol);
        }

        return ModuleDefinition.ReadModule(filePath, parameters);
    }
}
