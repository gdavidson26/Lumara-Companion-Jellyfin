// Lumara Companion for Jellyfin
// Copyright (C) 2026 Lumara
// Licensed under the GNU General Public License v3.0 (see LICENSE).

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.LumaraCompanion.Integrations;

/// <summary>One score from one source, ready to show.</summary>
/// <param name="Source">Source id, e.g. "imdb" or "tomatoes".</param>
/// <param name="Label">Display name, e.g. "Rotten Tomatoes".</param>
/// <param name="Value">Display value, e.g. "7.4", "92%" or "81".</param>
/// <param name="Votes">Number of votes, when known.</param>
public sealed record RatingEntry(string Source, string Label, string Value, long? Votes);

/// <summary>Where a title can be watched in one country, by service name.</summary>
/// <param name="Region">Two-letter country code.</param>
/// <param name="Stream">Subscription services.</param>
/// <param name="Rent">Rental services.</param>
/// <param name="Buy">Purchase services.</param>
public sealed record WatchProviders(string Region, IReadOnlyList<string> Stream, IReadOnlyList<string> Rent, IReadOnlyList<string> Buy);

/// <summary>Ratings and availability for a title.</summary>
/// <param name="Configured">MDBList or TMDB is set up on this server.</param>
/// <param name="Ratings">Scores, in a fixed order.</param>
/// <param name="Providers">Where to watch, when TMDB is set up and knows.</param>
public sealed record RatingsResult(bool Configured, IReadOnlyList<RatingEntry> Ratings, WatchProviders? Providers);

/// <summary>
/// Fetches scores from MDBList and TMDB, and streaming availability from TMDB,
/// on the server's behalf. Keys never leave the server, and requests go from
/// the server, so viewers' devices only ever talk to their own server.
/// </summary>
/// <remarks>
/// MDBList: <c>GET https://api.mdblist.com/tmdb/{movie|show}/{id}?apikey=…</c>
/// returns <c>ratings: [{source, value, score, votes}]</c>; values can be
/// numbers, strings or null, so parsing is lenient. TMDB:
/// <c>/3/{movie|tv}/{id}</c> (vote_average) and
/// <c>/3/{movie|tv}/{id}/watch/providers</c> (results by country). Results are
/// cached in memory for 12 hours. URLs are never logged (MDBList wants its key
/// in the query string), which is why a plain HttpClient is used.
/// </remarks>
public sealed class RatingsService
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromHours(12);
    private const int MaxCached = 2000;

    // Display order and names. Anything not listed is ignored.
    private static readonly (string Id, string Label)[] Sources =
    [
        ("imdb", "IMDb"),
        ("tomatoes", "Rotten Tomatoes"),
        ("popcorn", "RT Audience"),
        ("metacritic", "Metacritic"),
        ("letterboxd", "Letterboxd"),
        ("tmdb", "TMDB"),
        ("trakt", "Trakt"),
        ("myanimelist", "MyAnimeList"),
    ];

    private readonly IntegrationService _integrations;
    private readonly HttpClient? _http;
    private readonly Func<DateTime> _clock;
    private readonly string _mdbBase;
    private readonly string _tmdbBase;
    private readonly ConcurrentDictionary<string, (DateTime At, RatingsResult Result)> _cache = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="RatingsService"/> class.
    /// </summary>
    /// <param name="integrations">Holds the MDBList and TMDB keys.</param>
    /// <param name="http">HTTP client (null in tests that don't call out).</param>
    /// <param name="clock">Current time (UTC), injectable for tests.</param>
    /// <param name="mdbBase">MDBList API root (tests point it elsewhere).</param>
    /// <param name="tmdbBase">TMDB API root (tests point it elsewhere).</param>
    public RatingsService(
        IntegrationService integrations,
        HttpClient? http,
        Func<DateTime>? clock = null,
        string mdbBase = "https://api.mdblist.com",
        string tmdbBase = "https://api.themoviedb.org/3")
    {
        _integrations = integrations;
        _http = http;
        _clock = clock ?? (() => DateTime.UtcNow);
        _mdbBase = mdbBase.TrimEnd('/');
        _tmdbBase = tmdbBase.TrimEnd('/');
    }

    /// <summary>
    /// Ratings and availability for a film or show.
    /// </summary>
    /// <param name="tmdbId">TMDB id (required by both services).</param>
    /// <param name="isSeries">A show rather than a film.</param>
    /// <param name="region">Country for availability, e.g. "US".</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>What could be found; empty lists when nothing is set up or known.</returns>
    public async Task<RatingsResult> GetAsync(string? tmdbId, bool isSeries, string region, CancellationToken cancellationToken)
    {
        var mdbKey = _integrations.Reveal(IntegrationKind.MdbList).ApiKey;
        var tmdbKey = _integrations.Reveal(IntegrationKind.Tmdb).ApiKey;
        var configured = !string.IsNullOrEmpty(mdbKey) || !string.IsNullOrEmpty(tmdbKey);
        if (!configured || string.IsNullOrEmpty(tmdbId) || !tmdbId.All(char.IsAsciiDigit) || _http is null)
        {
            return new RatingsResult(configured, [], null);
        }

        region = string.IsNullOrEmpty(region) ? "US" : region.ToUpperInvariant();
        var cacheKey = $"{(isSeries ? "tv" : "movie")}:{tmdbId}:{region}";
        if (_cache.TryGetValue(cacheKey, out var hit) && _clock() - hit.At < CacheFor)
        {
            return hit.Result;
        }

        var ratings = new List<RatingEntry>();
        if (!string.IsNullOrEmpty(mdbKey))
        {
            var json = await GetJson($"{_mdbBase}/tmdb/{(isSeries ? "show" : "movie")}/{tmdbId}?apikey={Uri.EscapeDataString(mdbKey)}", null, cancellationToken).ConfigureAwait(false);
            if (json is not null)
            {
                ratings.AddRange(ParseMdbList(json));
            }
        }

        WatchProviders? providers = null;
        if (!string.IsNullOrEmpty(tmdbKey))
        {
            var kind = isSeries ? "tv" : "movie";
            if (!ratings.Any(r => r.Source == "tmdb"))
            {
                var details = await GetJson(TmdbUrl($"/{kind}/{tmdbId}", tmdbKey), tmdbKey, cancellationToken).ConfigureAwait(false);
                if (details is not null && ParseTmdbScore(details) is { } score)
                {
                    ratings.Add(score);
                }
            }

            var where = await GetJson(TmdbUrl($"/{kind}/{tmdbId}/watch/providers", tmdbKey), tmdbKey, cancellationToken).ConfigureAwait(false);
            if (where is not null)
            {
                providers = ParseProviders(where, region);
            }
        }

        var result = new RatingsResult(true, Order(ratings), providers);
        if (_cache.Count >= MaxCached)
        {
            _cache.Clear();
        }

        _cache[cacheKey] = (_clock(), result);
        return result;
    }

    /// <summary>
    /// Reads MDBList's ratings array.
    /// </summary>
    /// <param name="json">Response body.</param>
    /// <returns>Known sources with a value.</returns>
    public static IReadOnlyList<RatingEntry> ParseMdbList(string json)
    {
        var list = new List<RatingEntry>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("ratings", out var ratings) || ratings.ValueKind != JsonValueKind.Array)
            {
                return list;
            }

            foreach (var r in ratings.EnumerateArray())
            {
                var source = Str(r, "source")?.ToLowerInvariant();
                var label = Sources.FirstOrDefault(s => s.Id == source).Label;
                if (source is null || label is null)
                {
                    continue;
                }

                var value = Num(r, "value");
                var score = Num(r, "score");
                var shown = Format(source, value, score);
                if (shown is not null)
                {
                    list.Add(new RatingEntry(source, label, shown, (long?)Num(r, "votes")));
                }
            }
        }
        catch (JsonException)
        {
            // A broken reply just means no ratings this time.
        }

        return list;
    }

    /// <summary>
    /// Reads TMDB's own score from a details response.
    /// </summary>
    /// <param name="json">Response body.</param>
    /// <returns>The score, or <c>null</c> when there are no votes.</returns>
    public static RatingEntry? ParseTmdbScore(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var avg = Num(doc.RootElement, "vote_average");
            var count = Num(doc.RootElement, "vote_count");
            if (avg is null || avg <= 0 || count is null or <= 0)
            {
                return null;
            }

            return new RatingEntry("tmdb", "TMDB", avg.Value.ToString("0.0", CultureInfo.InvariantCulture), (long)count.Value);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads TMDB's watch providers for one country.
    /// </summary>
    /// <param name="json">Response body.</param>
    /// <param name="region">Country code.</param>
    /// <returns>Services by kind, or <c>null</c> when TMDB lists none there.</returns>
    public static WatchProviders? ParseProviders(string json, string region)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("results", out var results) || !results.TryGetProperty(region, out var here))
            {
                return null;
            }

            static List<string> Names(JsonElement e, string kind) =>
                e.TryGetProperty(kind, out var arr) && arr.ValueKind == JsonValueKind.Array
                    ? arr.EnumerateArray().Select(p => Str(p, "provider_name")).OfType<string>().Distinct().Take(12).ToList()
                    : [];

            var w = new WatchProviders(region, Names(here, "flatrate"), Names(here, "rent"), Names(here, "buy"));
            return w.Stream.Count + w.Rent.Count + w.Buy.Count == 0 ? null : w;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Formats a source's score the way that source presents it.
    /// </summary>
    /// <param name="source">Source id.</param>
    /// <param name="value">The source's native value (e.g. 7.4 or 3.9).</param>
    /// <param name="score">MDBList's 0-100 score.</param>
    /// <returns>Display text, or <c>null</c> when there's nothing to show.</returns>
    public static string? Format(string source, double? value, double? score)
    {
        var inv = CultureInfo.InvariantCulture;
        return source switch
        {
            // Out of 10 (IMDb, TMDB, MAL) or 5 (Letterboxd), one decimal.
            "imdb" or "tmdb" or "myanimelist" or "letterboxd" when value is > 0 => value.Value.ToString("0.0", inv),
            // Percentages.
            "tomatoes" or "popcorn" or "trakt" when score is > 0 => score.Value.ToString("0", inv) + "%",
            "metacritic" when score is > 0 => score.Value.ToString("0", inv),
            _ => null,
        };
    }

    private static List<RatingEntry> Order(List<RatingEntry> list) =>
        Sources.Select(s => list.FirstOrDefault(r => r.Source == s.Id)).OfType<RatingEntry>().ToList();

    // TMDB accepts a v4 read token as a Bearer header, or a v3 key as ?api_key.
    private string TmdbUrl(string path, string key) =>
        IsBearer(key) ? _tmdbBase + path : $"{_tmdbBase}{path}?api_key={Uri.EscapeDataString(key)}";

    private static bool IsBearer(string key) => key.StartsWith("eyJ", StringComparison.Ordinal);

    private async Task<string?> GetJson(string url, string? tmdbKey, CancellationToken cancellationToken)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(8));
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (tmdbKey is not null && IsBearer(tmdbKey))
            {
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + tmdbKey);
            }

            using var response = await _http!.SendAsync(request, cts.Token).ConfigureAwait(false);
            return response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false) : null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (TaskCanceledException)
        {
            return null;
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? Num(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v))
        {
            return null;
        }

        return v.ValueKind switch
        {
            JsonValueKind.Number => v.GetDouble(),
            JsonValueKind.String when double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
            _ => null,
        };
    }
}
