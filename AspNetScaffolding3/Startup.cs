﻿using AspNetCoreRateLimit;
using AspNetScaffolding.Extensions.AccountId;
using AspNetScaffolding.Extensions.Cache;
using AspNetScaffolding.Extensions.Cors;
using AspNetScaffolding.Extensions.CultureInfo;
using AspNetScaffolding.Extensions.Docs;
using AspNetScaffolding.Extensions.ExceptionHandler;
using AspNetScaffolding.Extensions.GracefullShutdown;
using AspNetScaffolding.Extensions.Healthcheck;
using AspNetScaffolding.Extensions.JsonFieldSelector;
using AspNetScaffolding.Extensions.JsonSerializer;
using AspNetScaffolding.Extensions.Logger;
using AspNetScaffolding.Extensions.Mapper;
using AspNetScaffolding.Extensions.QueryFormatter;
using AspNetScaffolding.Extensions.Queue;
using AspNetScaffolding.Extensions.RequestKey;
using AspNetScaffolding.Extensions.RequestLimit;
using AspNetScaffolding.Extensions.RoutePrefix;
using AspNetScaffolding.Extensions.TimeElapsed;
using AspNetScaffolding.Utilities;
using AspNetSerilog;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RestSharp.Serilog.Auto;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AspNetScaffolding
{
    public class Startup
    {
        public Startup()
        {
            var envName = EnvironmentUtility.GetCurrentEnvironment();

            var builder = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile($"appsettings.json", optional: true, reloadOnChange: true)
                .AddJsonFile($"appsettings.{envName}.json", optional: true, reloadOnChange: true)
                .AddEnvironmentVariables(Api.ApiBasicConfiguration?.EnvironmentVariablesPrefix);

            Api.ConfigurationRoot = builder.Build();

            Api.ConfigurationRoot.GetSection("ApiSettings").Bind(Api.ApiSettings);
            Api.ConfigurationRoot.GetSection("HealthcheckSettings").Bind(Api.HealthcheckSettings);
            Api.ConfigurationRoot.GetSection("LogSettings").Bind(Api.LogSettings);
            Api.ConfigurationRoot.GetSection("QueueSettings").Bind(Api.QueueSettings);
            Api.ConfigurationRoot.GetSection("DatabaseSettings").Bind(Api.DatabaseSettings);
            Api.ConfigurationRoot.GetSection("DocsSettings").Bind(Api.DocsSettings);
            Api.ConfigurationRoot.GetSection("ShutdownSettings").Bind(Api.ShutdownSettings);
            Api.ConfigurationRoot.GetSection("CacheSettings").Bind(Api.CacheSettings);

            //Create this lógic to keep retrocompatibility because we change the IpRateLimiting section name to RateLimiting
            var ipRateLimitingSection = Api.ConfigurationRoot.GetSection("IpRateLimiting");

            if (ipRateLimitingSection.Exists())
            {
                ipRateLimitingSection.Bind(Api.RateLimitingAdditional);
            }
            else
            {
                Api.ConfigurationRoot.GetSection("RateLimiting").Bind(Api.RateLimitingAdditional);
            }
        }

        public void ConfigureServices(IServiceCollection services)
        {
            services.AddSingleton(Api.ConfigurationRoot);
            services.AddHttpContextAccessor();

            services.AddOptions();
            services.SetupCache(Api.CacheSettings);

            services.SetupIpRateLimiting(Api.RateLimitingAdditional, Api.CacheSettings);
            services.SetupSwaggerDocs(Api.DocsSettings, Api.ApiSettings);

            services.AddControllers(mvc => { mvc.EnableEndpointRouting = false; });

            var mvc = services.AddMvc(options => options.EnableEndpointRouting = false);
            mvc.RegisterAssembliesForControllers(Api.ApiBasicConfiguration?.AutoRegisterAssemblies);
            mvc.RegisterAssembliesForFluentValidation(Api.ApiBasicConfiguration?.AutoRegisterAssemblies);
            mvc.ConfigureJsonSettings(services,
                Api.ApiSettings.JsonSerializer,
                Api.ApiSettings?.TimezoneHeader,
                Api.ApiSettings?.TimezoneDefaultInfo);

            mvc.AddMvcOptions(options =>
            {
                options.UseCentralRoutePrefix(Api.ApiSettings.GetPathPrefixConsideringVersion());
                options.AddQueryFormatter(Api.ApiSettings.JsonSerializer);
                options.AddPathFormatter(Api.ApiSettings.JsonSerializer);
            });

            services.AddScoped<IRestClientFactory, RestClientFactory>();

            services.SetupAllowCors();
            services.SetupRequestKey(Api.ApiSettings?.RequestKeyProperty);
            services.SetupAccountId(Api.ApiSettings?.AccountIdProperty);
            services.SetupTimeElapsed(Api.ApiSettings?.TimeElapsedProperty);

            List<string> ignoredRoutes = Api.DocsSettings.GetDocsFinalRoutes().ToList();

            if (Api.HealthcheckSettings.LogEnabled == false)
            {
                ignoredRoutes.Add(HealthcheckkMiddlewareExtension.GetFullPath(Api.ApiSettings, Api.HealthcheckSettings));
            }

            services.SetupSerilog(Api.ApiSettings?.Domain,
                Api.ApiSettings?.Application,
                Api.LogSettings,
                ignoredRoutes);

            services.SetupAutoMapper();
            services.SetupQueue();

            Api.ApiBasicConfiguration.ConfigureServices?.Invoke(services);

            services.AddGracefullShutdown();

            services.SetupHealthcheck(Api.ApiSettings,
                Api.HealthcheckSettings,
                Api.ApiBasicConfiguration.ConfigureHealthcheck);
        }

        public void Configure(IApplicationBuilder app, IWebHostEnvironment en)
        {
            Api.ApiBasicConfiguration.ConfigureBefore?.Invoke(app);

            app.UseGracefullShutdown();
            app.UseAspNetSerilog();
            app.UseAccountId();
            app.UseRequestKey();
            app.UseTimeElapsed();
            app.UseScaffoldingSwagger();
            app.UseScaffoldingRequestLocalization(Api.ApiSettings?.SupportedCultures);
            app.UseScaffoldingExceptionHandler();
            app.UseHealthcheck();
            app.UseRouting();
            app.AllowCors();

            if (Api.ApiSettings.UseStaticFiles)
            {
                var path = Api.ApiSettings.GetStaticFilesPath();
                Console.WriteLine("StaticFiles Path: {0}", path);
                app.UseStaticFiles(path);
            }

            if (Api.RateLimitingAdditional?.Enabled == true)
            {
                if (Api.RateLimitingAdditional.ByClientIdHeader)
                {
                    app.UseClientRateLimiting();
                }
                else
                {
                    app.UseIpRateLimiting();
                }
            }

            Api.ApiBasicConfiguration.Configure?.Invoke(app);

            app.UseJsonFieldSelector(Api.ApiSettings.JsonFieldSelectorProperty, Api.ApiSettings.IsJsonFieldSelectorEnabled);

            app.UseMvc();
            app.UseEndpoints(endpoints =>
            {
                endpoints.MapControllers();
            });

            Api.ApiBasicConfiguration.ConfigureAfter?.Invoke(app);
        }

        public static void ConfigureHealthcheck(IHealthChecksBuilder builder, IServiceProvider provider)
        {
            builder.AddUrlGroup(new Uri("https://www.google.com"), "google");
        }
    }
}