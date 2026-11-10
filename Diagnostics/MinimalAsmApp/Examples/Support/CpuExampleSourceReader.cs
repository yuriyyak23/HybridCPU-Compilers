namespace MinimalAsmApp.Examples.Support;

internal static class CpuExampleSourceReader
{
    public static string ReadRunMethod(Type exampleType)
    {
        string? sourcePath = FindSourceFile(exampleType.Name + ".cs");
        if (sourcePath is null)
        {
            return $"Source file for {exampleType.Name} is unavailable.";
        }

        string source = File.ReadAllText(sourcePath);
        return ExtractRunMethod(source) ?? NormalizeIndent(source);
    }

    private static string? FindSourceFile(string fileName)
    {
        string? examplesRoot = FindExamplesRoot(Directory.GetCurrentDirectory()) ??
            FindExamplesRoot(AppContext.BaseDirectory);

        return examplesRoot is null
            ? null
            : Directory.EnumerateFiles(examplesRoot, fileName, SearchOption.AllDirectories)
                .FirstOrDefault();
    }

    private static string? FindExamplesRoot(string startPath)
    {
        DirectoryInfo? directory = new(startPath);
        while (directory is not null)
        {
            string nestedExamples = Path.Combine(directory.FullName, "MinimalAsmApp", "Examples");
            if (Directory.Exists(nestedExamples))
            {
                return nestedExamples;
            }

            string localExamples = Path.Combine(directory.FullName, "Examples");
            if (Directory.Exists(localExamples))
            {
                return localExamples;
            }

            directory = directory.Parent;
        }

        return null;
    }

    private static string? ExtractRunMethod(string source)
    {
        const string signature = "public CpuExampleResult Run()";

        int signatureIndex = source.IndexOf(signature, StringComparison.Ordinal);
        if (signatureIndex < 0)
        {
            return null;
        }

        int braceStart = source.IndexOf('{', signatureIndex);
        if (braceStart < 0)
        {
            return null;
        }

        int depth = 0;
        for (int index = braceStart; index < source.Length; index++)
        {
            if (source[index] == '{')
            {
                depth++;
            }
            else if (source[index] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    int lineStart = source.LastIndexOf('\n', signatureIndex);
                    lineStart = lineStart < 0 ? 0 : lineStart + 1;

                    return NormalizeIndent(source[lineStart..(index + 1)]);
                }
            }
        }

        return null;
    }

    private static string NormalizeIndent(string source)
    {
        string[] lines = source.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        int firstLine = 0;
        while (firstLine < lines.Length && string.IsNullOrWhiteSpace(lines[firstLine]))
        {
            firstLine++;
        }

        int lastLine = lines.Length - 1;
        while (lastLine >= firstLine && string.IsNullOrWhiteSpace(lines[lastLine]))
        {
            lastLine--;
        }

        if (firstLine > lastLine)
        {
            return string.Empty;
        }

        int minIndent = int.MaxValue;
        for (int index = firstLine; index <= lastLine; index++)
        {
            string line = lines[index];
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            int indent = 0;
            while (indent < line.Length && char.IsWhiteSpace(line[indent]))
            {
                indent++;
            }

            minIndent = Math.Min(minIndent, indent);
        }

        IEnumerable<string> normalized = lines[firstLine..(lastLine + 1)]
            .Select(line => line.Length >= minIndent ? line[minIndent..] : line);

        return string.Join(Environment.NewLine, normalized);
    }
}
