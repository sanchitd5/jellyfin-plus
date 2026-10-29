using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Plus
{
    // The card image pre-render lives in the patcher assembly (it records through a Harmony prefix and
    // needs Jellyfin's database and image types). These are the hooks Jellyfin discovers in a plugin
    // assembly; they call across the load-context boundary through PatcherLoader and share no types.

    /// <summary>Registers the card image pre-render's start-up service.</summary>
    public class CardPrerenderServiceRegistrator : IPluginServiceRegistrator
    {
        /// <inheritdoc />
        public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
        {
            serviceCollection.AddHostedService<CardPrerenderHostedService>();
        }
    }

    /// <summary>Resolves the services, creates JellyfinPlus_ImageSizes and starts flushing recorded shapes.</summary>
    public sealed class CardPrerenderHostedService : IHostedService
    {
        private readonly IServiceProvider _services;
        private readonly ILogger<CardPrerenderHostedService> _logger;

        public CardPrerenderHostedService(IServiceProvider services, ILogger<CardPrerenderHostedService> logger)
        {
            _services = services;
            _logger = logger;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            // Off the start-up path: it takes a database connection and must never delay the server coming up.
            _ = Task.Run(() => PatcherLoader.CardPrerenderStart(_services, _logger), CancellationToken.None);
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            PatcherLoader.CardPrerenderStop();
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// After every library scan: start filling the card images the scan made missing, in the background.
    /// Returns at once, so a first run (minutes to an hour) never holds the scan open.
    /// </summary>
    public sealed class CardPrerenderPostScanTask : ILibraryPostScanTask
    {
        /// <inheritdoc />
        public Task Run(IProgress<double> progress, CancellationToken cancellationToken)
        {
            PatcherLoader.CardPrerenderStartBackground();
            return Task.CompletedTask;
        }
    }

    /// <summary>Dashboard > Scheduled Tasks > Run: pre-renders card images now and shows progress.</summary>
    public sealed class CardPrerenderTask : IScheduledTask
    {
        /// <inheritdoc />
        public string Name => "Jellyfin Plus: pre-render card images";

        /// <inheritdoc />
        public string Key => "JellyfinPlusCardPrerender";

        /// <inheritdoc />
        public string Description => "Resizes the poster, thumb and backdrop images that library grids ask for ahead of time, using the sizes your browsers have actually requested, so the first visit to a view finds them cached. Runs automatically in the background after each library scan; images already cached are skipped. Two low-priority threads that wait while anyone is browsing.";

        /// <inheritdoc />
        public string Category => "Library";

        /// <inheritdoc />
        public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
            => PatcherLoader.CardPrerenderRun(progress, cancellationToken);

        /// <inheritdoc />
        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Array.Empty<TaskTriggerInfo>();
    }
}
