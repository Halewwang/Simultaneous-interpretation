# Third-party notices

## Microsoft Windows driver samples

- Repository: `https://github.com/microsoft/Windows-driver-samples.git`
- Resolved commit: `2ee527bfeb0aeb6be11f0a8b6dce4011b358ce89`
- Source directories used:
  - `audio/simpleaudiosample/Source/Main`
  - `audio/simpleaudiosample/Source/Filters`
  - `audio/simpleaudiosample/Source/Inc`
  - `audio/simpleaudiosample/Source/Utilities`
- Related SYSVAD implementation reviewed:
  - `audio/sysvad`
  - `audio/sysvad/EndpointsCommon`
- License: Microsoft Public License (MS-PL), reproduced in full below.

The Simple Audio Sample is the smallest current Microsoft WaveRT virtual-audio
sample in the same driver-samples repository as SYSVAD. EMKE imports only its
driver source/header/resource files. The sample repository is not a submodule,
and the resolved commit above is the immutable provenance reference. The EMKE
cross-endpoint data path is local code rather than an assertion that a SYSVAD
physical connection or loopback pin is a cross-endpoint bridge.

### Local modifications

- Flattened the four sample source directories into
  `Windows/driver/EMKE.VirtualAudio/src/` and combined their original projects
  into one x64 driver project.
- Renamed project, binary, resource metadata, service, root hardware ID, and
  endpoint reference strings for EMKE.
- Expanded the sample's one render and one capture endpoint to two render and
  two capture WaveRT miniport pairs.
- Added two fixed-capacity, preallocated SPSC bridges called directly from the
  WaveRT render/capture data-movement callbacks. The bridge design follows the
  bounded/nonpaged stream-state conventions reviewed in
  `audio/sysvad/EndpointsCommon`; it does not import SYSVAD tone, keyword,
  offload, or same-filter loopback features.
- Removed the sample capture tone generator and render file-sink/work-item
  implementation from the EMKE driver.
- Replaced the sample PCM endpoint tables with one exact 48 kHz stereo IEEE
  Float32 contract.
- Added one shared native endpoint-contract header compiled by the driver and
  native host, containing the endpoint-role property key, role strings, driver
  ABI 1, and the two user-facing plus two internal endpoint identities.
- Replaced the sample INF with the EMKE package INF and added fail-closed build
  and package-verification scripts. The distributable INF/SYS are copied from
  WDK `DriverPackageTarget` output; catalog membership is checked with Windows
  catalog APIs against the exact staged bytes.
- No Microsoft logos, trademarks, certificates, binaries, or package artifacts
  are copied.

### Microsoft Public License (MS-PL)

Copyright (c) 2015 Microsoft

This license governs use of the accompanying software. If you use the software,
you accept this license. If you do not accept the license, do not use the
software.

1. Definitions

The terms "reproduce," "reproduction," "derivative works," and "distribution"
have the same meaning here as under U.S. copyright law.

A "contribution" is the original software, or any additions or changes to the
software.

A "contributor" is any person that distributes its contribution under this
license.

"Licensed patents" are a contributor's patent claims that read directly on its
contribution.

2. Grant of Rights

(A) Copyright Grant- Subject to the terms of this license, including the
license conditions and limitations in section 3, each contributor grants you a
non-exclusive, worldwide, royalty-free copyright license to reproduce its
contribution, prepare derivative works of its contribution, and distribute its
contribution or any derivative works that you create.

(B) Patent Grant- Subject to the terms of this license, including the license
conditions and limitations in section 3, each contributor grants you a
non-exclusive, worldwide, royalty-free license under its licensed patents to
make, have made, use, sell, offer for sale, import, and/or otherwise dispose of
its contribution in the software or derivative works of the contribution in the
software.

3. Conditions and Limitations

(A) No Trademark License- This license does not grant you rights to use any
contributors' name, logo, or trademarks.

(B) If you bring a patent claim against any contributor over patents that you
claim are infringed by the software, your patent license from such contributor
to the software ends automatically.

(C) If you distribute any portion of the software, you must retain all
copyright, patent, trademark, and attribution notices that are present in the
software.

(D) If you distribute any portion of the software in source code form, you may
do so only under this license by including a complete copy of this license with
your distribution. If you distribute any portion of the software in compiled or
object code form, you may only do so under a license that complies with this
license.

(E) The software is licensed "as-is." You bear the risk of using it. The
contributors give no express warranties, guarantees or conditions. You may
have additional consumer rights under your local laws which this license cannot
change. To the extent permitted under your local laws, the contributors exclude
the implied warranties of merchantability, fitness for a particular purpose and
non-infringement.
