# AudioChoice Android FFmpeg build

AudioChoice builds FFmpeg from the official source release rather than depending on an abandoned
Android wrapper. The build is pinned to FFmpeg 8.1.2 and enables only the components required for
local AAX input and MP4/M4B stream-copy output.

Run `./scripts/build-ffmpeg-android.sh` from the repository root. The script builds both
`arm64-v8a` (physical 64-bit Android devices) and `x86_64` (the Pixel emulator).

FFmpeg is licensed under the GNU Lesser General Public License (LGPL), version 2.1 or later for
this configuration. Before public distribution, AudioChoice must include the applicable notices,
the corresponding FFmpeg source offer/source, and a relinking path for the statically linked
library objects. No GPL-only configure switches may be enabled without a separate distribution
review.
