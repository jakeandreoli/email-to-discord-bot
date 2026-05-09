using System.Text.RegularExpressions;
using EmailToDiscord.Configuration;
using EmailToDiscord.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace EmailToDiscord;

public class Program
{
    public static async Task<int> Main(string[] args)
    {
        var configPath = Environment.GetEnvironmentVariable("CONFIG_PATH") ?? "config.yaml";
        if (!File.Exists(configPath))
        {
            Console.Error.WriteLine($"Configuration file not found at: {Path.GetFullPath(configPath)}");
            Console.Error.WriteLine("Set CONFIG_PATH or place config.yaml next to the binary.");
            return 1;
        }

        AppConfig config;
        try
        {
            var yaml = await File.ReadAllTextAsync(configPath);
            yaml = ExpandEnvVars(yaml);

            var deserializer = new DeserializerBuilder()
                .WithNamingConvention(UnderscoredNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build();
            config = deserializer.Deserialize<AppConfig>(yaml) ?? new AppConfig();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to load config: {ex.Message}");
            return 1;
        }

        if (config.Mailboxes.Count == 0)
        {
            Console.Error.WriteLine("No mailboxes configured.");
            return 1;
        }

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft.Hosting", Serilog.Events.LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .WriteTo.Console(
                outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}"
            )
            .WriteTo.File(
                "logs/bot-.log",
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}"
            )
            .CreateLogger();

        try
        {
            var host = Host.CreateDefaultBuilder(args)
                .UseSerilog()
                .ConfigureServices(services =>
                {
                    services.AddSingleton(config);
                    services.AddSingleton<StateStore>();
                    services.AddSingleton<ThreadStore>();
                    services.AddSingleton<CannedReplyService>();
                    services.AddSingleton<ContentConverter>();
                    services.AddSingleton<EmailSender>();
                    services.AddSingleton<DiscordBotService>();
                    services.AddHostedService(p => p.GetRequiredService<DiscordBotService>());
                    services.AddSingleton<EmailPollingService>();
                    services.AddHostedService(p => p.GetRequiredService<EmailPollingService>());
                })
                .Build();

            await host.RunAsync();
            return 0;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Application terminated unexpectedly");
            return 1;
        }
        finally
        {
            await Log.CloseAndFlushAsync();
        }
    }

    private static string ExpandEnvVars(string yaml)
    {
        return Regex.Replace(yaml, @"\$\{([A-Z_][A-Z0-9_]*)\}", match =>
        {
            var name = match.Groups[1].Value;
            return Environment.GetEnvironmentVariable(name) ?? match.Value;
        });
    }
}
