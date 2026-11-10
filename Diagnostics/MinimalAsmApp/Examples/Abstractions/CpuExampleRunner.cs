using MinimalAsmApp.Examples.Support;

namespace MinimalAsmApp.Examples.Abstractions;

public sealed class CpuExampleRunner
{
    private readonly IReadOnlyList<ICpuExample> _examples;

    public CpuExampleRunner(IEnumerable<ICpuExample> examples)
    {
        _examples = examples.ToArray();
        if (_examples.Count == 0)
        {
            throw new ArgumentException("At least one CPU example must be registered.", nameof(examples));
        }
    }

    public int Run(string[] args)
    {
        try
        {
            if (args.Length == 0)
            {
                return RunInteractiveMenu();
            }

            string command = args[0].Trim();
            if (Is(command, "help") || Is(command, "--help") || Is(command, "-h"))
            {
                PrintHelp();
                return 0;
            }

            if (Is(command, "list") || Is(command, "ls"))
            {
                PrintList();
                return 0;
            }

            if (Is(command, "all"))
            {
                return RunAll();
            }

            if (Is(command, "run"))
            {
                if (args.Length < 2)
                {
                    Console.Error.WriteLine("Usage: dotnet run -- run <number|name>");
                    PrintList();
                    return 1;
                }

                return TryRunSelector(args[1]);
            }

            return TryRunSelector(command);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"MinimalAsmApp failed: {ex.Message}");
            return 1;
        }
    }

    private int TryRunSelector(string selector)
    {
        ICpuExample? example = Find(selector);
        if (example is null)
        {
            Console.Error.WriteLine($"Example '{selector}' was not found.");
            PrintList();
            return 1;
        }

        return RunOne(example);
    }

    private int RunInteractiveMenu()
    {
        int lastExitCode = 0;
        string? pendingSelector = null;

        while (true)
        {
            string? selector = pendingSelector;
            pendingSelector = null;

            if (string.IsNullOrWhiteSpace(selector))
            {
                PrintInteractiveHeader();
                selector = ReadSelector("Select example number/name, 'all' to run all, or 'q' to exit: ");
            }

            if (selector is null)
            {
                return lastExitCode;
            }

            selector = selector.Trim();
            if (selector.Length == 0)
            {
                continue;
            }

            if (Is(selector, "q") || Is(selector, "quit") || Is(selector, "exit"))
            {
                return lastExitCode;
            }

            if (Is(selector, "all"))
            {
                lastExitCode = RunAll();
                pendingSelector = ReadNextSelector();
                if (pendingSelector is null)
                {
                    return lastExitCode;
                }

                continue;
            }

            ICpuExample? example = Find(selector);
            if (example is null)
            {
                Console.WriteLine($"Example '{selector}' was not found.");
                continue;
            }

            Console.WriteLine();
            lastExitCode = RunOne(example);
            pendingSelector = ReadNextSelector();
            if (pendingSelector is null)
            {
                return lastExitCode;
            }
        }
    }

    private void PrintInteractiveHeader()
    {
        Console.WriteLine("MinimalAsmApp CPU ISE examples");
        Console.WriteLine();
        PrintList();
        Console.WriteLine();
    }

    private static string? ReadSelector(string prompt)
    {
        Console.Write(prompt);
        return Console.ReadLine();
    }

    private static string? ReadNextSelector()
    {
        Console.WriteLine();
        Console.Write("Press Enter to return to menu, type number/name/all to run another, or q to exit: ");
        string? selector = Console.ReadLine();
        if (selector is null)
        {
            return null;
        }

        selector = selector.Trim();
        return selector.Length == 0 ? string.Empty : selector;
    }

    private int RunAll()
    {
        int failures = 0;
        for (int index = 0; index < _examples.Count; index++)
        {
            ICpuExample example = _examples[index];
            Console.WriteLine($"[{index:00}] {example.Category} / {example.Name}");

            CpuExampleResult result = RunSafely(example);
            Console.WriteLine(result.Success ? "Status: OK" : "Status: FAILED");
            if (!string.IsNullOrWhiteSpace(result.Output))
            {
                Console.WriteLine(result.Output);
            }

            Console.WriteLine();
            if (!result.Success)
            {
                failures++;
            }
        }

        Console.WriteLine(failures == 0
            ? $"All examples completed: {_examples.Count}"
            : $"Examples completed with failures: {failures}/{_examples.Count}");
        return failures == 0 ? 0 : 1;
    }

    private int RunOne(ICpuExample example)
    {
        CpuExampleResult result = RunSafely(example);
        PrintResult(example, result);
        return result.Success ? 0 : 1;
    }

    private static CpuExampleResult RunSafely(ICpuExample example)
    {
        try
        {
            return example.Run();
        }
        catch (Exception ex)
        {
            return CpuExampleResult.Fail(ex.Message);
        }
    }

    private void PrintHelp()
    {
        Console.WriteLine("MinimalAsmApp CPU ISE examples");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.WriteLine("  dotnet run                 Open numbered example menu");
        Console.WriteLine("  dotnet run -- list         List examples");
        Console.WriteLine("  dotnet run -- run <id>     Run one example by number or name");
        Console.WriteLine("  dotnet run -- all          Run all examples");
        Console.WriteLine();
        PrintList();
    }

    private void PrintList()
    {
        Console.WriteLine("Examples:");
        for (int index = 0; index < _examples.Count; index++)
        {
            ICpuExample example = _examples[index];
            Console.WriteLine($"  {index:00}  {example.Name,-28} {example.Category} - {example.Description}");
        }
    }

    private static void PrintResult(ICpuExample example, CpuExampleResult result)
    {
        Console.WriteLine($"{example.Category} / {example.Name}");
        Console.WriteLine();
        Console.WriteLine("Description:");
        Console.WriteLine($"  {example.Description}");
        Console.WriteLine();
        PrintExampleCode(example);
        Console.WriteLine();

        Console.WriteLine("Result:");
        if (!string.IsNullOrWhiteSpace(result.Output))
        {
            Console.WriteLine($"  {result.Output}");
        }

        PrintMap("Registers", result.Registers);
        PrintMap("Memory", result.Memory);
        PrintLines("Trace", result.Trace);
        PrintLines("Notes", result.Notes);

        Console.WriteLine();
        Console.WriteLine(result.Success ? "Status: OK" : "Status: FAILED");
    }

    private static void PrintExampleCode(ICpuExample example)
    {
        Console.WriteLine("Code:");
        string code = CpuExampleSourceReader.ReadRunMethod(example.GetType());
        foreach (string line in SplitLines(code))
        {
            Console.WriteLine($"  {line}");
        }
    }

    private static void PrintMap(string title, IReadOnlyDictionary<string, ulong> values)
    {
        if (values.Count == 0)
        {
            return;
        }

        Console.WriteLine();
        Console.WriteLine($"{title}:");
        foreach (KeyValuePair<string, ulong> item in values)
        {
            Console.WriteLine($"  {item.Key} = {item.Value} (0x{item.Value:X})");
        }
    }

    private static void PrintLines(string title, IReadOnlyList<string> lines)
    {
        if (lines.Count == 0)
        {
            return;
        }

        Console.WriteLine();
        Console.WriteLine($"{title}:");
        foreach (string line in lines)
        {
            Console.WriteLine($"  {line}");
        }
    }

    private static IEnumerable<string> SplitLines(string text)
    {
        using StringReader reader = new(text);
        while (reader.ReadLine() is { } line)
        {
            yield return line;
        }
    }

    private ICpuExample? Find(string selector)
    {
        if (int.TryParse(selector, out int index) &&
            index >= 0 &&
            index < _examples.Count)
        {
            return _examples[index];
        }

        return _examples.FirstOrDefault(example =>
            string.Equals(example.Name, selector, StringComparison.OrdinalIgnoreCase));
    }

    private static bool Is(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
