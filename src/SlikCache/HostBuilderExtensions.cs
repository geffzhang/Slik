using DotNext.Net.Cluster.Consensus.Raft;
using DotNext.Net.Cluster.Consensus.Raft.Http;
using DotNext.Net.Cluster.Consensus.Raft.StateMachine;
using DotNext.Net.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ProtoBuf.Grpc.Server;
using Slik.Cache.Grpc.V1;
using Slik.Security;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;

namespace Slik.Cache
{
    public static class HostBuilderExtensions
    {
        public static IHostBuilder UseSlik(this IHostBuilder builder, SlikOptions? cacheOptions = null)
        {
            cacheOptions ??= new SlikOptions
            {
                Host = new IPEndPoint(IPAddress.Loopback, SlikOptions.DefaultPort)
            };

            // updating configuration
            string folder = string.IsNullOrEmpty(cacheOptions.DataFolder)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Slik")
                : cacheOptions.DataFolder;
            cacheOptions.DataFolder = folder;
            string cacheLocation = Path.Combine(folder, "Cache-v6");
            string clusterConfigurationLocation = Path.Combine(folder, "Cluster-v6");
            string localMemberAddress = SlikMembershipHandler.NormalizeMemberAddress($"https://{cacheOptions.Host}");
            string? bootstrapMemberAddress = cacheOptions.Members
                .Select(SlikMembershipHandler.NormalizeMemberAddress)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(member => member, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            bool isBootstrapNode = bootstrapMemberAddress is null
                || string.Equals(localMemberAddress, bootstrapMemberAddress, StringComparison.OrdinalIgnoreCase);

            var nodeConfiguration = new Dictionary<string, string>
            {
                { "folder", folder },
                { "cacheLogLocation", cacheLocation },
                { "port", cacheOptions.Host.Port.ToString() }
            };

            int i = 0;
            foreach (string member in cacheOptions.Members)
                nodeConfiguration[$"members:{i++}"] = member;

            builder
                .ConfigureAppConfiguration(builder => builder.AddInMemoryCollection(nodeConfiguration))

                .ConfigureWebHostDefaults(webBuilder => webBuilder
                    .Configure((context, app) =>
                    {
                        app.UseConsensusProtocolHandler();

                        if (cacheOptions.EnableGrpcApi)
                        {
                            (context.HostingEnvironment.IsDevelopment() ? app.UseDeveloperExceptionPage() : app)
                                .UseRouting()
                                .UseEndpoints(endpoints =>
                                {
                                    endpoints.MapGrpcService<SlikMembershipGrpcService>();
                                    endpoints.MapGrpcService<SlikCacheGrpcService>();
                                });
                        }
                    })
                    .ConfigureKestrel((context, options) =>
                    {
                        var certifier = options.ApplicationServices.GetRequiredService<ICommunicationCertifier>();
                        options.ConfigureHttpsDefaults(options => certifier.SetupServer(options));

                        // TODO check why we can't use Listen() in both cases
                        if (cacheOptions.Host.Address == IPAddress.Loopback)
                            options.ListenLocalhost(cacheOptions.Host.Port, opt => opt.UseHttps().Protocols = HttpProtocols.Http2);
                        else
                            options.Listen(cacheOptions.Host, opt => opt.UseHttps().Protocols = HttpProtocols.Http2);

                    }))
                .ConfigureServices(services =>
                {
                    if (cacheOptions.CertificateOptions.UseSelfSigned)
                        services.AddSingleton<ICommunicationCertifier, SelfSignedCertifier>();
                    else
                        services.AddSingleton<ICommunicationCertifier, CaSignedCertifier>();

                    services
                        .AddTransient<ICertificateGenerator, CertificateGenerator>()
                        .Configure<CertificateOptions>(options => cacheOptions.CertificateOptions.CopyTo(options))
                        .AddSingleton<IHttpMessageHandlerFactory, RaftClientHandlerFactory>()
                        .Configure<SlikOptions>(options => cacheOptions.CopyTo(options))
                        .AddSingleton<IDistributedCache>(provider => provider.GetRequiredService<SlikCache>())
                        .UsePersistentConfigurationStorage(clusterConfigurationLocation)
                        .UseStateMachine<SlikCache>(new WriteAheadLog.Options
                        {
                            Location = cacheLocation,
                            FlushInterval = Timeout.InfiniteTimeSpan
                        })
                        .AddHostedService<SlikRouter>();

                    services.Replace(ServiceDescriptor.Singleton<SlikCache>(provider =>
                    {
                        var cache = ActivatorUtilities.CreateInstance<SlikCache>(provider);
                        cache.RestoreAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
                        return cache;
                    }));

                    if (cacheOptions.EnableGrpcApi)
                    {
                        services
                            .AddSingleton<SlikMembershipHandler>()
                            .AddSingleton<ISlikMembership>(ServiceProviderServiceExtensions.GetRequiredService<SlikMembershipHandler>)
                            .AddCodeFirstGrpc();
                    }
                })
                .JoinCluster((memberConfiguration, _, _) =>
                {
                    memberConfiguration.PublicEndPoint = new Uri($"https://{cacheOptions.Host}");
                    memberConfiguration.ProtocolVersion = HttpProtocolVersion.Http2;
                    memberConfiguration.ProtocolVersionPolicy = HttpVersionPolicy.RequestVersionExact;
                    memberConfiguration.ColdStart = !cacheOptions.EnableGrpcApi || isBootstrapNode;
                });

            return builder;
        }
    }
}