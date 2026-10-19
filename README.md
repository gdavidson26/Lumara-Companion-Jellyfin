<p align="center"><img src="assets/thumb.png" alt="Lumara Companion for Jellyfin" width="640"></p>

# Lumara Companion for Jellyfin

A server plugin for **Jellyfin 10.11 and 12** that hosts **Lumara Web**, a fast, good-looking
web app for your Jellyfin library, and gives Lumara apps the features that need your server's
help: requests through Seerr, a calendar and download list from your *arr apps, a watchlist,
bookmarks, Spoiler Guard, household reviews and more.

Every address and API key for your other services stays encrypted on your server. Lumara apps
on TVs, phones and browsers only ever talk to your Jellyfin server.

[Install](#install) · [What it does](#what-it-does) · [Integrations](#integrations) ·
[Privacy and security](#privacy-and-security) · [Troubleshooting](#troubleshooting) ·
[Building](#building-from-source) · [License](#license)

## Install

You need Jellyfin **10.11.x** or **12.x** and an admin account.

1. In Jellyfin, open **Dashboard → Plugins → Manage Repositories**, select **New Repository**
   and add:

   | Name | Repository URL |
   |---|---|
   | Lumara | `https://raw.githubusercontent.com/gdavidson26/Lumara-Companion-Jellyfin/main/manifest.json` |

2. Back on **Plugins**, open the **Available** tab, select **Lumara Companion** (under General)
   and install the newest version.
   Jellyfin picks the build for your server version by itself.
3. **Restart Jellyfin.**
4. Open **Dashboard → Plugins → Lumara Companion → Settings** and choose how Lumara Web should appear (see
   below). Nothing changes for your users until you do.

Updates show up on the same Plugins page, and Jellyfin can install them automatically.

**Manual install:** download the zip for your server from
[Releases](https://github.com/gdavidson26/Lumara-Companion-Jellyfin/releases)
(`lumara-companion_<version>.12.zip` for Jellyfin 12, `lumara-companion_<version>.11.zip` for Jellyfin 10.11), stop Jellyfin, unzip it into a new
folder inside Jellyfin's `plugins` folder, and start Jellyfin again. A build only loads on the
server version it was made for.

## What it does

### Lumara Web, served by your server

Choose what people get at your server's address:

| Mode | What happens |
|---|---|
| Off (default) | Nothing changes. Only the standard Jellyfin web app. |
| Side by side | The standard app stays as it is, with a Lumara button. Lumara Web lives at `/Lumara/Web/`. |
| Replace | Lumara Web opens at your server's address. Anyone can switch back to the standard app, and their browser remembers. |

The standard app is **always** at `/web/?stock=1`, whatever you pick. If the Lumara Web files
are missing, the standard app opens instead, so a broken install never locks anyone out.

### For everyone

- **Requests:** discover trending, popular and upcoming titles, search for anything (add a year,
  "Dune 1984", to narrow it), and request films or single seasons, in 4K where allowed, with
  your own Seerr account, without signing in to Seerr.
- **Watchlist:** for titles you have and titles you don't yet, kept in step with your Seerr
  watchlist in both directions.
- **Calendar:** upcoming episodes, film releases and albums as a list, a week or a month.
- **Downloads:** the progress of what you asked for.
- **Problem reports:** report a picture, sound or subtitle problem with a film, season or
  episode, and follow the replies.
- **Bookmarks, hidden titles and Spoiler Guard:** private to each person and the same on every
  device.
- **Reviews:** rate and review titles for the rest of the household.
- **Ratings and where to watch** from MDBList and TMDB.
- **Settings that follow you:** each person's Lumara settings are kept on the server.

### For admins

- A settings page for every integration, each with a connection test.
- The *arr apps' view of a title (quality profile, files, tags), a release search or a Bazarr
  subtitle search on demand, and links to the apps.
- App health, Prowlarr's failing indexers, and Jellystat's most watched lists.
- Seerr accounts next to every Jellyfin user, with warnings worth a look and one-step import.
- Optional auto-requests of the next season or the next film in a collection (each person can
  turn it off for themselves).
- A nightly task that copies Sonarr, Radarr and Lidarr tags onto matching titles, with a prefix
  so your own tags are never touched.
- A maintenance notice with a countdown and an optional admins-only lockout, and server
  branding (name, sign-in message, accent colour, logo) for Lumara's sign-in screen.
- Server-wide default Lumara settings.

Parental limits apply throughout: accounts with a parental limit only see discovery titles,
title pages and calendar entries within that limit.

## Integrations

Enter each one's address and API key in the plugin's settings page and press **Test
connection**. All are optional.

| Service | What it adds |
|---|---|
| Seerr (Overseerr's successor) | Requests, discovery, watchlist sync, problem reports |
| Radarr, Sonarr | Calendar, downloads, title tools, tags |
| Lidarr | Album calendar, downloads, tags |
| Bazarr | Missing subtitles and subtitle searches (admins) |
| Prowlarr | Indexer health (admins) |
| Shoko | Anime episodes on the calendar |
| Jellystat | Most watched titles and most active people (admins) |
| qBittorrent, SABnzbd | Downloads the *arr apps don't track, and download speed (admins) |
| TMDB, MDBList | Ratings and where to watch |

**Seerr:** use the API key from Seerr → Settings → General. Lumara uses each person's own Seerr
account (the one Seerr linked to their Jellyfin account), so Seerr's permissions, request limits
and auto-approval apply exactly as on Seerr's own site. People without a Seerr account can be
added from the plugin's settings page.

## Privacy and security

- **Keys stay on the server.** Addresses, API keys and qBittorrent's login are encrypted with
  ASP.NET Core Data Protection, using keys that never leave this server install. No endpoint
  returns them, the settings page included; it only shows "Configured" or "Not set up".
- **Devices only talk to your server.** Calls to Seerr, the *arr apps, TMDB and MDBList go out
  from the server, and posters for titles you don't have are passed through it (on signed
  addresses), so phones and TVs never contact those services.
- **No analytics, no telemetry, no phone-home.** The plugin sends nothing anywhere except to the
  services you set up.
- **Admin only where it matters.** Integration settings, *arr tools and statistics need a
  Jellyfin admin. Everything else works for the signed-in person only, on their own data.

Found a security problem? Please report it privately through
[GitHub's private vulnerability reporting](https://github.com/gdavidson26/Lumara-Companion-Jellyfin/security/advisories/new)
rather than an issue.

## Troubleshooting

- **The plugin isn't under Available:** check the repository URL, then reload the Plugins
  page. Jellyfin only lists the build that matches its version (10.11 or 12).
- **"Restart" next to the plugin:** Jellyfin needs a restart after installing or updating.
- **Lumara Web doesn't open:** the web mode is Off by default. The standard app is always at
  `/web/?stock=1`.
- **Requests say "no Seerr account":** add the person under **Seerr accounts** in the plugin's
  settings page, or sign them in to Seerr once with their Jellyfin account.
- **A connection test fails:** the address must be reachable from the Jellyfin server itself
  (for example `http://radarr:7878` inside Docker), not from your browser.

Bugs and ideas: [open an issue](https://github.com/gdavidson26/Lumara-Companion-Jellyfin/issues).

## Building from source

Requires the .NET 10 SDK, which also builds the Jellyfin 10.11 target (`net9.0`).

```bash
dotnet build LumaraCompanion.slnx -c Release
```

```bash
dotnet test LumaraCompanion.slnx -c Release
```

The `net10.0` build is for Jellyfin 12, the `net9.0` build for Jellyfin 10.11. The release zips
also carry the Lumara Web files in a `lumara-web` folder next to the DLL; the plugin works
without them (only the web modes need them).

## Endpoints

All under `/Lumara`. "Admin" means a Jellyfin administrator; "signed in" uses the person's own
Jellyfin session.

| Endpoint | Who | Purpose |
|---|---|---|
| `GET Info` | anyone | Plugin version, web mode, whether Lumara Web is installed |
| `GET Web/{path}` | anyone | Lumara Web's files (404 when the mode is Off) |
| `GET/PUT/DELETE Settings` | signed in | Your own Lumara settings, plus the admin defaults |
| `PUT Defaults` | admin | Defaults for everyone |
| `PUT Web/Settings` | admin | Web mode and the Lumara button |
| `GET Integrations` | admin | Which integrations are set up (never the values) |
| `PUT/DELETE Integrations/{kind}` | admin | Save or remove an address and key |
| `POST Integrations/{kind}/Test` | admin | Test a connection from the server |
| `GET/PUT UserData/{bookmarks,hidden,spoiler}` | signed in | Your own lists |
| `GET/PUT/DELETE Reviews/{itemId}` | signed in | Household reviews (your own; admins can remove any) |
| `GET Ratings/{itemId}` | signed in | Scores and where to watch |
| `PUT Maintenance`, `PUT/DELETE Branding`, `PUT Branding/Logo` | admin | Maintenance notice and branding |
| `GET Requests/Features` | signed in | What you can use: requests, calendar, downloads, admin tools |
| `GET Requests/Search`, `Discover/{row}`, `Genres/{type}` | signed in | Seerr search and discovery, as you |
| `GET Requests/Title/{type}/{tmdbId}[/{related}]`, `Requests/ForItem/{itemId}` | signed in | A title's Seerr page, recommendations, similar titles |
| `POST Requests`, `GET Requests`, `POST Requests/{id}/{approve,decline,cancel}` | signed in | Make, list and manage requests (Seerr permissions apply) |
| `GET Requests/Quota`, `GET/POST Requests/Issues`, `GET/POST Requests/Issues/{id}/...` | signed in | Request limits; problem reports and comments |
| `PUT Requests/Preferences` | signed in | Your own auto-request choice |
| `GET/PUT Requests/Settings`, `GET Requests/Users`, `POST Requests/Users/Import` | admin | Request options; Seerr accounts and import |
| `GET/POST Watchlist`, `DELETE Watchlist/{type}/{tmdbId}` | signed in | Your watchlist (synced with Seerr) |
| `GET Calendar?start=&end=` | signed in | Upcoming episodes, films and albums (up to 62 days) |
| `GET Downloads` | signed in | All downloads (admins) or your requests' downloads |
| `GET Arr/Item/{itemId}`, `POST Arr/Item/{itemId}/{Search,Subtitles}` | admin | Radarr/Sonarr/Bazarr info and searches for a title |
| `GET Arr/Health`, `GET Arr/Stats`, `POST Arr/TagSync` | admin | App health, Jellystat statistics, copy *arr tags now |
| `GET Img/{source}?p=&s=` | anyone | Posters passed through the server (signed addresses only) |

## License

This plugin is free software under the **GNU GPL v3** (see [LICENSE](LICENSE)), because Jellyfin
plugins link Jellyfin's GPL-3.0 libraries.

**Lumara Web**, which the release zips include in a separate `lumara-web` folder, is a separate
proprietary program with its own license (`lumara-web/LICENSE.txt`). It is not compiled into the
plugin; the plugin serves its files over HTTP, and the GPL here does not extend to it.

Lumara is not affiliated with Jellyfin, Seerr or the *arr projects.
