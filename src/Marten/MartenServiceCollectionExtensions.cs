using System;
using System.Composition.Hosting.Core;
using LamarCodeGeneration;
using Marten.Events.Daemon;
using Marten.Events.Daemon.HighWater;
using Marten.Events.Daemon.Resiliency;
using Marten.Events.Projections;
using Marten.Linq.QueryHandlers;
using Marten.Schema;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
#nullable enable
namespace Marten
{
    public static class MartenServiceCollectionExtensions
    {
        /// <summary>
        /// Add Marten IDocumentStore, IDocumentSession, and IQuerySession service registrations
        /// to your application with the given Postgresql connection string and Marten
        /// defaults
        /// </summary>
        /// <param name="services"></param>
        /// <param name="connectionString">The connection string to your application's Postgresql database</param>
        /// <returns></returns>
        public static MartenConfigurationExpression AddMarten(this IServiceCollection services, string connectionString)
        {
            var options = new StoreOptions();
            options.Connection(connectionString);
            return services.AddMarten(options);
        }

        /// <summary>
        /// Add Marten IDocumentStore, IDocumentSession, and IQuerySession service registrations
        /// to your application using the configured StoreOptions
        /// </summary>
        /// <param name="services"></param>
        /// <param name="options">The Marten configuration for this application</param>
        /// <returns></returns>
        public static MartenConfigurationExpression AddMarten(this IServiceCollection services, StoreOptions options)
        {
            services.AddMarten(s => options);
            return new MartenConfigurationExpression(services, options);
        }

        /// <summary>
        /// Add Marten IDocumentStore, IDocumentSession, and IQuerySession service registrations
        /// to your application by configuring a StoreOptions using services in your DI container
        /// </summary>
        /// <param name="optionSource">Func that will build out a StoreOptions with the applications IServiceProvider as the input</param>
        /// <returns></returns>
        public static MartenConfigurationExpression AddMarten(this IServiceCollection services, Func<IServiceProvider, StoreOptions> optionSource)
        {
            services.AddSingleton<StoreOptions>(optionSource);

            services.AddSingleton<IDocumentStore>(s =>
            {
                var options = s.GetRequiredService<StoreOptions>();
                if (options.Logger().GetType() != typeof(NulloMartenLogger)) return new DocumentStore(options);
                var logger = s.GetService<ILogger<IDocumentStore>>() ?? new NullLogger<IDocumentStore>();
                options.Logger(new DefaultMartenLogger(logger));

                return new DocumentStore(options);
            });

            // This can be overridden by the expression following
            services.AddSingleton<ISessionFactory, DefaultSessionFactory>();
            services.AddSingleton<GenerationRules>(new GenerationRules(SchemaConstants.MartenGeneratedNamespace));

            // To support LamarCodeGeneration.Commands
            services.AddSingleton<DynamicCodeBuilder>();


            services.AddScoped(s => s.GetRequiredService<ISessionFactory>().QuerySession());
            services.AddScoped(s => s.GetRequiredService<ISessionFactory>().OpenSession());

            services.AddHostedService<AsyncProjectionHostedService>();

            services.AddSingleton<IGeneratesCode[]>(s =>
            {
                var options = s.GetRequiredService<StoreOptions>();
                return new IGeneratesCode[] {options.EventGraph, options};
            });

            return new MartenConfigurationExpression(services, null);
        }

        /// <summary>
        /// Add Marten IDocumentStore, IDocumentSession, and IQuerySession service registrations
        /// to your application using the configured StoreOptions
        /// </summary>
        /// <param name="services"></param>
        /// <param name="configure"></param>
        /// <returns></returns>
        public static MartenConfigurationExpression AddMarten(this IServiceCollection services, Action<StoreOptions> configure)
        {
            var options = new StoreOptions();
            configure(options);

            return services.AddMarten(options);
        }

        public class MartenConfigurationExpression
        {
            private readonly StoreOptions? _options;

            internal MartenConfigurationExpression(IServiceCollection services, StoreOptions? options)
            {
                Services = services;
                _options = options;
            }

            /// <summary>
            /// Use an alternative strategy / configuration for opening IDocumentSession or IQuerySession
            /// objects in the application with a custom ISessionFactory type registered as a singleton
            /// </summary>
            /// <param name="lifetime">IoC service lifetime for the session factory. Default is Singleton, but use Scoped if you need to reference per-scope services</param>
            /// <typeparam name="T"></typeparam>
            /// <returns></returns>
            public MartenConfigurationExpression BuildSessionsWith<T>(ServiceLifetime lifetime = ServiceLifetime.Singleton ) where T : class, ISessionFactory
            {
                Services.Add(new ServiceDescriptor(typeof(ISessionFactory), typeof(T), lifetime));
                return this;
            }

            /// <summary>
            /// Gets the IServiceCollection
            /// </summary>
            public IServiceCollection Services { get; }

            /// <summary>
            /// Use lightweight sessions by default for the injected IDocumentSession objects. Equivalent to IDocumentStore.LightweightSession();
            /// </summary>
            /// <returns></returns>
            public MartenConfigurationExpression UseLightweightSessions()
            {
                BuildSessionsWith<LightweightSessionFactory>();
                return this;
            }

            /// <summary>
            /// Eagerly build the application's DocumentStore during application
            /// bootstrapping rather than waiting for the first usage of IDocumentStore
            /// at runtime.
            /// </summary>
            /// <returns></returns>
            public IDocumentStore InitializeStore()
            {
                if (_options == null)
                    throw new InvalidOperationException(
                        "This operation is not valid when the StoreOptions is built by Func<IServiceProvider, StoreOptions>");

                var store = new DocumentStore(_options);
                Services.AddSingleton<IDocumentStore>(store);

                return store;
            }

        }
    }

    /// <summary>
    /// Pluggable strategy for customizing how IDocumentSession / IQuerySession
    /// objects are created within an application.
    /// </summary>
    public interface ISessionFactory
    {
        /// <summary>
        /// Build new instances of IQuerySession on demand
        /// </summary>
        /// <returns></returns>
        IQuerySession QuerySession();

        /// <summary>
        /// Build new instances of IDocumentSession on demand
        /// </summary>
        /// <returns></returns>
        IDocumentSession OpenSession();
    }

    internal class DefaultSessionFactory: ISessionFactory
    {
        private readonly IDocumentStore _store;

        public DefaultSessionFactory(IDocumentStore store)
        {
            _store = store;
        }

        public IQuerySession QuerySession()
        {
            return _store.QuerySession();
        }

        public IDocumentSession OpenSession()
        {
            return _store.OpenSession();
        }
    }

    internal class LightweightSessionFactory: ISessionFactory
    {
        private readonly IDocumentStore _store;

        public LightweightSessionFactory(IDocumentStore store)
        {
            _store = store;
        }

        public IQuerySession QuerySession()
        {
            return _store.QuerySession();
        }

        public IDocumentSession OpenSession()
        {
            return _store.LightweightSession();
        }
    }
}
