// Lumara Companion for Jellyfin
// Copyright (C) 2026 Lumara
// Licensed under the GNU General Public License v3.0 (see LICENSE).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.LumaraCompanion.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.LumaraCompanion.Integrations.Arr;

/// <summary>The result of copying tags.</summary>
/// <param name="Ok">The run finished.</param>
/// <param name="Matched">Library titles matched to an *arr entry.</param>
/// <param name="Changed">Titles whose tags changed.</param>
/// <param name="Message">Plain-English summary.</param>
public sealed record TagSyncResult(bool Ok, int Matched, int Changed, string Message);

/// <summary>
/// Copies Sonarr, Radarr and Lidarr tags onto the matching Jellyfin titles, so
/// people can filter the library by them ("kids", "4k-only", whatever the admin
/// uses in the *arrs).
/// </summary>
/// <remarks>
/// Copied tags carry a prefix (default "arr: ") so the plugin can tell them from
/// tags added by hand: it only ever adds or removes tags with that prefix. A
/// metadata refresh that rewrites a title's tags just means the next run puts
/// them back.
/// </remarks>
public sealed class TagSync
{
    private readonly ServiceClient _client;
    private readonly ILibraryManager _library;
    private readonly Func<RequestSettings> _settings;

    /// <summary>
    /// Initializes a new instance of the <see cref="TagSync"/> class.
    /// </summary>
    /// <param name="client">Service client.</param>
    /// <param name="library">Jellyfin's library.</param>
    /// <param name="settings">The live request settings (prefix).</param>
    public TagSync(ServiceClient client, ILibraryManager library, Func<RequestSettings> settings)
    {
        _client = client;
        _library = library;
        _settings = settings;
    }

    /// <summary>
    /// The tags a title should end up with: hand-added tags unchanged, prefixed
    /// tags replaced by the current *arr tags. Pure, for unit tests.
    /// </summary>
    /// <param name="current">The title's tags now.</param>
    /// <param name="arrTags">Tag labels from the *arrs.</param>
    /// <param name="prefix">Prefix for copied tags.</param>
    /// <returns>The new tag list.</returns>
    public static string[] Desired(IEnumerable<string> current, IEnumerable<string> arrTags, string prefix)
    {
        var kept = current.Where(t => !t.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        var copied = arrTags.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => prefix + t.Trim());
        return kept.Concat(copied).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>
    /// Runs the copy now.
    /// </summary>
    /// <param name="progress">Progress (0-100).</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>What happened.</returns>
    public async Task<TagSyncResult> RunAsync(IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var prefix = _settings().TagPrefix;
        if (string.IsNullOrWhiteSpace(prefix))
        {
            return new TagSyncResult(false, 0, 0, "Set a tag prefix first, so copied tags can be told apart from your own.");
        }

        // (Jellyfin kind, provider, id) -> tag labels, from each configured app.
        var wanted = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var sources = 0;
        foreach (var (kind, tagPath, listPath, provider, field, itemKind) in new[]
        {
            (IntegrationKind.Radarr, "/api/v3/tag", "/api/v3/movie", MetadataProvider.Tmdb, "tmdbId", BaseItemKind.Movie),
            (IntegrationKind.Sonarr, "/api/v3/tag", "/api/v3/series", MetadataProvider.Tvdb, "tvdbId", BaseItemKind.Series),
            (IntegrationKind.Lidarr, "/api/v1/tag", "/api/v1/artist", MetadataProvider.MusicBrainzArtist, "foreignArtistId", BaseItemKind.MusicArtist),
        })
        {
            if (!_client.Integrations.IsConfigured(kind))
            {
                continue;
            }

            using var tags = (await _client.SendAsync(kind, HttpMethod.Get, tagPath, null, cancellationToken).ConfigureAwait(false)).Json();
            using var list = (await _client.SendAsync(kind, HttpMethod.Get, listPath, null, cancellationToken).ConfigureAwait(false)).Json();
            if (tags is null || list is null)
            {
                return new TagSyncResult(false, 0, 0, $"Couldn't read {kind}. Nothing was changed.");
            }

            sources++;
            var labels = tags.RootElement.Arr().Where(t => t.Long("id") is not null).ToDictionary(t => t.Long("id")!.Value, t => t.Str("label") ?? string.Empty);
            foreach (var entry in list.RootElement.Arr())
            {
                var id = entry.Str(field);
                if (string.IsNullOrEmpty(id) || id == "0")
                {
                    continue;
                }

                var names = entry.Arr("tags").Where(t => t.ValueKind == JsonValueKind.Number).Select(t => labels.GetValueOrDefault(t.GetInt64())).OfType<string>().ToList();
                wanted[itemKind + ":" + provider + ":" + id] = names;
            }
        }

        if (sources == 0)
        {
            return new TagSyncResult(false, 0, 0, "Set up Sonarr, Radarr or Lidarr first.");
        }

        var items = _library.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.MusicArtist],
            Recursive = true,
            IsVirtualItem = false,
        });
        int matched = 0, changed = 0, done = 0;
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            done++;
            progress?.Report(100.0 * done / Math.Max(1, items.Count));
            var provider = item.GetBaseItemKind() switch
            {
                BaseItemKind.Movie => MetadataProvider.Tmdb,
                BaseItemKind.Series => MetadataProvider.Tvdb,
                _ => MetadataProvider.MusicBrainzArtist,
            };
            var id = item.GetProviderId(provider);
            List<string>? arrTags = null;
            if (!string.IsNullOrEmpty(id) && wanted.TryGetValue(item.GetBaseItemKind() + ":" + provider + ":" + id, out arrTags))
            {
                matched++;
            }

            var desired = Desired(item.Tags ?? [], arrTags ?? [], prefix);
            if (desired.SequenceEqual(item.Tags ?? [], StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            item.Tags = desired;
            await _library.UpdateItemAsync(item, item.GetParent(), ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
            changed++;
        }

        return new TagSyncResult(true, matched, changed, changed == 1 ? $"Updated tags on 1 title ({matched} matched)." : $"Updated tags on {changed} titles ({matched} matched).");
    }
}

/// <summary>
/// The daily "Copy *arr tags" task in Dashboard → Scheduled tasks. It does
/// nothing unless the admin switched tag copying on in the plugin settings.
/// </summary>
public sealed class TagSyncTask : IScheduledTask
{
    private readonly TagSync _sync;

    /// <summary>
    /// Initializes a new instance of the <see cref="TagSyncTask"/> class.
    /// </summary>
    /// <param name="sync">The tag copier.</param>
    public TagSyncTask(TagSync sync)
    {
        _sync = sync;
    }

    /// <inheritdoc />
    public string Name => "Copy Sonarr, Radarr and Lidarr tags";

    /// <inheritdoc />
    public string Key => "LumaraCopyArrTags";

    /// <inheritdoc />
    public string Description => "Adds the *arr apps' tags to matching titles, with the prefix set in Lumara Companion.";

    /// <inheritdoc />
    public string Category => "Lumara Companion";

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (Plugin.Instance?.Configuration.Requests.CopyArrTags != true)
        {
            progress?.Report(100);
            return;
        }

        await _sync.RunAsync(progress, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        // Daily at 04:00 server time.
        return [new TaskTriggerInfo { Type = TaskTriggerInfoType.DailyTrigger, TimeOfDayTicks = TimeSpan.FromHours(4).Ticks }];
    }
}
