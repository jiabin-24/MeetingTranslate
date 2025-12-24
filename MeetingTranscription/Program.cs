using Azure.Identity;
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
using System.Collections.Concurrent;

class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Host.UseWindowsService(options => { options.ServiceName = "Echo Bot Service"; });

        BuildConfig(builder);

        builder.Services.AddHttpClient().AddControllers().AddNewtonsoftJson();

        // Creates Singleton Card Factory.
        builder.Services.AddSingleton<ICardFactory, CardFactory>();

        // Create a global hashset for our save task details
        builder.Services.AddSingleton<ConcurrentDictionary<string, string>>();

        // Create the Bot Framework Authentication to be used with the Bot Adapter.
        builder.Services.AddSingleton<BotFrameworkAuthentication, ConfigurationBotFrameworkAuthentication>();

        // Create the Bot Adapter with error handling enabled.
        builder.Services.AddSingleton<CloudAdapter, AdapterWithErrorHandler>();

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

        app.Run();
    }

    private static void BuildConfig(WebApplicationBuilder builder)
    {
        // Load the configuration from the .env file in development environment
        if (builder.Environment.IsDevelopment())
            builder.Configuration.AddDotNetEnv();
        else
            builder.Configuration.AddAzureKeyVault(new System.Uri(builder.Configuration.GetValue<string>("AzureKeyVaultURL")), new DefaultAzureCredential());

        // Adds application configuration settings to specified IServiceCollection.
        builder.Services.AddOptions<AzureSettings>().Configure<IConfiguration>((botOptions, configuration) =>
        {
            botOptions.MicrosoftAppId = configuration.GetValue<string>("MicrosoftAppId");
            botOptions.MicrosoftAppPassword = configuration.GetValue<string>("MicrosoftAppPassword");
            botOptions.MicrosoftAppTenantId = configuration.GetValue<string>("MicrosoftAppTenantId");
            botOptions.AppBaseUrl = configuration.GetValue<string>("AppBaseUrl");
            botOptions.UserId = configuration.GetValue<string>("UserId");
            botOptions.GraphApiEndpoint = configuration.GetValue<string>("GraphApiEndpoint");

            if (string.IsNullOrEmpty(botOptions.MicrosoftAppPassword))
                throw new System.Exception("MicrosoftAppPassword is null or empty. Please check your configuration.");
        });
    }
}

