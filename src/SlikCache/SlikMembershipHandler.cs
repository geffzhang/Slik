using DotNext.Net.Cluster.Consensus.Raft.Http;
using Grpc.Net.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProtoBuf.Grpc.Client;
using Slik.Cache.Grpc.V1;
using Slik.Security;
using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Slik.Cache
{
    public interface ISlikMembership
    {
        Task Add(string member, CancellationToken token);
        Task Remove(string member, CancellationToken token);
    }

    public class MembershipChangeRecord
    {
        public enum MemebershipOperation { Add, Remove };

        public MemebershipOperation Operation { get; set; }
        public string Member { get; set; } = "";
    }

    internal class SlikMembershipHandler : ISlikMembership
    {
        private readonly ILogger<SlikMembershipHandler> _logger;
        private readonly IOptionsMonitor<SlikOptions> _options;
        private readonly IRaftHttpCluster _cluster;
        private readonly SemaphoreSlim _membershipChangeLock = new(1, 1);

        public SlikMembershipHandler(ILogger<SlikMembershipHandler> logger,
            IHttpMessageHandlerFactory httpHandlerFactory, IOptionsMonitor<SlikOptions> options, IRaftHttpCluster cluster)
        {
            _logger = logger;
            _options = options;

            _cluster = cluster;

            _ = UpdateClusterMembershipAsync(httpHandlerFactory);
        }

        private async Task UpdateClusterMembershipAsync(IHttpMessageHandlerFactory httpHandlerFactory)
        {
            try
            {
                string[] members = _options.CurrentValue.Members
                    .Select(NormalizeMemberAddress)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(member => member, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                string localMember = NormalizeMemberAddress($"https://{_options.CurrentValue.Host}");
                string? bootstrapMember = members.FirstOrDefault();

                if (bootstrapMember is not null && !string.Equals(localMember, bootstrapMember, StringComparison.OrdinalIgnoreCase))
                {
                    using var httpHandler = httpHandlerFactory.CreateHandler();

                    const int maxAttempts = 10;
                    for (int attempt = 0; attempt < maxAttempts; attempt++)
                    {
                        _logger.LogDebug($"Trying to contact bootstrap member '{bootstrapMember}' for adding this node.");
                        try
                        {
                            using var channel = GrpcChannel.ForAddress(bootstrapMember, new GrpcChannelOptions { HttpHandler = httpHandler });
                            var service = channel.CreateGrpcService<ISlikMembershipService>();
                            await service.Add(new MemberRequest { Member = localMember }).ConfigureAwait(false);
                            return;
                        }
                        catch (Exception e)
                        {
                            _logger.LogWarning(e, $"Error contacting bootstrap member '{bootstrapMember}' (attempt {attempt + 1} of {maxAttempts}).");
                            if (attempt == maxAttempts - 1)
                                throw new InvalidOperationException($"Unable to join bootstrap cluster member '{bootstrapMember}'.", e);

                            await Task.Delay(300).ConfigureAwait(false);
                        }
                    }
                }
                else
                    _logger.LogDebug("This node is the bootstrap member or no cluster members were configured.");
            }
            catch (Exception e)
            {
                _logger.LogCritical(e, "Error while trying to get added to a cluster");
            }
        }

        internal static string NormalizeMemberAddress(string member)
        {
            var address = new Uri(member.Replace("localhost", "127.0.0.1", StringComparison.OrdinalIgnoreCase));
            return address.ToString();
        }

        private async Task ChangeMembershipAsync(MembershipChangeRecord record, CancellationToken token)
        {
            await _membershipChangeLock.WaitAsync(token).ConfigureAwait(false);
            try
            {
                bool handled = RedirectHandler != null && await RedirectHandler(record, token).ConfigureAwait(false);

                if (!handled)
                {
                    record.Member = record.Member.Replace("localhost", "127.0.0.1", StringComparison.OrdinalIgnoreCase);
                    var memberAddress = new Uri(record.Member);
                    switch (record.Operation)
                    {
                        case MembershipChangeRecord.MemebershipOperation.Add:
                            await _cluster.AddMemberAsync(memberAddress, token).ConfigureAwait(false);
                            break;
                        case MembershipChangeRecord.MemebershipOperation.Remove:
                            await _cluster.RemoveMemberAsync(memberAddress, token).ConfigureAwait(false);
                            break;
                    }
                }
            }
            finally
            {
                _membershipChangeLock.Release();
            }
        }

        public async Task Add(string member, CancellationToken token)
        {
            var record = new MembershipChangeRecord { Member = member, Operation = MembershipChangeRecord.MemebershipOperation.Add };

            await ChangeMembershipAsync(record, token).ConfigureAwait(false);
        }

        public async Task Remove(string member, CancellationToken token)
        {
            var record = new MembershipChangeRecord { Member = member, Operation = MembershipChangeRecord.MemebershipOperation.Remove };

            await ChangeMembershipAsync(record, token).ConfigureAwait(false);
        }

        // delegate to handle leader redirection
        internal event Func<MembershipChangeRecord, CancellationToken, ValueTask<bool>>? RedirectHandler;
    }
}
