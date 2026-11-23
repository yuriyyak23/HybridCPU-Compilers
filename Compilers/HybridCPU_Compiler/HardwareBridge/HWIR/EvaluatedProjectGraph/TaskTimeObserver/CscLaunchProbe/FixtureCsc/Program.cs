using System.Text.Json;

var capturePath = Environment.GetEnvironmentVariable("CW_CSC_FIXTURE_CAPTURE")
    ?? throw new InvalidOperationException("CW_CSC_FIXTURE_CAPTURE missing");
var exitText = Environment.GetEnvironmentVariable("CW_CSC_FIXTURE_EXIT_CODE") ?? "0";
if (!int.TryParse(exitText, out var exitCode))
    throw new InvalidOperationException("CW_CSC_FIXTURE_EXIT_CODE invalid");
var standardInput = Environment.GetEnvironmentVariable("CW_CSC_FIXTURE_READ_STDIN") == "1"
    ? Console.ReadLine()
    : null;

File.WriteAllText(capturePath, JsonSerializer.Serialize(new
{
    pid = Environment.ProcessId,
    currentDirectory = Environment.CurrentDirectory,
    arguments = args,
    standardInput
}));
Console.WriteLine("CscLaunchProbe fixture stdout");
Console.Error.WriteLine("CscLaunchProbe fixture stderr");
return exitCode;
