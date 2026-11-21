using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace HybridCPU_ISE.Tests.TestHelpers;

internal static class CompilerSourceScanner
{
    private static readonly string[] CompilerRootRelativePaths =
    [
        Path.Combine("Compilers", "HybridCPU_Compiler"),
        Path.Combine("HybridCPU_ISE", "Compiler")
    ];

    private static readonly string[] CompilerEmissionSurfaceRootRelativePaths =
    [
        Path.Combine("Compilers", "HybridCPU_Compiler", "API"),
        Path.Combine("Compilers", "HybridCPU_Compiler", "Core", "IR", "Construction"),
        Path.Combine("Compilers", "HybridCPU_Compiler", "Core", "IR", "Bundling")
    ];

    internal static IReadOnlyList<string> EnumerateCompilerSourceFiles() =>
        EnumerateSourceFiles(CompilerRootRelativePaths, "compiler source");

    internal static string ReadAllCompilerSource() =>
        ReadSourceFiles(EnumerateCompilerSourceFiles());

    internal static IReadOnlyList<string> EnumerateCompilerEmissionSurfaceSourceFiles() =>
        EnumerateSourceFiles(CompilerEmissionSurfaceRootRelativePaths, "compiler emission surface");

    internal static string ReadCompilerEmissionSurfaceSource() =>
        ReadSourceFiles(EnumerateCompilerEmissionSurfaceSourceFiles());

    private static IReadOnlyList<string> EnumerateSourceFiles(
        IReadOnlyList<string> relativeRoots,
        string scannerName)
    {
        string repoRoot = CompatFreezeScanner.FindRepoRoot();
        string[] files = relativeRoots
            .Select(relativeRoot => Path.Combine(relativeRoot.StartsWith("Compilers" + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? CompatFreezeScanner.FindCompilerRepoRoot() : repoRoot, relativeRoot))
            .Where(Directory.Exists)
            .SelectMany(EnumerateRootSources)
            .Where(static path => !CompatFreezeScanner.IsGeneratedPath(path))
            .OrderBy(static path => path, StringComparer.Ordinal)
            .ToArray();

        if (files.Length == 0)
        {
            throw new DirectoryNotFoundException(
                $"No {scannerName} files were found under the expected compiler roots.");
        }

        return files;
    }


    private static IEnumerable<string> EnumerateRootSources(string root)
    {
        if (!Path.GetFileName(root).Equals("HybridCPU_Compiler", StringComparison.OrdinalIgnoreCase))
            return Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories);

        // The compiler project excludes these separately built hardware tools and fixtures.
        return Directory.EnumerateFiles(root, "*.cs", SearchOption.TopDirectoryOnly)
            .Concat(Directory.EnumerateDirectories(root)
                .Where(static path => Path.GetFileName(path) is not ("HardwareBridge" or "HardwareBridge_intel8080"))
                .SelectMany(static path => Directory.EnumerateFiles(path, "*.cs", SearchOption.AllDirectories)));
    }

    private static string ReadSourceFiles(IEnumerable<string> files) =>
        string.Join(Environment.NewLine, files.Select(File.ReadAllText));
}
