using EchoBot.Bot;
using EchoBot.Util;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Graph.Communications.Common.Telemetry;

namespace EchoBot
{
    public static class StartupExtensions
    {
        /// <summary>
        /// Register bot-related services. Accepts the WebApplicationBuilder as 'app' parameter.
        /// </summary>
        public static void RegisterBotServices(this WebApplicationBuilder app)
        {
            app.Services
                .AddOptions<AppSettings>()
                .BindConfiguration(nameof(AppSettings))
                .ValidateDataAnnotations()
                .ValidateOnStart();

            app.Services.AddSingleton<IGraphLogger, GraphLogger>(_ => new GraphLogger("EchoBotWorker", redirectToTrace: true));
            app.Services.AddSingleton<IBotMediaLogger, BotMediaLogger>();

            app.Services.AddSingleton<IBotService, BotService>();

            // determine internal hosting protocol and build listening urls
            var botInternalHostingProtocol = "https";
            // localhost
            var baseDomain = "+";

            // http for local development
            // https for running on VM
            var appSettings = app.Configuration.GetSection("AppSettings").Get<AppSettings>();
            var callListeningUris = new HashSet<string>
            {
                $"{botInternalHostingProtocol}://{baseDomain}:{appSettings!.BotInternalPort}/",
                $"{botInternalHostingProtocol}://{baseDomain}:{appSettings.BotInternalPort + 1}/"
            };
            app.WebHost.UseUrls([.. callListeningUris]);

            if (!app.Environment.IsDevelopment())
            {
                app.WebHost.ConfigureKestrel(serverOptions =>
                {
                    serverOptions.ConfigureHttpsDefaults(listenOptions =>
                    {
                        listenOptions.ServerCertificate = Utilities.GetCertificateFromStore(appSettings.CertificateThumbprint);
                    });
                });

                app.Services.PostConfigure<AppSettings>(options =>
                {
                    options.BotInstanceExternalPort = 443;
                    options.MediaInstanceExternalPort = 443;

                });
            }
        }

        public static void UseBotServices(this WebApplication app)
        {
            using var scope = app.Services.CreateScope();
            var bot = scope.ServiceProvider.GetRequiredService<IBotService>();
            bot.Initialize();
        }
    }
}
