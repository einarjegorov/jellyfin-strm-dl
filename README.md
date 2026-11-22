# STRM Download

A minimal **Jellyfin 12** plugin that downloads the HTTP(S) media behind `.strm`
files through the standard Download endpoint. Streamer-agnostic, with immediate
proxying, resume support and no media writes to disk. No configuration required.

## Install

- Add [this repository](https://github.com/einarjegorov/jellyfin-strm-dl/releases/latest/download/manifest.json)
  in **Dashboard → Plugins → Repositories**.
- Install **STRM Download** from the catalog.
- Restart Jellyfin.

The repository URL becomes available when the first GitHub release is published.

For manual installation, download the DLL from
[Releases](https://github.com/einarjegorov/jellyfin-strm-dl/releases), stop Jellyfin,
and copy it into a `StrmDownload` subfolder of your server's plugin directory.
Restart Jellyfin. Do not copy dependency DLLs or test output.

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

Requires [mise](https://mise.jdx.dev/), Make and Python 3.11+:

```sh
mise trust
mise install
make test
make package
```

`artifacts/` contains the DLL, installable ZIP, populated catalog and SHA-256
checksums. Automated tests use in-memory HTTP handlers; test the final DLL with
your server and clients before releasing.

The source is split into the download result filter, bounded STRM reader, HTTP
proxy, filename handling and plugin registration. Tests cover HTTP behavior,
credential isolation, cancellation, metadata and release packaging.

## Release

Update `Version` and `AssemblyVersion` in the plugin project, run the checks above,
then push a matching `v<Version>` tag. GitHub Actions tests and packages the plugin
before publishing the release assets. The tracked `manifest.json` is the catalog
template; the release asset contains the version, download URL and ZIP checksum
required by Jellyfin's **Manage repositories** feature.

## License

Project source is licensed under [MIT](LICENSE). Compiled plugin releases are
distributed under [GPL-3.0-only](LICENSE.GPL-3.0), following
[Jellyfin's licensing guidance](https://github.com/jellyfin/jellyfin-plugin-template#licensing).
Releases include both license texts, [notices](NOTICE), and links to the matching
source tag and archive. The source license remains MIT.

## AI Disclosure

AI-assisted tools were used during the development of this plugin.