# SharpCaster for Musync

This source dependency is SharpCaster 3.0.0, pinned to upstream commit
`d04aa95f8a518c6b0434012e3feee7ce3f36ef61` from
https://github.com/Tapanila/SharpCaster. The MIT license is retained in
LICENSE.txt. UPSTREAM.json records the original Git blob and SHA-256 hash of
each source file, verified against the pinned upstream tree before copying.
Its NormalizedSha256 fields normalize CRLF to LF for comparison across
checkouts while retaining any UTF-8 BOM.
Upstream whitespace is retained; the source hash verifier, rather than
formatting cleanup, guards changes to the vendored files.

Musync needs every entry of a Cast MEDIA_STATUS notification: its accepted
playback can appear after another session. The published dependency only
raises StatusChanged for the first entry. The local repair in
Source/Channels/MediaChannel.cs raises the existing event for each entry,
in order, while retaining the original first-entry MediaStatus property.
The maintained patch is patches/0001-forward-all-media-status.patch.

Connection, TLS, heartbeat, request correlation, serialization, discovery,
and playback command implementation remain identical to the pinned source.
Musync continues to select its own accepted media ID and ignore foreign or
old sessions. No reflection or mutation of the global NuGet cache is used.

SharpCaster.Musync.csproj builds the upstream .NET 9 target, consumed by the
.NET 10 Musync application, with the same external runtime dependency
versions as the NuGet package. CSharp and System.Text.Json are supplied by
the target framework. The project pins C# 13 and explicit usings, preserving
upstream compilation semantics. Public nullable annotations remain enabled;
flow warnings in the unchanged upstream source are outside Musync's edits.
It replaces the NuGet reference and retains the
Sharpcaster assembly name. The project does not add upstream packaging
analyzers or distribute another package.

To review or update, compare each source against UPSTREAM.json, review and
reapply the documented patch, run verify-source.ps1, and run Musync's full
core/network suite. The verifier checks both unchanged and patched hashes
and rejects extra source files; update its pin explicitly for an upstream update.
Loopback TLS tests cover mixed-session notifications, early statuses,
expected stop, remote replacement and existing request ownership. Release
license collection includes the license and provenance, and excludes this
source project's build directories.
