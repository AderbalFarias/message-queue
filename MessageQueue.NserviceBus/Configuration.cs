﻿using MessageQueue.Domain.Entities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NServiceBus;
using NServiceBus.Logging;
using System;
using System.Data.SqlClient;
using System.IO;

namespace MessageQueue.NserviceBus
{
    public static class Configuration
    {
        /// <summary>
        ///     Method to create initial confiration for NServiceBus endpoints
        /// </summary>
        /// <param name="hostContext">Host context</param>
        /// <param name="services">Services injected</param>
        /// <param name="connectionName">Connection string name</param>
        /// <param name="nServiceBusSection">NServiceBus settings name</param>
        /// <param name="appSection">App settings name</param>
        /// <param name="endpointStart">Endpoint should be started</param>
        /// <param name="messageTypeRoute">Message type in case of route to an endpoint</param>
        /// <param name="messageTypePublisher">Message type in case of subscription to an endpoint</param>
        /// <returns></returns>
        public static void Register
        (
            HostBuilderContext hostContext,
            IServiceCollection services,
            string connectionName,
            string nServiceBusSection,
            string appSection,
            bool endpointStart = false,
            Type messageTypeRoute = null,
            Type messageTypePublisher = null
        )
        {
            var serviceBusSettings = hostContext.Configuration
                .GetSection(nServiceBusSection)
                .Get<NServiceBusSettings>();

            var endpointConfiguration = new EndpointConfiguration(serviceBusSettings.ProjectEndpoint);

            endpointConfiguration.TransportConfig(hostContext, serviceBusSettings,
                connectionName, messageTypeRoute, messageTypePublisher);

            endpointConfiguration.EnableInstallers();
            endpointConfiguration.AutoSubscribe();
            endpointConfiguration.UsePersistence<SqlPersistence>();
            endpointConfiguration.UseContainer<ServicesBuilder>(c => c.ExistingServices(services));
            endpointConfiguration.AuditProcessedMessagesTo(serviceBusSettings.AuditProcessedMessagesTo);
            endpointConfiguration.SendFailedMessagesTo(serviceBusSettings.SendFailedMessagesTo);

            if (serviceBusSettings.UseHeartbeat)
                endpointConfiguration.SendHeartbeatTo(serviceBusSettings.SendFailedMessagesTo);

            if (serviceBusSettings.UseRetry)
                endpointConfiguration.RetryConfig(serviceBusSettings);

            if (serviceBusSettings.UseMetrics)
                endpointConfiguration.MetricsConfig(serviceBusSettings);

            endpointConfiguration.PersistenceConfig(hostContext, serviceBusSettings, connectionName);

            if (!Directory.Exists(serviceBusSettings.PathToLog))
                Directory.CreateDirectory(serviceBusSettings.PathToLog);

            var defaultFactory = LogManager.Use<DefaultFactory>();
            defaultFactory.Directory(serviceBusSettings.PathToLog);

            endpointConfiguration.RegisterComponents(
                registration: configureComponents =>
                {
                    configureComponents.RegisterSingleton(hostContext
                            .Configuration.GetSection(appSection).Get<AppSettings>());
                });

            var endpointInstance = Endpoint.Start(endpointConfiguration).GetAwaiter().GetResult();
            services.AddSingleton(endpointInstance);
            services.AddSingleton<IMessageSession>(endpointInstance);
        }

        private static void TransportConfig(this EndpointConfiguration endpointConfiguration,
            HostBuilderContext hostContext, NServiceBusSettings serviceBusSettings,
            string connectionName, Type messageTypeRoute = null, Type messageTypePublisher = null)
        {
            var transport = endpointConfiguration.UseTransport<SqlServerTransport>();

            transport.ConnectionString(hostContext.Configuration.GetConnectionString(connectionName));
            transport.Transactions(TransportTransactionMode.SendsAtomicWithReceive);

            if (messageTypeRoute != null && !string.IsNullOrEmpty(serviceBusSettings.RouteToEndpoint))
                transport.Routing().RouteToEndpoint(messageTypeRoute, serviceBusSettings.RouteToEndpoint);

            // Previous versions 
            //if (messageTypePublisher != null && !string.IsNullOrEmpty(serviceBusSettings.SubscribeToEndpoint))
            //    transport.Routing().RegisterPublisher(messageTypePublisher, serviceBusSettings.SubscribeToEndpoint);
        }

        private static void PersistenceConfig(this EndpointConfiguration endpointConfiguration,
            HostBuilderContext hostContext, NServiceBusSettings serviceBusSettings, string connectionName)
        {
            var persistence = endpointConfiguration.UsePersistence<SqlPersistence>();

            persistence.SqlDialect<SqlDialect.MsSqlServer>();
            persistence.ConnectionBuilder(
                connectionBuilder: () =>
                {
                    return new SqlConnection(hostContext.Configuration.GetConnectionString(connectionName));
                });

            var subscriptions = persistence.SubscriptionSettings();
            subscriptions.CacheFor(TimeSpan.FromMinutes(serviceBusSettings.SubscriptionCacheForInMinutes));
        }

        private static void RetryConfig(this EndpointConfiguration endpointConfiguration,
            NServiceBusSettings serviceBusSettings)
        {
            var recoverability = endpointConfiguration.Recoverability();

            recoverability.Immediate(
               immediate =>
               {
                   immediate.NumberOfRetries(serviceBusSettings.NumberOfImmediateRetries);
               });

            recoverability.Delayed(
               delayed =>
               {
                   delayed.NumberOfRetries(serviceBusSettings.NumberOfDelayedRetries);
                   delayed.TimeIncrease(TimeSpan.FromSeconds(serviceBusSettings.RecoverabilityTimeIncreaseInSeconds));
               });
        }

        private static void MetricsConfig(this EndpointConfiguration endpointConfiguration,
            NServiceBusSettings serviceBusSettings)
        {
            var metrics = endpointConfiguration.EnableMetrics();

            metrics.SendMetricDataToServiceControl(serviceBusSettings.SendMetricDataToServiceControl,
               TimeSpan.FromMilliseconds(serviceBusSettings.MetricsIntervalInMilliseconds));
        }
    }
}
