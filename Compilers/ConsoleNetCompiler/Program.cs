using HybridCPU.Compiler.Roslyn;

internal static class Program
{
    private const string Banner = "HybridCPU C# source compiler (bounded source → image pipeline)";

    private static int Main(string[] args)
    {
        if (args.Length > 0)
        {
            if (IsHelp(args))
            {
                PrintHelp();
                return 0;
            }

            return RunCompiler(args);
        }

        return RunMenu();
    }

    private static int RunCompiler(string[] args)
    {
        using CancellationTokenSource cancellation = new();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        Console.CancelKeyPress += cancelHandler;
        try
        {
            return ManagedSourceCommandLineV1.Run(args, Console.Out, Console.Error, cancellation.Token);
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    private static int RunMenu()
    {
        Console.WriteLine(Banner);
        while (true)
        {
            Console.WriteLine();
            Console.WriteLine("1. Скомпилировать C# source в HybridCPU image");
            Console.WriteLine("2. Показать команды и ограничения");
            Console.WriteLine("0. Выход");
            Console.Write("Выбор: ");

            string? choice = Console.ReadLine();
            if (choice is null or "0")
                return 0;

            switch (choice.Trim())
            {
                case "1":
                    string[]? command;
                    try
                    {
                        command = ReadCompileRequest();
                    }
                    catch (EndOfStreamException)
                    {
                        return 0;
                    }
                    if (command is not null)
                    {
                        int exitCode = RunCompiler(command);
                        Console.WriteLine($"Код завершения: {exitCode}");
                    }
                    break;
                case "2":
                    PrintHelp();
                    break;
                default:
                    Console.WriteLine("Неизвестный пункт меню.");
                    break;
            }
        }
    }

    private static string[]? ReadCompileRequest()
    {
        List<string> args = ["compile-source-image"];
        AddRequired(args, "Путь к C# source (.cs)", "--source");
        while (ReadYesNo("Добавить ещё source файл? (д/н): "))
            AddRequired(args, "Путь к C# source (.cs)", "--source");

        AddRequired(args, "Путь к PE reference assembly (.dll)", "--reference");
        while (ReadReference())
        {
            Console.WriteLine("Роль ссылки: 1 — declarations only, 2 — разрешить выбранные method bodies.");
            string? role = ReadChoice("Роль [1/2]: ", "1", "2");
            if (role is null)
                return null;
            AddRequired(args, "Путь к PE reference assembly (.dll)", role == "2" ? "--body-reference" : "--metadata-reference");
        }

        AddRequired(args, "Имя выходной сборки", "--assembly-name");
        AddRequired(args, "Полное metadata-имя типа (например, Demo.Program)", "--type");
        AddRequired(args, "Имя метода-root", "--method");
        AddOptionalGenericArguments(args, "Аргумент generic declaring type", "--type-argument");
        AddOptionalGenericArguments(args, "Аргумент generic method", "--method-argument");
        AddRequired(args, "Путь для HybridCPU image (.hcexe)", "--out");

        if (ReadYesNo("Сохранить emitted PE? (д/н): "))
            AddRequired(args, "Путь для PE", "--emit-pe");
        if (ReadYesNo("Сохранить emitted PDB? (д/н): "))
            AddRequired(args, "Путь для PDB", "--emit-pdb");
        if (ReadYesNo("Сохранить source map JSON? (д/н): "))
            AddRequired(args, "Путь для source map JSON", "--emit-map");

        Console.WriteLine("Язык: 1 — C# 13 (default host), 2 — C# 15 (только SDK-host opt-in build).");
        string? language = ReadChoice("Язык [1/2, Enter = C# 13]: ", "1", "2", allowEmpty: true);
        if (language is null)
            return null;
        if (language == "2")
        {
            args.Add("--language");
            args.Add("csharp15");
        }

        return args.ToArray();
    }

    private static void AddOptionalGenericArguments(List<string> args, string prompt, string option)
    {
        while (ReadYesNo($"Добавить {prompt} (int32/int64)? (д/н): "))
        {
            string? value = ReadChoice("Тип аргумента [int32/int64]: ", "int32", "int64");
            if (value is null)
                throw new EndOfStreamException();
            args.Add(option);
            args.Add(value);
        }
    }

    private static bool ReadReference() => ReadYesNo("Добавить reference assembly с явной ролью? (д/н): ");

    private static void AddRequired(List<string> args, string prompt, string option)
    {
        while (true)
        {
            Console.Write($"{prompt}: ");
            string? line = Console.ReadLine();
            if (line is null)
                throw new EndOfStreamException();
            string? value = line.Trim().Trim('"');
            if (!string.IsNullOrWhiteSpace(value))
            {
                args.Add(option);
                args.Add(value);
                return;
            }
            Console.WriteLine("Значение обязательно.");
        }
    }

    private static string? ReadChoice(string prompt, string first, string second, bool allowEmpty = false)
    {
        while (true)
        {
            Console.Write(prompt);
            string? value = Console.ReadLine()?.Trim().ToLowerInvariant();
            if (allowEmpty && string.IsNullOrEmpty(value))
                return first;
            if (value == first || value == second)
                return value;
            if (value is null)
                return null;
            Console.WriteLine($"Введите {first} или {second}.");
        }
    }

    private static bool ReadYesNo(string prompt)
    {
        while (true)
        {
            Console.Write(prompt);
            string? value = Console.ReadLine()?.Trim().ToLowerInvariant();
            if (value is "д" or "да" or "y" or "yes")
                return true;
            if (value is "н" or "нет" or "n" or "no" or null)
                return false;
            Console.WriteLine("Введите да или нет.");
        }
    }

    private static bool IsHelp(string[] args) => args.Length == 1 && args[0] is "--help" or "-h" or "/?";

    private static void PrintHelp()
    {
        Console.WriteLine(Banner);
        Console.WriteLine();
        Console.WriteLine("Интерактивный режим: запустите без аргументов.");
        Console.WriteLine("CLI:");
        Console.WriteLine("  ConsoleNetCompiler compile-source-image --source App.cs --reference Core.dll --assembly-name App --type App.Program --method Read --out App.hcexe");
        Console.WriteLine("  Повторяйте --source и --reference; для ссылок с body используйте --body-reference, для declarations-only — --metadata-reference.");
        Console.WriteLine("  Опционально: --type-argument int32|int64, --method-argument int32|int64, --emit-pe PATH, --emit-pdb PATH, --emit-map PATH.");
        Console.WriteLine("  --language csharp13|csharp15; C# 13 — default, C# 15 требует сборку companion с UseSdkRoslynForCompanion=true.");
        Console.WriteLine();
        Console.WriteLine("Компилятор выполняет source diagnostics/NoHeap, deterministic Roslyn emit, restricted CIL import, canonical IR, object/link/image.");
        Console.WriteLine("Поддержка ограничена текущим контрактом importer/backend: unsupported source/IL отвергаются. Это не publish/runtime qualification.");
        Console.WriteLine("Reference PE задаются явно; автоматического поиска framework assemblies и method bodies нет.");
        Console.WriteLine("Коды: 0 успех; 2 usage/input; 3 source/NoHeap; 4 CIL/graph; 5 link/image; 130 отмена.");
    }
}
