# Third-party notices

The shared desktop in `src/UiAtlas.Shell` includes AI Successors' MapLock presentation
code and Windows application classification code. Its original copyright and
licensing notice is retained in `licenses/MapLock-shell-COPYRIGHT.txt`; extraction
does not change those terms.

This inventory is derived from the committed package lock files. Runtime redistribution includes the following components:

| Component | Version | Author/vendor | License | Use |
| --- | --- | --- | --- | --- |
| Google.Protobuf | 3.27.1 | Google | BSD-3-Clause | UI Maps sensor, perception and map serialization |
| Microsoft.Data.Sqlite | 10.0.10 | Microsoft | MIT | managed SQLite API |
| Microsoft.Data.Sqlite.Core | 10.0.10 | Microsoft | MIT | managed SQLite implementation |
| Microsoft.Windows.SDK.NET | 10.0.19041.57 | Microsoft | Microsoft Windows SDK license | Windows Runtime projection used only by the optional Windows OCR enrichment path |
| WinRT.Runtime | 2.2.0 | Microsoft | MIT | C#/WinRT runtime support required by the Windows OCR projection |
| Interop.UIAutomationClient | 10.19041.0 | Roman | MIT | native UI Automation 3 COM interop |
| SQLitePCLRaw.bundle_e_sqlite3 | 3.0.5 | Eric Sink and contributors | Apache-2.0 | native-provider bundle |
| SQLitePCLRaw.config.e_sqlite3 | 3.0.5 | Eric Sink and contributors | Apache-2.0 | provider configuration |
| SQLitePCLRaw.core | 3.0.5 | Eric Sink and contributors | Apache-2.0 | low-level managed API |
| SQLitePCLRaw.provider.e_sqlite3 | 3.0.5 | Eric Sink and contributors | Apache-2.0 | native-provider binding |
| SQLite | 3.53.4 | SQLite authors | Public domain | native database library |

Test-only dependencies are not shipped in the runtime archive: Microsoft.NET.Test.Sdk 17.14.1 and its Microsoft test-platform/code-coverage dependencies (MIT); xunit 2.9.3, xunit.runner.visualstudio 3.1.4, xunit.assert 2.9.3, xunit.core 2.9.3, xunit.extensibility.core 2.9.3, xunit.extensibility.execution 2.9.3, and xunit.analyzers 1.18.0 (Apache-2.0); xunit.abstractions 2.0.3 (Apache-2.0); and Newtonsoft.Json 13.0.3 (MIT).

Package metadata, lock files, and the SDK-generated runtime manifest are the version authority. Redistribution texts and notices are committed under `licenses/` and included in release archives: `Apache-2.0.txt`, `CsWinRT-MIT.txt`, `FirebirdSql.Data.FirebirdClient-IDPL-1.0.txt`, `Microsoft-Windows-SDK-NET-NOTICE.txt`, `Microsoft.Data.Sqlite-MIT.txt`, `Interop.UIAutomationClient-MIT.txt`, and `SQLite-public-domain.txt`. `Microsoft.Windows.SDK.NET.dll` is governed by Microsoft's Windows SDK license linked in its notice; it is not relicensed as MIT. The native Firebird engine is not bundled or downloaded by UiAtlas. No third-party application source code or graphical asset is copied into this repository.

The repository `.gitignore` is adapted from the CC0-1.0 GitHub Visual Studio template identified in `provenance/files.csv`. It is repository configuration and is not included in the runtime archive.

UI Maps Protocol, Perception Core, Map Core and the native Windows recorder/provider are source imports pinned in
`provenance/uimaps-foundation.json`. Their upstream MIT notices are preserved in
`vendor/uimaps/*/LICENSE.txt` and packaged under `licenses/uimaps/`. Only the
allowlisted mapping source is imported. Atlas build adapters and the extraction
of the local OCR contracts are identified separately in the provenance manifest.

Google.Protobuf's redistribution text is in `licenses/Google.Protobuf-BSD-3-Clause.txt`.
Grpc.Tools 2.71.0 (the gRPC Authors, Apache-2.0) is a build-only dependency used to
generate the bindings and is not redistributed in the runtime archive.

Historical source projects additionally use ModelContextProtocol.Core 2.2.0
(Apache-2.0), Microsoft.Extensions.AI.Abstractions 10.8.3 and Logging/DependencyInjection
Abstractions 10.0.10 (MIT), and FirebirdSql.Data.FirebirdClient 10.3.4 (IDPL-1.0).
Their notices remain in `licenses/` for source compatibility. Their executables and
runtime packages are excluded from the mapping-only release; the runtime SBOM
reflects that boundary.

The native Windows recorder uses System.Drawing.Common 9.0.8 and
Microsoft.Win32.SystemEvents 9.0.8 (Microsoft, MIT). Their license and third-party
notices are included under `licenses/`. The provider worker runs in its own
`native-recorder/` directory with its declared runtime dependencies.
