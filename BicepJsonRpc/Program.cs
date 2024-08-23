using System.IO.Pipes;
using System.CommandLine;
using System.CommandLine.Binding;
using BicepJsonRpc;
using Microsoft.Extensions.Logging;
using StreamJsonRpc;



var bicepModuleRootPathOption = new Option<string>(name: "--bicepModuleRootPath",
    description: "The root path containing bicep modules to compile");
bicepModuleRootPathOption.AddAlias("-r");
bicepModuleRootPathOption.IsRequired = true;

var bicepCompilationOutputPathOption = new Option<string>(name: "--bicepCompilationOutputPath",
    description: "The path to output the compiled bicep files");
bicepCompilationOutputPathOption.AddAlias("-o");
bicepCompilationOutputPathOption.IsRequired = true;

var pipeNameOption = new Option<string>(name: "--pipeName",
    description: "The name of the pipe to use for communication");
pipeNameOption.AddAlias("-p");
pipeNameOption.IsRequired = false;

var shouldExtractInSameFolderOption = new Option<bool>(name: "--shouldExtractInSameFolder",
    description: "Whether to extract the compiled files in the same folder as the bicep files");
shouldExtractInSameFolderOption.AddAlias("-s");
shouldExtractInSameFolderOption.IsRequired = false;

var searchRecursivelyOption = new Option<bool>(name: "--searchRecursively",
    description: "Whether to search for bicep files recursively");
searchRecursivelyOption.AddAlias("-rs");
searchRecursivelyOption.IsRequired = false;

var verbosityOption = new Option<VerbosityLevel>(name: "--verbosity",
    description: "The verbosity level of the logger. Informational bicep diagnostics will be logged to the trace.")
    {
        IsRequired = false
    };

var rootCommand = new RootCommand
{
    bicepCompilationOutputPathOption,
    bicepModuleRootPathOption,
    pipeNameOption,
    shouldExtractInSameFolderOption,
    searchRecursivelyOption,
    verbosityOption
};

rootCommand.Description = "A tool to compile bicep files into JSON ARM templates";

rootCommand.SetHandler(async (bicepCompilationOutputPathValue, bicepModuleRootPathValue, 
    pipeNameValue, shouldExtractInSameFolderValue, searchRecursivelyValue, logger) =>
{
    await RunAsync(bicepModuleRootPathValue, bicepCompilationOutputPathValue, logger, pipeNameValue, 
        shouldExtractInSameFolderValue, searchRecursivelyValue);
    
}, bicepCompilationOutputPathOption, bicepModuleRootPathOption, pipeNameOption, 
    shouldExtractInSameFolderOption, searchRecursivelyOption, new MyCustomBinder(verbosityOption));

return await rootCommand.InvokeAsync(args);

static async Task<int> RunAsync(string rootPath, string outputPath, ILogger logger, string? pipeName = null, bool? sameFolderExtraction = null,
    bool? recursiveSearch = null)
{
    pipeName = pipeName ?? Guid.NewGuid().ToString();
    await using var pipeStream = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 
        NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
    logger.LogInformation("Named piped created with name {0}", pipeName);

    var timeout = TimeSpan.FromMinutes(15);
    var cts = new CancellationTokenSource(timeout);

    try
    {
        logger.LogWarning("Starting to listen on the pipe {0} until {1}", pipeName,
            DateTime.Now.AddMinutes(timeout.TotalMinutes));
        await pipeStream.WaitForConnectionAsync(cts.Token);
        logger.LogInformation("A client connected to the pipe {0}", pipeName);
        var client = JsonRpc.Attach<ICliJsonRpcProtocol>(CliJsonRpcServer.CreateMessageHandler(pipeStream, pipeStream));
        await DecompileBicepFilesAsync(rootPath, outputPath, logger, client, sameFolderExtraction, recursiveSearch);
    }
    catch (Exception ex)
    {
        logger.LogError("An unexpected error occurred: {0}", ex.Message);
        return 1;
    }
    finally
    {
        await cts.CancelAsync();
    }
    return 0;
}

static async Task DecompileBicepFilesAsync(string rootPath, string outputPath, ILogger logger, ICliJsonRpcProtocol bicepClient,  bool? sameFolderExtraction = null,
    bool? recursiveSearch = null)
{
    var bicepFiles = Directory.GetFiles(rootPath, "*.bicep", recursiveSearch == true ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly);
    var tasks = new List<Task>();
    foreach (var bicepFile in bicepFiles)
    {
        tasks.Add(CompileBicepFileAsync(bicepFile, bicepClient, 
            sameFolderExtraction == true, outputPath, logger));
    }

    await Task.WhenAll(tasks);

    logger.LogInformation("Finished compiling {0} bicep files", bicepFiles.Length);
}

static async Task CompileBicepFileAsync(string bicepFilePath, ICliJsonRpcProtocol bicepClient, bool shouldExtractInSameFolder, string outputPath, ILogger logger)
{
    using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1));
    var compileResult = await bicepClient.Compile(
        new CompileRequest(bicepFilePath), cts.Token);
    if (compileResult.Success)
    {
        string outPutFilePath = shouldExtractInSameFolder
            ? Path.Combine(Directory.GetParent(bicepFilePath)!.FullName, Path.GetFileNameWithoutExtension(bicepFilePath) + ".json")
            : Path.Combine(outputPath, Path.GetFileNameWithoutExtension(bicepFilePath) + ".json");
        logger.LogInformation("Extracting file {0} to {1}",bicepFilePath, outPutFilePath);
        foreach (var diagnostic in compileResult.Diagnostics)
        {
            if (diagnostic.Level == "Warning")
            {
                logger.LogWarning("{0} ({1}:{2}): {3}", diagnostic.Source, diagnostic.Range.Start.Line, 
                    diagnostic.Range.Start.Char, diagnostic.Message);
            }
            if(diagnostic.Level == "Error")
            {
                logger.LogError("{0} ({1}:{2}): {3}", diagnostic.Source, diagnostic.Range.Start.Line,
                    diagnostic.Range.Start.Char, diagnostic.Message);
            }
            else
            {
                logger.LogTrace("{0} ({1}:{2}): {3}", diagnostic.Source, diagnostic.Range.Start.Line, 
                    diagnostic.Range.Start.Char, diagnostic.Message);
            }
        }
        await File.WriteAllTextAsync(outPutFilePath, compileResult.Contents);
    }
    else
    {
        var errorMessage = string.Join("\n", compileResult.Diagnostics.Select(d => d.Message).ToArray());
        logger.LogError("Failed to compile {0}. Error: {1}", bicepFilePath, errorMessage);
    }
}

public class MyCustomBinder(Option<VerbosityLevel> verbosity) : BinderBase<ILogger>
{
    protected override ILogger GetBoundValue(
        BindingContext bindingContext) => GetLogger(bindingContext);

    ILogger GetLogger(BindingContext bindingContext)
    {
        var verbosityOptionResult = bindingContext.ParseResult.CommandResult.GetValueForOption(verbosity);
        LogLevel logLevel;
        switch (verbosityOptionResult)
        {
            case VerbosityLevel.Error:
                logLevel = LogLevel.Error;
                break;
            case VerbosityLevel.Warning:
                logLevel = LogLevel.Warning;
                break;
            case VerbosityLevel.Info:
                logLevel = LogLevel.Information;
                break;
            case VerbosityLevel.Trace:
                logLevel = LogLevel.Trace;
                break;
            default:
                logLevel = LogLevel.Information;
                break;
        }
        using ILoggerFactory loggerFactory = LoggerFactory.Create(
            builder => builder.AddConsole().SetMinimumLevel(logLevel));
        ILogger logger = loggerFactory.CreateLogger("BicepJsonRpc");
        return logger;
    }
}

public enum VerbosityLevel
{
    Trace,
    Info,
    Warning,
    Error
}