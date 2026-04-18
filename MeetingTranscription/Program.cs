using Azure.Communication.CallAutomation;
using Azure.Communication.Identity;
using Azure.Identity;
using DotNetEnv.Configuration;
using EchoBot;
using EchoBot.Models.Configuration;
using EchoBot.Util;
using EchoBot.WebRTC;
using EchoBot.WebSocket;
using MeetingTranscription;
using MeetingTranscription.Bots;
using MeetingTranscription.Models.Configuration;
using MeetingTranscription.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Bot.Builder;
using Microsoft.Bot.Builder.Integration.AspNet.Core;
using Microsoft.Bot.Connector.Authentication;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using System;
using System.Collections.Concurrent;
using System.IO;

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

        builder.RegisterBotServices();

        // Real-time messaging: Azure SignalR/SignalR
        var signalrConn = builder.Configuration.GetValue<string>("AzureSignalRConnection");
        if (!string.IsNullOrEmpty(signalrConn))
            builder.Services.AddSignalR().AddAzureSignalR(option => option.ConnectionString = signalrConn); // Use Azure SignalR service to scale real-time messaging
        else
            builder.Services.AddSignalR(options =>
            {
                options.KeepAliveInterval = TimeSpan.FromSeconds(15);
                options.ClientTimeoutInterval = TimeSpan.FromSeconds(45);
            });

        // Azure Conmmunication Service (WebRTC)
        var acsConn = builder.Configuration.GetValue<string>("ACSConnectionString");
        builder.Services.AddSingleton(new CallAutomationClient(acsConn));
        builder.Services.AddSingleton(new CommunicationIdentityClient(acsConn));

        // Create the bot as a transient. In this case the ASP Controller is expecting an IBot.
        builder.Services.AddTransient<IBot, TranscriptionBot>();
        builder.Services.AddMvc().AddSessionStateTempDataProvider();
        builder.Services.AddSwaggerGen();

        BuildCache(builder);

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
        var spaFileProvider = new PhysicalFileProvider(Path.Combine(AppContext.BaseDirectory, "ClientApp", "build"));
        var combinedStaticFileProvider = new CompositeFileProvider(app.Environment.WebRootFileProvider, spaFileProvider);

        if (app.Environment.IsDevelopment())
            app.UseDeveloperExceptionPage().UseSwagger().UseSwaggerUI();

        app.UseCors("DevCors");
        app.UseWebSockets();
        app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = combinedStaticFileProvider, RequestPath = string.Empty });
        app.UseStaticFiles(new StaticFileOptions { FileProvider = combinedStaticFileProvider, RequestPath = string.Empty });
        app.UseBotServices();

        app.UseRouting().UseAuthorization().UseEndpoints(endpoints =>
        {
            endpoints.MapControllers();
            endpoints.MapControllerRoute(
            name: "default",
            pattern: "{controller=Home}/{action=Index}/{id?}");

            endpoints.MapHub<CaptionSignalRHub>("/captionHub"); // SignalR hub for captions
        });

        // WebSocket endpoint that ACS will connect to (configure this URL in MediaStreamingOptions.TransportUri)
        app.Map("/ws/media", async (HttpContext context) =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync("Expected WebSocket request");
                return;
            }

            var threadId = context.Request.Query["threadId"].ToString();
            var targetLang = context.Request.Query["targetLang"].ToString();
            if (string.IsNullOrWhiteSpace(threadId) || string.IsNullOrWhiteSpace(targetLang))
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync("Missing threadId or targetLang");
                return;
            }

            using var ws = await context.WebSockets.AcceptWebSocketAsync();
            var handler = new AcsMediaWebSocketHandler(threadId, targetLang);
            await AcsWebSocketHandlerRegistry.Register(threadId, targetLang, handler).RunAsync(ws, context.RequestAborted);
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
            builder.Configuration.AddAzureKeyVault(new Uri(builder.Configuration.GetValue<string>("KeyVaultUri")), new DefaultAzureCredential(
                new DefaultAzureCredentialOptions
                {
                    ManagedIdentityClientId = builder.Configuration.GetValue<string>("VmUserAssignedIdentity:KeyVault")
                }));

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
        builder.Services.AddOptions<ByteDanceSettings>().BindConfiguration("ByteDanceSettings");
    }

    private static void BuildCache(WebApplicationBuilder builder)
    {
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
        builder.Services.AddMemoryCache();
    }
}
