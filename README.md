# STRM Download

A minimal **Jellyfin 12** plugin that downloads the HTTP(S) media behind `.strm`
files through the standard Download endpoint. Streamer-agnostic, with immediate
proxying, resume support and no media writes to disk. No configuration required.

## Install

- Add [this repository](https://github.com/einarjegorov/jellyfin-strm-dl/releases/latest/download/manifest.json)
  in **Dashboard → Plugins → Repositories**.
- Install **STRM Download** from the catalog.
- Restart Jellyfin.

For manual installation, download the ZIP from
[Releases](https://github.com/einarjegorov/jellyfin-strm-dl/releases), stop Jellyfin,
and extract it into a `StrmDownload` subfolder of your server's plugin directory.
Restart Jellyfin.

## Compatibility

- Requires Jellyfin **12**. Only `GET /Items/{itemId}/Download` is intercepted;
  Jellyfin's permission checks and download logging run first.
- Each `.strm` must contain one absolute HTTP(S) file URL in UTF-8, up to 64 KiB.
  Blank lines, comments and a UTF-8 BOM are accepted. No transcoding, HLS/DASH
  assembly or provider-specific authentication APIs.
- Resume depends on upstream range support and a stable file. Transfers use a
  64 KiB buffer; cancellation stops upstream reads. Streamer and external proxy
  caches are independent.
- Filename headers use the upstream filename, URL or MIME type. The official
  Android client may keep `.strm` because it chooses its filename from library
  metadata. Other clients may honor the supplied filename.

Only trusted administrators should control pointer files: LAN streamers are
allowed. Jellyfin credentials and client cookies are not forwarded upstream;
signed URLs are preserved and URL credentials are not logged by the plugin.

## Build and test

Requires the .NET 10 SDK, Make and Python 3.11+.

```sh
make test
make build
```

Source is in `src/`, tests in `tests/`, and the built DLL in `artifacts/`.
Run `make package` to also generate the install ZIP and catalog.

## License

Project source is licensed under [MIT](LICENSE). Compiled plugin releases are
distributed under [GPL-3.0-only](LICENSE.GPL-3.0), following
[Jellyfin's licensing guidance](https://github.com/jellyfin/jellyfin-plugin-template#licensing).
Releases include both license texts, [notices](NOTICE), and links to the matching
source tag and archive. The source license remains MIT.

## AI Disclosure

AI-assisted tools were used during the development of this plugin.
