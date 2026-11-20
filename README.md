# Jellyfin FilmAffinity Ratings Plugin

A Jellyfin **12** (`.NET 10`) metadata plugin that fetches **FilmAffinity** scores for movies and TV series and writes them to Jellyfin's native `CommunityRating` (and optionally `CriticRating`).

## Features

- **Native `CommunityRating` (0–10 ★)** — works across every Jellyfin client (Web, Android TV, Apple TV, Swiftfin, Findroid, etc.).
- **Deterministic ID matching via Wikidata (`P480`)** — resolves FilmAffinity IDs from existing IMDb (`P345`) and TMDb (`P4947` / `P4983`) provider IDs with zero fuzzy title-search ambiguity.
- **Clickable FilmAffinity External Link (`IExternalId`)** — stores `ProviderIds["FilmAffinity"]` and adds a clickable FilmAffinity link (`es` or `en`) to every matched Movie and Series.
- **Dual-mode Score Endpoint**:
  - **JSON API mode** for a self-hosted or third-party score service (`GET {BaseUrl}/score/{id}`).
  - **HTML mode** for a FilmAffinity reverse proxy/mirror (or direct FilmAffinity HTML if not blocked by Cloudflare).
- **Both Metadata Provider & Scheduled Task**:
  - Runs automatically on library scans/refreshes for Movies and Series.
  - Includes a daily *"Update FilmAffinity Ratings"* scheduled task to backfill and refresh existing libraries.
- **Polite & Resilient**:
  - Configurable request throttling (`ThrottleMs`), minimum vote threshold (`MinVotes`), optional `CriticRating` (`rating × 10`), Cloudflare `403` detection with clear log warnings, and in-memory TTL caching (6h for recent releases, 24h for older titles).
  - Never overwrites existing TMDb/IMDb ratings when a title has no FilmAffinity match or the endpoint fails.

## Installation (Custom Repository)

1. In Jellyfin, go to **Dashboard → Plugins → Repositories**.
2. Click **+** and add:
   - **Repository Name**: `FilmAffinity Ratings`
   - **Repository URL**:
     ```
     https://raw.githubusercontent.com/pportxyz/jellyfin-plugin-filmaffinity/main/manifest.json
     ```
3. Open **Dashboard → Plugins → Catalog**, install **FilmAffinity Ratings**, and restart Jellyfin.
4. Configure your **Score Endpoint URL** under **Dashboard → Plugins → FilmAffinity Ratings**.
5. Run **Dashboard → Scheduled Tasks → Update FilmAffinity Ratings** to populate scores for your existing library.

## External Score Endpoint Contract

Because `filmaffinity.com` has no official public API and sits behind Cloudflare (which blocks standard HTTP clients with `HTTP 403`), this plugin lets you point **Score Endpoint URL** at your own fetcher, aggregator, or reverse proxy:

- **URL templates** support `{id}` and `{lang}` placeholders:
  - `http://localhost:8080/score/{id}`
  - `https://my-proxy.local/{lang}/film{id}.html`
  - If `{id}` is omitted (e.g. `http://localhost:8080`), `/score/{id}` is appended automatically.
- **JSON response example**:
  ```json
  {
    "rating": 9.0,
    "votes": 175432
  }
  ```
  Accepted rating keys: `rating`, `score`, `average`, `ratingValue` (number or string, `.` or `,` decimal).
  Accepted vote keys (optional): `votes`, `voteCount`, `vote_count`, `ratingCount`, `count`.
- **HTML response**: parses FilmAffinity's `#movie-rat-avg` / `itemprop="ratingValue"` and `itemprop="ratingCount"` markup.

## Local Development (Docker)

Build and run unit tests without installing the .NET SDK locally:

```bash
docker run --rm -v "$PWD:/src" -w /src mcr.microsoft.com/dotnet/sdk:10.0 \
  dotnet test Jellyfin.Plugin.FilmAffinity.slnx --configuration Release
```

## Releasing

Push a version tag (e.g. `git tag v1.0.0 && git push origin v1.0.0`). GitHub Actions will build the `net10.0` plugin, package the `.zip`, compute its MD5 checksum, create the GitHub Release, and update `manifest.json` on `main`.

## License

GPL-3.0 — see [LICENSE](LICENSE).
