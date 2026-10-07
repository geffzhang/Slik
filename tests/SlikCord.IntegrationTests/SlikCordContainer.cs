using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Slik.Cord.IntegrationTests
{
    
    public class SlikCordContainer
    {
        private const string NetFramework = "10.0";
        public const ushort HostPort = 3099;
        
        public const ushort ContainerPort = 3100;
        public readonly string ImageName = $"test-slik-cord:{NetFramework}";
        public readonly string ContainerId = $"test-slik-cord-{NetFramework}";

        private readonly Task? _prepareTask;
        private bool _isContainerReady;

        // Using singleton because we do not want to recreate the container for each test
        public static SlikCordContainer Instance { get; } = new SlikCordContainer(recreateContainer: false);

        public async ValueTask EnsureReady()
        { 
            if (!_isContainerReady && _prepareTask != null && !_prepareTask.IsCompleted)
            {
                await _prepareTask;
            }

            if (!_isContainerReady)
                throw new Exception("Container is not ready");
        }

        public SlikCordContainer(bool recreateContainer)
        {
            var docker = new DockerProcess();
            bool exists = docker.DoesContainerExistAsync(ContainerId).Result;

            if (recreateContainer || !exists)
            {
                _prepareTask = PrepareContainerAsync();
            }
            else
                _isContainerReady = true;
        }

        private async Task PrepareContainerAsync()
        {
            var docker = new DockerProcess();

            Console.WriteLine("Cleaning up previously allocated container.");
            await RemoveContainerAsync();

            Console.WriteLine($"Building a new image '{ImageName}'.");
            await docker.BuildAsync(
                tag: ImageName,
                folder: "..\\..\\..\\..\\..",
                "src/SlikCord/Dockerfile");

            Console.WriteLine($"Running the container '{ContainerId}'.");
            await docker.RunAsync(ContainerId, ImageName, $"{HostPort}:{ContainerPort}");

            Console.WriteLine("Waiting for the container.");
            await WaitForHttpEndpointAsync(TimeSpan.FromSeconds(10));
        }

        private async Task WaitForHttpEndpointAsync(TimeSpan timeout)
        {
            using var client = new HttpClient();
            using var cts = new CancellationTokenSource(timeout);
            Exception? lastException = null;

            while (!cts.IsCancellationRequested)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, $"http://localhost:{HostPort}")
                {
                    Version = HttpVersion.Version20,
                    VersionPolicy = HttpVersionPolicy.RequestVersionExact
                };

                try
                {
                    using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                    if (response.Version == HttpVersion.Version20)
                        _isContainerReady = true;

                    if (_isContainerReady)
                        return;
                }
                catch (HttpRequestException exception)
                {
                    lastException = exception;
                }
                catch (OperationCanceledException) when (cts.IsCancellationRequested)
                {
                    break;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(250), cts.Token);
            }

            throw new TimeoutException("The Slik.Cord container did not establish an HTTP/2 connection before the startup timeout.", lastException);
        }

        public async Task RemoveContainerAsync()
        {
            var docker = new DockerProcess();

            try
            {                
                await docker.StopAsync(ContainerId);                
            }
            catch 
            {
                // container not found
            }

            try
            {
                await docker.RemoveContainerAsync(ContainerId);
            }
            catch
            {
                // container not found
            }
        }
    }
}
