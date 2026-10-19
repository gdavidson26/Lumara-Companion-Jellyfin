# Changelog: Lumara Companion for Jellyfin

All notable changes to Lumara Companion for Jellyfin, newest first. Versions follow
[Semantic Versioning](https://semver.org). Each release on
[GitHub Releases](https://github.com/gdavidson26/Lumara-Companion-Jellyfin/releases) is tagged
`v<version>` and has one zip per Jellyfin version (`.12` for Jellyfin 12, `.11` for 10.11).

## 1.0.0 (2026-10-19)

### Features

- **Web app modes:** pick Off, Side by side or Replace in the dashboard. In Replace mode Lumara opens at your server's address, anyone can switch back to the standard Jellyfin app, and the standard app always stays reachable at /web/?stock=1.
- **Integration settings:** enter addresses and API keys for Seerr, Radarr, Sonarr, Lidarr, Bazarr, Prowlarr, Shoko, Jellystat, TMDB, MDBList, qBittorrent and SABnzbd, and test each connection. They're encrypted on your server and never sent back out, not even to the settings page.
- **Settings that follow you:** each person's Lumara settings are kept on your server and carried between their devices. Admins can set defaults for everyone.
- **Bookmarks, hidden titles and Spoiler Guard:** each person's lists are kept on your server, private to them, so they're the same on every device.
- **Reviews:** people on your server can rate and review titles for each other. Each person changes only their own review; admins can remove any.
- **Maintenance notice:** set a message, a start and end time, how early the countdown appears, and whether only admins can use the server meanwhile. Admins are never locked out, and playback progress is still saved for everyone else.
- **Branding:** set the server name, a sign-in message, an accent colour from Lumara's palette and a logo (PNG, JPEG or WebP up to 512 KB) for Lumara's sign-in screen.
- **Ratings and availability:** with MDBList or TMDB keys saved, Lumara apps get scores and where-to-watch information through your server. The keys stay on the server and results are cached for 12 hours.
- **Seerr requests:** people request films and shows (whole shows or single seasons, in 4K where allowed) from Lumara with their own Seerr account, without signing in to Seerr. Seerr's permissions, request limits and auto-approval apply as usual.
- **Discovery:** trending, popular and upcoming titles, genres and networks, recommendations and similar titles come from Seerr. Searching with a year ("Dune 1984") finds that year's title.
- **Problem reports:** anyone can report a problem with a film, season or episode, comment on it, and admins can mark it fixed. Reports go to Seerr's issue list.
- **Watchlist:** a watchlist for library titles and titles you don't have yet. With Seerr set up it stays in step with each person's Seerr watchlist in both directions.
- **Auto-requests:** optionally request the next season when someone finishes a show's newest season, or the next film in a collection. Off by default; each person can turn it off for themselves.
- **Calendar:** upcoming episodes, films and albums from Sonarr, Radarr, Lidarr and Shoko in one list, for admins or everyone.
- **Downloads:** what's downloading, from the Sonarr, Radarr and Lidarr queues and from qBittorrent and SABnzbd. Admins see everything; others see their own requests.
- **Tools for admins:** what Radarr, Sonarr and Bazarr know about a title (quality profile, files, tags, missing subtitles), a search or subtitle search on demand, links to the apps, the apps' health with Prowlarr's failing indexers, and Jellystat's most watched lists.
- **Tags from the *arr apps:** a nightly task copies Sonarr, Radarr and Lidarr tags onto matching titles, with a prefix so your own tags are never touched.
- **Seerr accounts:** see every Jellyfin user next to their Seerr account and permissions, with warnings worth a look, and add people to Seerr in one step.
- **Parental limits:** accounts with a parental limit only see discovery titles, title pages and calendar entries within that limit.

### Improvements

- **TMDB keys:** both kinds of TMDB key work now, the API read token and the shorter v3 API key, including in the connection test.
- **Images through your server:** posters for titles that aren't in the library load through your server, so TVs and phones never contact TMDB or your *arr apps directly.
