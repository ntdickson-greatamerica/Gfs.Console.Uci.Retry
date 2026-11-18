using log4net;
using log4net.Appender;
using log4net.Core;
using log4net.Repository;
using log4net.Repository.Hierarchy;
using log4net.Util;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ILoggerFactory = Microsoft.Extensions.Logging.ILoggerFactory;

namespace Gfs.Console.Uci.Retry.Logging;

/// <summary>
/// Builds the application's logger factory. Logs always go to stderr (never stdout, which is reserved for the JSON result);
/// when Logging:Log4Net:Enabled is true they also go to a log4net RollingFileAppender configured by log4net.config.
/// </summary>
public sealed class AppLogging : IDisposable
{
    private const string DirectoryProperty = "UciRetryLogDirectory";
    private static readonly object Sync = new();

    private readonly ILoggerRepository? _repository;

    private AppLogging(ILoggerFactory factory, ILoggerRepository? repository)
    {
        Factory = factory;
        _repository = repository;
    }

    public ILoggerFactory Factory { get; }

    /// <summary>Creates the factory. Returns false (with a non-sensitive reason) when enabled file logging cannot be initialized.</summary>
    public static bool TryCreate(IConfiguration configuration, out AppLogging? logging, out string? failure)
    {
        logging = null;
        failure = null;

        var options = configuration.GetSection(Log4NetOptions.SectionName).Get<Log4NetOptions>() ?? new Log4NetOptions();
        ILoggerRepository? repository = null;

        if (options.Enabled)
        {
            if (!TryConfigureFileLogging(options, out repository, out failure))
            {
                return false;
            }
        }

        var factory = LoggerFactory.Create(builder =>
        {
            builder.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
            if (repository is not null)
            {
                builder.AddLog4Net(new Log4NetProviderOptions { ExternalConfigurationSetup = true, LoggerRepository = repository.Name });
            }
        });

        logging = new AppLogging(factory, repository);
        return true;
    }

    public void Dispose()
    {
        Factory.Dispose();
        _repository?.Shutdown(); // closes the appender so the file is released
    }

    private static bool TryConfigureFileLogging(Log4NetOptions options, out ILoggerRepository? repository, out string? failure)
    {
        repository = null;
        failure = null;

        if (string.IsNullOrWhiteSpace(options.Directory))
        {
            failure = "Logging:Log4Net:Directory is required when file logging is enabled.";
            return false;
        }

        var configFile = new FileInfo(Path.Combine(AppContext.BaseDirectory, "log4net.config"));
        if (!configFile.Exists)
        {
            failure = "log4net.config was not found next to the application.";
            return false;
        }

        string directory;
        try
        {
            directory = Path.GetFullPath(options.Directory.Trim());
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, $".write-test-{Guid.NewGuid():N}.tmp");
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            failure = $"The log directory could not be created or written ({ex.GetType().Name}).";
            return false;
        }

        // log4net reports its own configuration problems only through LogLog, so errors raised while configuring are captured.
        var internalErrors = new List<string>();
        void Capture(object? sender, LogReceivedEventArgs e)
        {
            if (e.LogLog.Prefix.Contains("ERROR", StringComparison.OrdinalIgnoreCase))
            {
                internalErrors.Add(e.LogLog.Message);
            }
        }

        lock (Sync)
        {
            LogLog.LogReceived += Capture;
            try
            {
                GlobalContext.Properties[DirectoryProperty] = directory;
                var created = LogManager.CreateRepository($"UciRetry-{Guid.NewGuid():N}", typeof(Hierarchy));
                log4net.Config.XmlConfigurator.Configure(created, configFile);

                if (internalErrors.Count > 0 || !created.Configured || !created.GetAppenders().OfType<RollingFileAppender>().Any())
                {
                    created.Shutdown();
                    failure = "log4net could not be initialized from log4net.config.";
                    return false;
                }

                repository = created;
                return true;
            }
            catch (Exception ex)
            {
                failure = $"log4net could not be initialized ({ex.GetType().Name}).";
                return false;
            }
            finally
            {
                LogLog.LogReceived -= Capture;
            }
        }
    }
}

