using Gfs.Console.Uci.Retry;
using Gfs.Console.Uci.Retry.Logging;
using Gfs.Console.Uci.Retry.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

// stdout is reserved for the single JSON result; logs go to stderr (and to the log4net file when enabled).
using var bootstrapFactory = LoggerFactory.Create(builder => builder
    .AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace));

// A failure before the normal flow can run. Logs it, attempts the one technical notification with whatever configuration is
// available (notification setup does not depend on logging), and exits non-zero.
static int Fail(ILoggerFactory factory, IConfiguration? configuration, string[] args, Exception? ex, string message)
{
    var logger = factory.CreateLogger("UciRetry");
    if (ex is null) logger.LogError("{Message}", message); else logger.LogError(ex, "{Message}", message);

    var result = new ProcessingResult
    {
        Outcome = ProcessingOutcome.ProcessingError,
        Message = message,
        OriginalErrorFile = CommandLine.TryGetErrorFile(args, out _)
    };

    try
    {
        if (configuration is not null)
        {
            result = UciRetryApp.NotifyTechnical(result, configuration, logger);
        }
    }
    catch (Exception notifyEx)
    {
        logger.LogError("The technical notification could not be attempted ({Error}).", notifyEx.GetType().Name);
    }

    Console.Out.WriteLine(result.ToJson());
    return result.ExitCode;
}

IConfiguration configuration;
try
{
    configuration = new ConfigurationBuilder()
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.json", optional: false)
        .AddEnvironmentVariables("UCIRETRY_")
        .Build();
}
catch (Exception ex)
{
    // appsettings.json is unusable; environment variables may still carry complete notification settings, so try them alone.
    IConfiguration? fallback = null;
    try
    {
        fallback = new ConfigurationBuilder().AddEnvironmentVariables("UCIRETRY_").Build();
    }
    catch (Exception)
    {
    }

    return Fail(bootstrapFactory, fallback, args, ex, "Configuration could not be loaded.");
}

// When file logging is enabled but cannot start the run is refused (not run unlogged), after attempting the technical notification.
if (!AppLogging.TryCreate(configuration, out var logging, out var loggingFailure))
{
    return Fail(bootstrapFactory, configuration, args, null, loggingFailure!);
}

using (logging)
{
    try
    {
        return UciRetryApp.Run(args, Console.Out, logging!.Factory, configuration);
    }
    catch (Exception ex)
    {
        return Fail(logging!.Factory, configuration, args, ex, "An unexpected error occurred. See the application log for details.");
    }
}