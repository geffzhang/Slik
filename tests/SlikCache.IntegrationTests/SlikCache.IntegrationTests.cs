using Grpc.Net.Client;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using ProtoBuf.Grpc.Client;
using Slik.Cache.Grpc.V1;
using Slik.Security;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

namespace Slik.Cache.IntegrationTests
{
    [TestClass]
    [TestCategory(".NET 10")]
    public class SilkCacheIntegrationTests
    {
#if DEBUG
        private const string TestProjectPath = "..\\..\\..\\..\\..\\examples\\SlikNode\\bin\\Debug\\net10.0\\SlikNode.exe";
#else
        private const string TestProjectPath = "..\\..\\..\\..\\..\\examples\\SlikNode\\bin\\Release\\net10.0\\SlikNode.exe";
#endif

        private static readonly HttpMessageHandler _httpHandler;
        private static readonly X509Certificate2 _certificate;

        [ClassCleanup]
        public static void Cleanup()
        {
            _httpHandler.Dispose();
            _certificate.Dispose();
        }

        static SilkCacheIntegrationTests()
        {
            var generator = new CertificateGenerator(Mock.Of<ILogger<CertificateGenerator>>());
            var certifier = new SelfSignedCertifier(Options.Create(new CertificateOptions { UseSelfSigned = true }), generator, Mock.Of<ILogger<SelfSignedCertifier>>());
            var rootCertificate = certifier.RootCertificate;                                        

            //_certificate = Node.Startup.LoadCertificate("node.pfx");
            _certificate = generator.Generate("test client cert", rootCertificate, CertificateAuthentication.Client);

            var certifierMock = new Mock<ICommunicationCertifier>();
            certifierMock.Setup(c => c.SetupClient(It.IsAny<SslClientAuthenticationOptions>())).Callback<SslClientAuthenticationOptions>(opt =>
            {
                opt.ClientCertificates = new(new[] { _certificate });
                opt.RemoteCertificateValidationCallback = (_, __, ___, ____) => true;
            });

            _httpHandler = new RaftClientHandlerFactory(certifierMock.Object).CreateHandler("");
        }

        private static Task RunInstances(int instanceCount, string executable, int startPort, string dataFolder, Func<int, string> memberListFunc, string? arguments = null, CancellationToken token = default)
        {
            List<Process> processList = new();

            for (int n = 0; n < instanceCount; n++)
            {
                string path = Path.Combine(dataFolder, $"{startPort + n}");

                var newProcess = Process.Start(new ProcessStartInfo
                {
                    FileName = executable,
                    Arguments = $"--port={startPort + n} --folder=\"{path}\" --members=\"{memberListFunc(n)}\" --use-self-signed {arguments}",
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetDirectoryName(executable) ?? string.Empty
                })
                    ?? throw new Exception($"Error creating {n}th process");

                processList.Add(newProcess);
            }

            return Task.WhenAll(processList.Select(async p =>
            {
                try
                {
                    await p.WaitForExitAsync(token);
                }
                catch (TaskCanceledException)
                {
                    if (!p.HasExited)
                    {
                        p.Kill(entireProcessTree: true);
                        await p.WaitForExitAsync();
                    }
                }
            }));
        }

        private static Task RunInstances(int instanceCount, string executable, int startPort, string dataFolder, string? arguments = null, CancellationToken token = default)
        {
            string memberList = $"{string.Join(",", Enumerable.Range(startPort, instanceCount).Select(port => $"localhost:{port}")) }";
            return RunInstances(instanceCount, executable, startPort, dataFolder, _ => memberList, arguments, token);
        }

        private static Process StartNode(string executable, int port, string dataFolder)
        {
            return Process.Start(new ProcessStartInfo
            {
                FileName = executable,
                Arguments = $"--port={port} --folder=\"{dataFolder}\" --members=\"https://localhost:{port}\" --use-self-signed --api",
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(executable) ?? string.Empty
            }) ?? throw new InvalidOperationException("Unable to start SlikNode.");
        }

        private static async Task WaitForNodeAsync(Process process, int port, string dataFolder, CancellationToken token)
        {
            for (int attempt = 0; attempt < 40; attempt++)
            {
                token.ThrowIfCancellationRequested();
                if (process.HasExited)
                    throw new InvalidOperationException(
                        $"SlikNode exited during startup with code {process.ExitCode}.{Environment.NewLine}{ReadNodeLog(dataFolder)}");

                try
                {
                    await UseGrpcService<ISlikCacheService, ValueResponse>(
                        port,
                        service => service.Get(new KeyRequest { Key = "startup-check" }));
                    return;
                }
                catch (RpcException exception) when (exception.StatusCode is StatusCode.Unavailable or StatusCode.Cancelled)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(250), token);
                }
            }

            throw new TimeoutException(
                $"SlikNode on port {port} did not become available.{Environment.NewLine}{ReadNodeLog(dataFolder)}");
        }

        private static async Task WaitForNodesAsync(int startPort, int instanceCount, string dataFolder)
        {
            for (int port = startPort; port < startPort + instanceCount; port++)
            {
                bool available = false;
                for (int attempt = 0; attempt < 40; attempt++)
                {
                    try
                    {
                        await UseGrpcService<ISlikCacheService, ValueResponse>(
                            port,
                            service => service.Get(new KeyRequest { Key = "startup-check" }));
                        available = true;
                        break;
                    }
                    catch (RpcException exception) when (exception.StatusCode is StatusCode.Unavailable or StatusCode.Cancelled)
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(250));
                    }
                }

                if (!available)
                    throw new TimeoutException($"SlikNode on port {port} did not become available.{Environment.NewLine}{ReadClusterLogs(dataFolder, startPort, instanceCount)}");
            }
        }

        private static async Task DeleteTestDataFolderAsync(string dataFolder)
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    if (Directory.Exists(dataFolder))
                        Directory.Delete(dataFolder, recursive: true);
                    return;
                }
                catch (IOException) when (attempt < 4)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(250));
                }
            }
        }

        private static string ReadNodeLog(string dataFolder)
        {
            string logFolder = Path.Combine(dataFolder, "Logs");
            string? logFile = Directory.Exists(logFolder)
                ? Directory.GetFiles(logFolder, "SlikNode-*.log").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
                : null;
            return logFile is null ? "No SlikNode log file was created." : string.Join(Environment.NewLine, File.ReadLines(logFile).TakeLast(120));
        }

        private static string ReadClusterLogs(string dataFolder, int startPort, int instanceCount) =>
            string.Join(
                Environment.NewLine,
                Enumerable.Range(startPort, instanceCount).Select(port =>
                    $"===== Node {port} ====={Environment.NewLine}{ReadNodeLog(Path.Combine(dataFolder, port.ToString()))}"));

        private static async Task StopNodeAsync(Process process)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }

        [TestMethod]
        public async Task Cluster_Consensus_HappyPath()
        {
            int instances = 3;
            int startPort = SlikOptions.DefaultPort;
            string dataFolder = Path.Combine(Path.GetTempPath(), $"SlikClusterConsensus-{Guid.NewGuid():N}");

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            try
            {
                await RunInstances(instances, TestProjectPath, startPort, dataFolder, "--testCache", cts.Token);

                // collect logs and compare history from each node
                List<string[]> history = new();
                for (int n = 0; n < instances; n++)
                {
                    string historyFileName = Path.Combine(dataFolder, $"{startPort + n}", "history.txt");
                    var instanceHistory = await File.ReadAllLinesAsync(historyFileName);
                    history.Add(instanceHistory);
                }

                // TODO align data in columns

                // output in columns
                string line = "";
                for (int n = 0; n < instances; n++)
                    line += $"{startPort + n}\t";

                Console.WriteLine(line);
                Console.WriteLine("------------------------------------------------");

                // non-aligned output
                for (int i = 0; i < history.Max(h => h.Length); i++)
                {
                    line = "";
                    for (int n = 0; n < instances; n++)
                        line += $"{(history[n].Length > i ? history[n][i] : "")}\t";

                    Console.WriteLine(line);
                }
            }
            finally
            {
                await DeleteTestDataFolderAsync(dataFolder);
            }
        }

        private static async ValueTask<T> UseGrpcService<I, T>(int port, Func<I, ValueTask<T>> useFunction) where I : class
        {
            using var channel = GrpcChannel.ForAddress($"https://localhost:{port}", new GrpcChannelOptions
            {
                HttpHandler = _httpHandler
            });

            var service = channel.CreateGrpcService<I>();

            return await useFunction(service);
        }

        private static async Task UseGrpcService<I>(int port, Func<I, Task> useAction) where I : class =>
            await UseGrpcService<I, bool>(port, async service => { await useAction(service); return true; });

        [TestMethod]
        public async Task SetAndRemove_GetReplicated()
        {
            int instances = 3;
            int startPort = SlikOptions.DefaultPort;
            string dataFolder = Path.Combine(Path.GetTempPath(), $"SlikCacheReplication-{Guid.NewGuid():N}");

            using var cts = new CancellationTokenSource();
            bool failed = false;

            var runTask = RunInstances(instances, TestProjectPath, startPort, dataFolder, "--api", cts.Token);

            try
            {
                await WaitForNodesAsync(startPort, instances, dataFolder);
                await Task.Delay(TimeSpan.FromSeconds(2));
                await SetGetRemoveAssertAsync(startPort, instances);
            }
            catch (Exception exception)
            {
                failed = true;
                cts.Cancel();
                await runTask;
                throw new InvalidOperationException(
                    $"{exception}{Environment.NewLine}{ReadClusterLogs(dataFolder, startPort, instances)}",
                    exception);
            }
            finally
            {
                cts.Cancel();
                await runTask;
                if (!failed)
                    await DeleteTestDataFolderAsync(dataFolder);
            }
        }

        private static async Task SetGetRemoveAssertAsync(int startPort, int instances)
        {
            var expectedValue = new byte[] { 1, 2, 3 };

            await UseGrpcService<ISlikCacheService>(startPort, service => service.Set(new SetRequest { Key = "key", Value = expectedValue }));

            await Task.Delay(TimeSpan.FromSeconds(5));

            // checking set
            for (int port = startPort; port < startPort + instances; port++)
            {
                var result = await UseGrpcService<ISlikCacheService, ValueResponse>(port, service => service.Get(new KeyRequest { Key = "key" }));
                Assert.IsTrue(expectedValue.SequenceEqual(result.Value));
            }

            await UseGrpcService<ISlikCacheService>(startPort, service => service.Remove(new KeyRequest { Key = "key" }));

            await Task.Delay(TimeSpan.FromSeconds(5));

            // checking remove
            for (int port = startPort; port < startPort + instances; port++)
            {
                var result = await UseGrpcService<ISlikCacheService, ValueResponse>(port, service => service.Get(new KeyRequest { Key = "key" }));
                Assert.IsTrue(result.Value.Length == 0);
            }
        }

        [TestMethod]
        public async Task AddMemberTest()
        {
            int instances = 3;
            int startPort = SlikOptions.DefaultPort;
            string dataFolder = Path.Combine(Path.GetTempPath(), $"SlikCacheAddMember-{Guid.NewGuid():N}");

            using var cts = new CancellationTokenSource();

            var runTask = RunInstances(instances, TestProjectPath, startPort, dataFolder, n => n switch
            {
                0 => $"https://localhost:{startPort}",
                1 => $"https://localhost:{startPort},https://localhost:{startPort + 1}",
                2 => $"https://localhost:{startPort},https://localhost:{startPort + 2}",
                _ => throw new ArgumentOutOfRangeException(),
            },
            "--api", cts.Token);

            try
            {
                await WaitForNodesAsync(startPort, instances, dataFolder);
                await Task.Delay(TimeSpan.FromSeconds(2));
                await SetGetRemoveAssertAsync(startPort, instances);
            }
            finally
            {
                cts.Cancel();
                await runTask;
                await DeleteTestDataFolderAsync(dataFolder);
            }
        }

        [TestMethod]
        public async Task RemoveMemberTest()
        {
            int instances = 3;
            int startPort = SlikOptions.DefaultPort;
            string dataFolder = Path.Combine(Path.GetTempPath(), $"SlikCacheRemoveMember-{Guid.NewGuid():N}");

            using var cts = new CancellationTokenSource();

            var runTask = RunInstances(instances, TestProjectPath, startPort, dataFolder, n => n switch
            {
                0 => $"https://localhost:{startPort}",
                1 => $"https://localhost:{startPort},https://localhost:{startPort + 1}",
                2 => $"https://localhost:{startPort},https://localhost:{startPort + 2}",
                //3 => $"https://localhost:{startPort},https://localhost:{startPort + 3}",
                _ => throw new ArgumentOutOfRangeException(),
            },
            "--api", cts.Token);

            try
            {
                await WaitForNodesAsync(startPort, instances, dataFolder);
                await Task.Delay(TimeSpan.FromSeconds(2));
                await SetGetRemoveAssertAsync(startPort, instances);
                await UseGrpcService<ISlikMembershipService>(startPort, m => m.Remove(new MemberRequest { Member = $"https://localhost:{startPort + 2}" }));
                await Task.Delay(TimeSpan.FromSeconds(5));

                var expectedValue = new byte[] { 3, 2, 1 };

                await UseGrpcService<ISlikCacheService>(startPort, service => service.Set(new SetRequest { Key = "key", Value = expectedValue }));

                await Task.Delay(TimeSpan.FromSeconds(5));

                // checking that value is not replicated to the removed node
                var result = await UseGrpcService<ISlikCacheService, ValueResponse>(startPort + 2, service => service.Get(new KeyRequest { Key = "key" }));
                Assert.IsFalse(expectedValue.SequenceEqual(result.Value));

            }
            finally
            {
                cts.Cancel();
                await runTask;
                await DeleteTestDataFolderAsync(dataFolder);
            }
        }

        [TestMethod]
        public async Task Restart_RestoresCacheValueFromNewFormatStorage()
        {
            int port = SlikOptions.DefaultPort;
            string dataFolder = Path.Combine(Path.GetTempPath(), $"SlikCacheRestart-{Guid.NewGuid():N}");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            Process? process = null;
            byte[] expectedValue = new byte[] { 49 };

            try
            {
                process = StartNode(TestProjectPath, port, dataFolder);
                await WaitForNodeAsync(process, port, dataFolder, cts.Token);

                for (byte value = 0; value < 50; value++)
                {
                    await UseGrpcService<ISlikCacheService>(port, service =>
                        service.Set(new SetRequest { Key = "snapshot-key", Value = new[] { value } }));
                }

                await UseGrpcService<ISlikCacheService>(port, service =>
                    service.Set(new SetRequest { Key = "snapshot-key", Value = expectedValue }));
                await StopNodeAsync(process);
                process.Dispose();
                process = StartNode(TestProjectPath, port, dataFolder);
                await WaitForNodeAsync(process, port, dataFolder, cts.Token);

                var restored = await UseGrpcService<ISlikCacheService, ValueResponse>(
                    port,
                    service => service.Get(new KeyRequest { Key = "snapshot-key" }));
                Assert.IsTrue(expectedValue.SequenceEqual(restored.Value));
            }
            finally
            {
                if (process is not null)
                {
                    await StopNodeAsync(process);
                    process.Dispose();
                }

                if (Directory.Exists(dataFolder))
                    Directory.Delete(dataFolder, recursive: true);
            }
        }

        //[TestMethod]
        //public async Task Cluster_NewNode_GetsValues()
        //{
        //    throw new NotImplementedException();
        //}

        //[TestMethod]
        //public async Task Cluster_ChaosOfUpdates_GetsTheLastValue()
        //{
        //    throw new NotImplementedException();
        //}
    }
}
