using DotNetEnv.Configuration;
using EchoBot;
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

        // The following line enables Application Insights telemetry collection.
        builder.Services.AddApplicationInsightsTelemetry(option =>
        {
            option.ConnectionString = builder.Configuration.GetValue<string>("ApplicationInsights:ConnectionString");
        });
        builder.Logging.AddApplicationInsights();

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

        // Create the bot as a transient. In this case the ASP Controller is expecting an IBot.
        builder.Services.AddTransient<IBot, TranscriptionBot>();
        builder.Services.AddMvc().AddSessionStateTempDataProvider();

        var app = builder.Build();

        if (app.Environment.IsDevelopment())
        {
            app.UseDeveloperExceptionPage();
        }

        app.UseDefaultFiles()
            .UseStaticFiles()
            .UseWebSockets()
            .UseRouting()
            .UseAuthorization()
            .UseEndpoints(endpoints =>
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
            //if (app.Environment.IsDevelopment())
            //{
            //    spa.Options.StartupTimeout = TimeSpan.FromSeconds(120);
            //    spa.UseReactDevelopmentServer(npmScript: "start");
            //}
        });

        app.Run();
    }

    private static void BuildConfig(WebApplicationBuilder builder)
    {
        // Load the configuration from the .env file in development environment
        if (builder.Environment.IsDevelopment())
            builder.Configuration.AddDotNetEnv();

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
    }
}
