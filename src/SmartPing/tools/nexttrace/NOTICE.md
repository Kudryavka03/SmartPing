# Bundled NextTrace and WinDivert

This directory distributes unmodified third-party binaries for Windows x64.
NextTrace runs as a separate process; these notices concern the bundled
third-party components and do not assign a license to SmartPing source code.
The binaries are provided without warranty under their respective licenses.
Keep this notice, both license files, the manifest, and the runtime files together
when copying or redistributing a published SmartPing directory.

## NextTrace 1.7.4

- Copyright and attribution: the NextTrace authors and contributors.
- Project: <https://github.com/nxtrace/NTrace-core>
- License: GNU General Public License version 3; full text is in
  `LICENSE.NextTrace.txt`.
- Unmodified release: <https://github.com/nxtrace/NTrace-core/releases/tag/v1.7.4>
- Exact corresponding source (including build scripts, `go.mod`, and `go.sum`):
  <https://github.com/nxtrace/NTrace-core/tree/8884923d9b022d8a25b7fbf69e4ae64fa65eb1c7>
- Machine-readable source archive, available without charge:
  <https://github.com/nxtrace/NTrace-core/archive/8884923d9b022d8a25b7fbf69e4ae64fa65eb1c7.tar.gz>

`win-x64/ntrace.exe` is the official `nexttrace_windows_amd64.exe`, renamed
without changing its contents. Its SHA-256 matches the digest supplied by the
official GitHub release API. The source tree contains its dependency versions
and checksums and the upstream build instructions. Recipients may replace or
rebuild this executable under its license.

## WinDivert 2.2.2-A

- Copyright: Basil and the WinDivert contributors (see upstream source notices).
- Project: <https://github.com/basil00/WinDivert>
- Upstream offers a choice of LGPL version 3 or GPL version 2. This bundle
  distributes the unmodified WinDivert files under LGPL version 3. The complete
  upstream license file, including LGPLv3, GPLv3, and GPLv2, is retained as
  `LICENSE.WinDivert.txt`.
- Release: <https://github.com/basil00/WinDivert/releases/tag/v2.2.2>
- Exact corresponding source, including driver and library build scripts:
  <https://github.com/basil00/WinDivert/tree/1789526ecfb9ff5397c94f9f54c1a3dc2fb60440>
- Machine-readable source archive, available without charge:
  <https://github.com/basil00/WinDivert/archive/1789526ecfb9ff5397c94f9f54c1a3dc2fb60440.tar.gz>

The DLL and driver are byte-for-byte identical both to the runtime embedded in
the pinned NextTrace source and to the `x64` files in the official
`WinDivert-2.2.2-A.zip` release archive. No library or driver has been modified.
They remain separate files so recipients can inspect or replace them under
their license. Their individual hashes and upstream download links are recorded
in `manifest.json`; the archive SHA-256 there was measured from the official
download because that release does not publish an asset digest.

## Deployment and redistribution

Copy the entire published directory. A normal build and a Windows x64 publish
copy these files as loose content, including in a single-file SmartPing publish.
SmartPing does not invoke `--init`, download tools, or elevate itself. TCP tracing
requires administrator privileges; WinDivert itself may load/register its kernel
driver when NextTrace opens it, as documented by upstream. Operating-system
policy can still prevent driver loading.

This bundle only supplies x64 binaries. Native Windows ARM64 builds do not
include this x64 runtime and require a separately prepared compatible tool or
the Windows `tracert` fallback for basic ICMP tracing.

For public redistribution, provide equivalent access to the corresponding source
and retain the source directions above alongside the binaries. The pinned
third-party source locations are the distribution's source access mechanism;
redistributors must keep that access available while distributing the binaries,
or mirror the corresponding source and update these directions. No source-code
access charge, extra restriction, or restriction on license-permitted reverse
engineering is imposed by this bundle.
