using Azure.Identity;
using DotNetEnv.Configuration;
using EchoBot;
using EchoBot.Util;
using EchoBot.WebSocket;
using MeetingTranscription;
using MeetingTranscription.Bots;
using MeetingTranscription.Models.Configuration;
using MeetingTranscription.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Bot.Builder;
using Microsoft.Bot.Builder.Integration.AspNet.Core;
using Microsoft.Bot.Connector.Authentication;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using System;
using System.Collections.Concurrent;

static class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Host.UseWindowsService(options => { options.ServiceName = "Echo Bot Service"; });

        BuildConfig(builder);

        builder.Services.AddHttpClient().AddControllers().AddNewtonsoftJson();

        // Add Application Insights telemetry services to the services container.
        builder.Services.AddApplicationInsightsTelemetry(builder.Configuration);
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole();
        builder.Logging.AddApplicationInsights(); // 只用于旧模式，若 AddApplicationInsightsTelemetry 已注册可省略

        // Creates Singleton Card Factory.
        builder.Services.AddSingleton<ICardFactory, CardFactory>();

        // Create a global hashset for our save task details
        builder.Services.AddSingleton<ConcurrentDictionary<string, string>>();

        // Create the Bot Framework Authentication to be used with the Bot Adapter.
        builder.Services.AddSingleton<BotFrameworkAuthentication, ConfigurationBotFrameworkAuthentication>();

        // Create the Bot Adapter with error handling enabled.
        builder.Services.AddSingleton<CloudAdapter, AdapterWithErrorHandler>();

        // In production, the React files will be served from this directory
        builder.Services.AddSpaStaticFiles(configuration =>
        {
            configuration.RootPath = "ClientApp/build";
        });

        builder.RegisterBotServices();

        // WebSocket 相关服务
        builder.Services.AddSingleton<CaptionHub>();
        builder.Services.AddSingleton<CaptionPublisher>();

        // Create the bot as a transient. In this case the ASP Controller is expecting an IBot.
        builder.Services.AddTransient<IBot, TranscriptionBot>();
        builder.Services.AddMvc().AddSessionStateTempDataProvider();

        // 分布式缓存
        var redisConfig = builder.Configuration.GetSection("Redis");
        builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
        {
            var options = ConfigurationOptions.Parse(redisConfig["Configuration"]!);
            options.Password = redisConfig["Password"];
            options.Ssl = true;
            options.ReconnectRetryPolicy = new ExponentialRetry(5000); // 断线重连
            options.ConnectTimeout = 5000;
            options.SyncTimeout = 5000;
            options.ClientName = redisConfig["InstanceName"];
            return ConnectionMultiplexer.Connect(options);
        });
        builder.Services.AddCors(options =>
        {
            options.AddPolicy("DevCors", builder =>
            {
                builder.WithOrigins("https://teams.microsoft.com", "https://localhost:3000")
                       .AllowAnyHeader()
                       .AllowAnyMethod()
                       .AllowCredentials();
            });
        });

        var app = builder.Build();

        if (app.Environment.IsDevelopment())
            app.UseDeveloperExceptionPage();

        app.UseCors("DevCors");
        app.UseDefaultFiles();
        app.UseStaticFiles();
        app.UseWebSockets();
        app.UseRouting().UseAuthorization().UseEndpoints(endpoints =>
        {
            endpoints.MapControllers();
            endpoints.MapControllerRoute(
            name: "default",
            pattern: "{controller=Home}/{action=Index}/{id?}");
        });
        app.UseBotServices();
        app.UseSpaStaticFiles();
        app.UseSpa(spa =>
        {
            spa.Options.SourcePath = "ClientApp";
            if (app.Environment.IsDevelopment())
            {
                //spa.Options.StartupTimeout = TimeSpan.FromSeconds(120);
                //spa.UseReactDevelopmentServer(npmScript: "start");
            }
        });
        ServiceLocator.Initialize(app.Services);
        app.Map("/realtime", async ctx =>
        {
            // WebSocket 端点（前端连接：wss://host:port/realtime）
            var hub = ctx.RequestServices.GetRequiredService<CaptionHub>();
            await hub.HandleAsync(ctx);
        });

        app.Run();
    }

    private static void BuildConfig(WebApplicationBuilder builder)
    {
        // Load the configuration from the .env file in development environment
        if (builder.Environment.IsDevelopment())
            builder.Configuration.AddDotNetEnv();
        else
            builder.Configuration.AddAzureKeyVault(new Uri(builder.Configuration.GetValue<string>("KeyVaultUri")), new DefaultAzureCredential());

        // Add Environment Variables
        builder.Configuration.AddEnvironmentVariables(prefix: "AppSettings__");

        // Adds application configuration settings to specified IServiceCollection.
        builder.Services.AddOptions<AzureSettings>().Configure<IConfiguration>((botOptions, configuration) =>
        {
            botOptions.MicrosoftAppId = configuration.GetValue<string>("AppSettings:AadAppId");
            botOptions.MicrosoftAppPassword = configuration.GetValue<string>("AppSettings:AadAppSecret");
            botOptions.MicrosoftAppTenantId = configuration.GetValue<string>("MicrosoftAppTenantId");
            botOptions.AppBaseUrl = configuration.GetValue<string>("AppBaseUrl");
            botOptions.UserId = configuration.GetValue<string>("UserId");
            botOptions.GraphApiEndpoint = configuration.GetValue<string>("GraphApiEndpoint");

            if (string.IsNullOrEmpty(botOptions.MicrosoftAppPassword))
                throw new ArgumentException("MicrosoftAppPassword is null or empty. Please check your configuration.");
        });

        builder.Services.AddOptions<AIServiceSettings>().BindConfiguration(nameof(AIServiceSettings));
        builder.Services.AddOptions<TranslatorOptions>().BindConfiguration("Translator");
    }
}
