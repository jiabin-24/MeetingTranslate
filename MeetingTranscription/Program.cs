using Azure.Identity;
using DotNetEnv.Configuration;
using EchoBot;
using EchoBot.Util;
using EchoBot.WebRTC;
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

        // Real-time messaging: allow switching between Azure SignalR and original WebSocket
        var signalrConn = builder.Configuration.GetValue<string>("AzureSignalRConnection");
        if (!string.IsNullOrEmpty(signalrConn))
            builder.Services.AddSignalR().AddAzureSignalR(option => option.ConnectionString = signalrConn); // Use Azure SignalR service to scale real-time messaging
        else
            builder.Services.AddSingleton<CaptionHub>(); // Keep original in-process WebSocket hub

        // Register publisher implementation based on the mode
        builder.Services.AddSingleton<ICaptionPublisher>(sp => CaptionPublisher.CreateInstance(!string.IsNullOrEmpty(signalrConn)));

        // WebRCT services
        builder.Services.AddSingleton<OpusBroadcaster>();
        builder.Services.AddSingleton<RtcSessionManager>();

        // Create the bot as a transient. In this case the ASP Controller is expecting an IBot.
        builder.Services.AddTransient<IBot, TranscriptionBot>();
        builder.Services.AddMvc().AddSessionStateTempDataProvider();
        builder.Services.AddSwaggerGen();

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
        builder.Services.AddSingleton<CacheHelper>();
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
        {
            app.UseDeveloperExceptionPage();
            app.UseSwagger();
            app.UseSwaggerUI();
        }

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

            endpoints.MapHub<RtcHub>("/rtc"); // WebRTC signaling hub

            if (!string.IsNullOrEmpty(signalrConn))
                endpoints.MapHub<CaptionSignalRHub>("/captionHub"); // SignalR hub for captions
            else
                endpoints.Map("/captionHub", async ctx => await ctx.RequestServices.GetRequiredService<CaptionHub>().HandleAsync(ctx)); // WebSocket hub for captions
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

        app.Run();
    }

    private static void BuildConfig(WebApplicationBuilder builder)
    {
        // Add Environment Variables
        builder.Configuration.AddEnvironmentVariables(prefix: "AppSettings__");

        var AadAppId = builder.Configuration.GetValue<string>("AppSettings:AadAppId");
        var AadAppSecret = builder.Configuration.GetValue<string>("AppSettings:AadAppSecret");
        var tenantId = builder.Configuration.GetValue<string>("MicrosoftAppTenantId");

        // Load the configuration from the .env file in development environment
        if (builder.Environment.IsDevelopment())
            builder.Configuration.AddDotNetEnv();
        else
            builder.Configuration.AddAzureKeyVault(new Uri(builder.Configuration.GetValue<string>("KeyVaultUri")), new DefaultAzureCredential());

        // Adds application configuration settings to specified IServiceCollection.
        builder.Services.AddOptions<AzureSettings>().Configure<IConfiguration>((botOptions, configuration) =>
        {
            botOptions.MicrosoftAppId = AadAppId;
            botOptions.MicrosoftAppPassword = AadAppSecret;
            botOptions.MicrosoftAppTenantId = tenantId;
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
