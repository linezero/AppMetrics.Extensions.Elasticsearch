﻿using System;
using App.Metrics.Configuration;
using App.Metrics.Elasticsearch.Sandbox.JustForTesting;
using App.Metrics.Extensions.Reporting.ElasticSearch;
using App.Metrics.Extensions.Reporting.ElasticSearch.Client;
using App.Metrics.Filtering;
using App.Metrics.Reporting.Interfaces;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace App.Metrics.Elasticsearch.Sandbox
{
    public class Startup
    {
        private static readonly string ElasticSearchIndex = "appmetricssandbox";
        private static readonly Uri ElasticSearchUri = new Uri("http://127.0.0.1:9200");
        private static readonly bool HaveAppRunSampleRequests = true;
        private static readonly bool RunSamplesWithClientId = true;

        public Startup(IHostingEnvironment env)
        {
            var builder = new ConfigurationBuilder().SetBasePath(env.ContentRootPath).
                                                     AddJsonFile("appsettings.json", optional: false, reloadOnChange: true).
                                                     AddJsonFile($"appsettings.{env.EnvironmentName}.json", optional: true).
                                                     AddEnvironmentVariables();

            Configuration = builder.Build();
        }

        public IConfigurationRoot Configuration { get; }

        public void Configure(IApplicationBuilder app, IHostingEnvironment env, ILoggerFactory loggerFactory, IApplicationLifetime lifetime)
        {
            if (RunSamplesWithClientId && HaveAppRunSampleRequests)
            {
                app.Use(
                    (context, func) =>
                    {
                        RandomClientIdForTesting.SetTheFakeClaimsPrincipal(context);
                        return func();
                    });
            }

            loggerFactory.AddConsole(Configuration.GetSection("Logging"));
            // loggerFactory.AddDebug();

            app.UseMetrics();
            app.UseMetricsReporting(lifetime);

            app.UseMvc();

            if (HaveAppRunSampleRequests)
            {
                SampleRequests.Run(lifetime.ApplicationStopping);
            }
        }

        public void ConfigureServices(IServiceCollection services)
        {
            services.AddTestStuff();
            services.AddLogging().AddRouting(options => { options.LowercaseUrls = true; });

            services.AddMvc(options => options.AddMetricsResourceFilter());

            var reportFilter = new DefaultMetricsFilter();
            reportFilter.WithHealthChecks(false);

            services.AddMetrics(
                         Configuration.GetSection("AppMetrics"),
                         options =>
                         {
                             options.WithGlobalTags(
                                 (globalTags, info) =>
                                 {
                                     globalTags.Add("app", info.EntryAssemblyName);
                                     globalTags.Add("server", info.MachineName);
                                     globalTags.Add("version", info.EntryAssemblyVersion);
                                 });
                         }).
                     AddJsonSerialization().
                     AddReporting(
                         factory =>
                         {
                             factory.AddElasticSearch(
                                 new ElasticSearchReporterSettings
                                 {
                                     HttpPolicy = new HttpPolicy
                                                  {
                                                      FailuresBeforeBackoff = 3,
                                                      BackoffPeriod = TimeSpan.FromSeconds(30),
                                                      Timeout = TimeSpan.FromSeconds(10)
                                                  },
                                     ElasticSearchSettings = new ElasticSearchSettings(ElasticSearchUri, ElasticSearchIndex),
                                     ReportInterval = TimeSpan.FromSeconds(5)
                                 },
                                 reportFilter);
                         }).
                     AddHealthChecks(
                         factory =>
                         {
                             factory.RegisterPingHealthCheck("google ping", "google.com", TimeSpan.FromSeconds(10));
                             factory.RegisterHttpGetHealthCheck("github", new Uri("https://github.com/"), TimeSpan.FromSeconds(10));
                         }).
                     AddMetricsMiddleware(Configuration.GetSection("AspNetMetrics"));
        }
    }
}