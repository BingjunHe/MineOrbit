# Third-party notices

MineOrbit uses CmlLib.Core, CmlLib.Core.Auth.Microsoft, CmlLib.Core.Installer.Forge and XboxAuthNet under their MIT licenses; .NET, Microsoft.Extensions and related libraries under their included licenses; Serilog under Apache-2.0. Transitive dependencies retain their own licenses, including SharpZipLib (MIT), LZMA-SDK (public domain) and Microsoft WebView2 (Microsoft terms).

See `licenses/` for upstream CmlLib license texts, package metadata and packaged license/notice files, and `docs/dependency-manifest.json` for resolved application packages. The bundled .NET runtime and WPF license and third-party notice texts are included under `licenses/`; these must remain with the distribution. NuGet metadata includes project and license URLs for packages whose archive supplies a license expression rather than a text file.

Minecraft game files, Java runtimes and Mod Loader artifacts are downloaded only on request to the selected instance. They are not included in the release ZIP and remain subject to their upstream terms. Forge installation uses upstream library primitives without the high-level wrapper that opens an advertising URL.
