using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.HttpsPolicy;
using Microsoft.AspNetCore.SpaServices.ReactDevelopmentServer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Server.Messaging;
using Server.Middleware;

namespace Server
{
    public class Startup
    {
        public Startup(IConfiguration configuration)
        {
            Configuration = configuration;
        }

        public IConfiguration Configuration { get; }

        // This method gets called by the runtime. Use this method to add services to the container.
        public void ConfigureServices(IServiceCollection services)
        {
            services.AddSingleton<ConnectionManagementService>();
            services.AddSingleton<IConnectionManagementService, ConnectionManagementService>(
                sp => sp.GetService<ConnectionManagementService>());
            services.AddSingleton<IHostedService, ConnectionManagementService>(
	            sp => sp.GetService<ConnectionManagementService>());

            services.Configure<ConnectionManagementServiceOptions>(Configuration);

            if (Configuration.GetSection("backplane").Get<string>() == "azure")
            {
                services.AddSingleton<AzureServiceBusBackplane>();
                services.AddSingleton<IMessagingBackplane, AzureServiceBusBackplane>(
                    sp => sp.GetService<AzureServiceBusBackplane>());
                services.AddSingleton<IHostedService, AzureServiceBusBackplane>(
                    sp => sp.GetService<AzureServiceBusBackplane>());

                services.Configure<AzureServiceBusBackplaneOptions>(Configuration.GetSection("azure"));
            }
            else
            {
                services.AddSingleton<IMessagingBackplane, DummyBackplane>();
            }

            services.AddControllersWithViews();

            // In production, the React files will be served from this directory
            services.AddSpaStaticFiles(configuration =>
            {
                configuration.RootPath = "ClientApp/build";
            });
        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IConfiguration cfg, IWebHostEnvironment env)
        {
            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }
            else
            {
                app.UseExceptionHandler("/Error");
                // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
                app.UseHsts();
            }

            app.UseWebSockets();

            //app.UseHttpsRedirection();
            app.UseStaticFiles();
            app.UseSpaStaticFiles();

            app.UseRouting();

            app.UseEndpoints(endpoints =>
            {
                endpoints.MapRoomWebsocketEndpoint("/api/rooms/{id}");
                endpoints.MapControllerRoute(
                    name: "default",
                    pattern: "{controller}/{action=Index}/{id?}");
            });

            if (!env.IsEnvironment("Test"))
            {
                app.UseSpa(spa =>
                {
                    spa.Options.SourcePath = "ClientApp";

                    if (env.IsDevelopment())
                    {
                        spa.UseReactDevelopmentServer(npmScript: "start");
                    }
                });
            }
        }
    }
}
