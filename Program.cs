using System;
using System.IO;
using System.Threading;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using NetflixHouseholdConfirmator.Configuration;
using NetflixHouseholdConfirmator.Service;
using NetflixHouseholdConfirmator.Service.Processors;

using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using NuciLog;
using NuciLog.Configuration;
using NuciLog.Core;
using NuciWeb.Automation.Selenium;
using NuciWeb.Automation;

namespace NetflixHouseholdConfirmator
{
    public sealed class Program
    {
        static BotSettings botSettings;
        static DebugSettings debugSettings;
        static ImapSettings imapSettings;
        static NuciLoggerSettings loggerSettings;

        static IWebDriver webDriver;
        static ILogger logger;

        static IServiceProvider serviceProvider;
        static readonly CancellationTokenSource cancellationTokenSource = new();

        static void Main(string[] args)
        {
            LoadConfiguration();
            ValidateConfiguration();

            webDriver = InitialiseWebDriverSafely();

            serviceProvider = CreateIOC();
            logger = serviceProvider.GetRequiredService<ILogger>();
            IHouseholdConfirmator service = serviceProvider.GetRequiredService<IHouseholdConfirmator>();

            Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                cancellationTokenSource.Cancel();
            };

            logger.Info(Operation.StartUp, "The service has started.");

            try
            {
                service.ConfirmIncomingHouseholdUpdateRequests(cancellationTokenSource.Token);
            }
            catch (AggregateException ex)
            {
                if (logger is not null)
                {
                    LogInnerExceptions(ex);
                }

                SaveCrashScreenshot();
            }
            catch (Exception ex)
            {
                if (logger is not null)
                {
                    logger.Fatal(Operation.Unknown, OperationStatus.Failure, ex);
                }
                else
                {
                    Console.Error.WriteLine(ex);
                }

                SaveCrashScreenshot();
            }
            finally
            {
                webDriver?.Quit();

                logger?.Info(Operation.ShutDown, "The service has stopped.");
            }
        }

        static IWebDriver InitialiseWebDriverSafely()
        {
            try
            {
                return InitialiseChromeWebDriver();
            }
            catch (Exception chromeException)
            {
                throw new InvalidOperationException(
                    "Failed to initialise Chrome WebDriver with server-safe options. Check /tmp/chromedriver.log for driver details.",
                    chromeException);
            }
        }

        static IWebDriver InitialiseChromeWebDriver()
        {
            string driverPath = GetFirstExistingPath(
                "/usr/local/bin/chromedriver",
                "/usr/bin/chromedriver");

            ChromeDriverService driverService = string.IsNullOrWhiteSpace(driverPath)
                ? ChromeDriverService.CreateDefaultService()
                : ChromeDriverService.CreateDefaultService(Path.GetDirectoryName(driverPath), Path.GetFileName(driverPath));

            driverService.HideCommandPromptWindow = true;
            driverService.EnableVerboseLogging = true;
            driverService.LogPath = Path.Combine(Path.GetTempPath(), "chromedriver.log");

            ChromeOptions options = new();
            options.AddArgument("--no-sandbox");
            options.AddArgument("--disable-dev-shm-usage");
            options.AddArgument("--disable-gpu");
            options.AddArgument("--disable-software-rasterizer");
            options.AddArgument("--disable-background-networking");
            options.AddArgument("--disable-extensions");
            options.AddArgument("--window-size=1920,1080");
            options.AddArgument("--no-first-run");
            options.AddArgument("--no-default-browser-check");
            options.AddArgument("--remote-debugging-port=9222");

            string profileDirectory = Path.Combine(Path.GetTempPath(), "netflix-household-confirmator-chrome-profile");
            Directory.CreateDirectory(profileDirectory);
            options.AddArgument($"--user-data-dir={profileDirectory}");

            if (!debugSettings.IsDebugMode)
            {
                options.AddArgument("--headless");
            }

            string browserBinaryPath = GetFirstExistingPath(
                "/snap/chromium/current/usr/lib/chromium-browser/chrome",
                "/snap/bin/chromium",
                "/usr/bin/chromium-browser",
                "/usr/bin/chromium",
                "/usr/bin/google-chrome",
                "/usr/bin/google-chrome-stable");

            if (!string.IsNullOrWhiteSpace(browserBinaryPath))
            {
                options.BinaryLocation = browserBinaryPath;
            }

            TimeSpan commandTimeout = TimeSpan.FromSeconds(Math.Max(botSettings.PageLoadTimeout, 60));

            return new ChromeDriver(driverService, options, commandTimeout);
        }

        static string GetFirstExistingPath(params string[] candidatePaths)
        {
            foreach (string candidatePath in candidatePaths)
            {
                if (File.Exists(candidatePath))
                {
                    return candidatePath;
                }
            }

            return null;
        }

        static IConfiguration LoadConfiguration()
        {
            botSettings = new BotSettings();
            debugSettings = new DebugSettings();
            imapSettings = new ImapSettings();
            loggerSettings = new NuciLoggerSettings();

            IConfiguration config = new ConfigurationBuilder()
                .AddJsonFile("appsettings.json", true, true)
                .AddEnvironmentVariables()
                .Build();

            config.Bind(nameof(BotSettings), botSettings);
            config.Bind(nameof(DebugSettings), debugSettings);
            config.Bind(nameof(ImapSettings), imapSettings);
            config.Bind(nameof(NuciLoggerSettings), loggerSettings);

            return config;
        }

        static void ValidateConfiguration()
        {
            if (string.IsNullOrWhiteSpace(imapSettings.Server))
            {
                throw new InvalidOperationException("The IMAP server is not configured.");
            }

            if (imapSettings.Port <= 0)
            {
                throw new InvalidOperationException("The IMAP port must be greater than 0.");
            }

            if (string.IsNullOrWhiteSpace(imapSettings.Username))
            {
                throw new InvalidOperationException("The IMAP username is not configured.");
            }

            if (string.IsNullOrWhiteSpace(imapSettings.Password))
            {
                throw new InvalidOperationException(
                    "The IMAP password is not configured. Set it in appsettings.json or via ImapSettings__Password environment variable.");
            }

            if (imapSettings.MaxEmailAge <= 0)
            {
                throw new InvalidOperationException("The IMAP max email age must be greater than 0 seconds.");
            }

            if (botSettings.PageLoadTimeout <= 0)
            {
                throw new InvalidOperationException("Page load timeout must be greater than 0 seconds.");
            }

            if (botSettings.PollIntervalSeconds <= 0)
            {
                throw new InvalidOperationException("Poll interval must be greater than 0 seconds.");
            }

            if (botSettings.ErrorRetryDelaySeconds < 0)
            {
                throw new InvalidOperationException("Error retry delay cannot be negative.");
            }
        }

        static IServiceProvider CreateIOC()
        {
            return new ServiceCollection()
                .AddSingleton(botSettings)
                .AddSingleton(debugSettings)
                .AddSingleton(imapSettings)
                .AddSingleton(loggerSettings)
                .AddSingleton<IEmailProcessor, EmailProcessor>()
                .AddSingleton<ILogger, NuciLogger>()
                .AddSingleton<IWebDriver>(s => webDriver)
                .AddSingleton<IWebProcessor, SeleniumWebProcessor>()
                .AddSingleton<INetflixProcessor, NetflixProcessor>()
                .AddSingleton<IHouseholdConfirmator, HouseholdConfirmator>()
                .BuildServiceProvider();
        }

        static void LogInnerExceptions(AggregateException exception)
        {
            foreach (Exception innerException in exception.InnerExceptions)
            {
                if (innerException is not AggregateException innerAggregateException)
                {
                    logger.Fatal(Operation.Unknown, OperationStatus.Failure, innerException);
                }
                else
                {
                    LogInnerExceptions(innerAggregateException);
                }
            }
        }

        static void SaveCrashScreenshot()
        {
            if (debugSettings is null ||
                loggerSettings is null ||
                !debugSettings.IsCrashScreenshotEnabled ||
                webDriver is not ITakesScreenshot screenshotTaker)
            {
                return;
            }

            string directory = Path.GetDirectoryName(loggerSettings.LogFilePath);

            if (string.IsNullOrWhiteSpace(directory))
            {
                directory = Directory.GetCurrentDirectory();
            }

            Directory.CreateDirectory(directory);

            string filePath = Path.Combine(directory, debugSettings.CrashScreenshotFileName);

            try
            {
                screenshotTaker
                    .GetScreenshot()
                    .SaveAsFile(filePath);
            }
            catch (Exception exception)
            {
                logger?.Error(Operation.Unknown, OperationStatus.Failure, "Failed to save crash screenshot.", exception);
            }
        }
    }
}
