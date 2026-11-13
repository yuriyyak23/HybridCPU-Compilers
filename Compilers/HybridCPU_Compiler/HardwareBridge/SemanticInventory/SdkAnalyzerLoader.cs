#if HARDWAREBRIDGE_INVENTORY
using System;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;

internal sealed class SdkAnalyzerLoader : IAnalyzerAssemblyLoader
{
    public void AddDependencyLocation(string fullPath) { }
    public Assembly LoadFromPath(string fullPath) => AssemblyLoadContext.Default.LoadFromAssemblyPath(fullPath);
}
#endif
