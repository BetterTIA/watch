using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Siemens.Collaboration.Net;
using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;

namespace BtiaWatch
{
    public static class Program
    {
        public const int TiaMajorVersion = 21;

        public static async Task Main(string[] args)
        {
            Api.Global.Openness().Initialize(tiaMajorVersion: TiaMajorVersion);
            AppDomain.CurrentDomain.AssemblyResolve += ResolveSiemens;

            if (!Api.Global.Openness().IsUserInGroup())
            {
                Console.Error.WriteLine("The current user is not in the Windows group 'Siemens TIA Openness'.");
                Environment.ExitCode = 1;
                return;
            }

            // Window rectangles in physical pixels, so the capture is not cropped on scaled displays.
            WindowCapture.SetProcessDPIAware();
            WatchTools.Portal = new OpennessPortal();

            var builder = Host.CreateEmptyApplicationBuilder(settings: null);
            builder.Services
                .AddMcpServer(options =>
                {
                    options.ServerInfo = new global::ModelContextProtocol.Protocol.Implementation
                    {
                        Name = "BTIA_Watch",
                        Title = "BetterTIA Watch",
                        Version = typeof(Program).Assembly.GetName().Version.ToString()
                    };
                    options.ServerInstructions =
                        "Read-only view of live PLC data through a running TIA Portal V21. 'watch_online' reports or " +
                        "opens the online connection, 'watch_db' returns snapshot values of data blocks, 'watch_screen' " +
                        "returns a capture of a monitored editor. Nothing here writes values to the PLC.";
                })
                .WithStdioServerTransport()
                .WithToolsFromAssembly();

            await builder.Build().RunAsync();
        }

        // The Siemens resolver misses some Siemens.Engineering.* satellite assemblies; look them up in the install folder.
        private static Assembly? ResolveSiemens(object sender, ResolveEventArgs args)
        {
            var name = new AssemblyName(args.Name).Name;
            var root = Environment.GetEnvironmentVariable("TiaPortalLocation");
            if (!name.StartsWith("Siemens.Engineering", StringComparison.Ordinal) || string.IsNullOrEmpty(root))
            {
                return null;
            }

            var path = Path.Combine(root, "PublicAPI", $"V{TiaMajorVersion}", "net48", name + ".dll");
            return File.Exists(path) ? Assembly.LoadFrom(path) : null;
        }
    }
}
