using System.Reflection;
using System.Runtime.Loader;

namespace YAKSys_Hybrid_CPU.DiagnosticsConsole;

internal static class CompatAssemblyResolver
{
    private static int _initialized;

    public static void EnsureRegistered()
    {
        if (Interlocked.Exchange(ref _initialized, 1) != 0)
        {
            return;
        }

        AssemblyLoadContext.Default.Resolving += ResolveFromAppBase;
        AppDomain.CurrentDomain.AssemblyResolve += ResolveFromAppDomain;
    }

    private static Assembly? ResolveFromAppBase(AssemblyLoadContext context, AssemblyName assemblyName)
    {
        return TryResolveFromAppBase(assemblyName);
    }

    private static Assembly? ResolveFromAppDomain(object? sender, ResolveEventArgs args)
    {
        return TryResolveFromAppBase(new AssemblyName(args.Name));
    }

    private static Assembly? TryResolveFromAppBase(AssemblyName assemblyName)
    {
        ArgumentNullException.ThrowIfNull(assemblyName);

        if (!string.Equals(assemblyName.Name, "HybridCPU_ISE", StringComparison.Ordinal))
        {
            return null;
        }

        Assembly? alreadyLoaded = AppDomain.CurrentDomain
            .GetAssemblies()
            .FirstOrDefault(assembly =>
                string.Equals(assembly.GetName().Name, assemblyName.Name, StringComparison.Ordinal));
        if (alreadyLoaded is not null)
        {
            return alreadyLoaded;
        }

        string candidatePath = Path.Combine(AppContext.BaseDirectory, "HybridCPU_ISE.dll");
        return File.Exists(candidatePath)
            ? AssemblyLoadContext.Default.LoadFromAssemblyPath(candidatePath)
            : null;
    }
}
