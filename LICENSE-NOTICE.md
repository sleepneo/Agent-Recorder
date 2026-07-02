# License Notice

## Agent Recorder

Agent Recorder source code and documentation are licensed under the MIT License.
See `LICENSE` in this package.

## FFmpeg

This package includes FFmpeg command-line binaries (`ffmpeg.exe`, `ffprobe.exe`,
and associated DLLs). Agent Recorder invokes FFmpeg as a local process for
screen capture and MP4 output.

The bundled FFmpeg build reports `--enable-gpl --enable-version3` in its
configuration, so the included FFmpeg binaries are redistributed under the GNU
General Public License version 3 or later (GPLv3+).

- FFmpeg website: https://ffmpeg.org/
- FFmpeg source code: https://github.com/FFmpeg/FFmpeg
- FFmpeg legal information: https://ffmpeg.org/legal.html
- GPLv3 license text: https://www.gnu.org/licenses/gpl-3.0.txt

Agent Recorder's MIT license applies to the Agent Recorder project itself. The
FFmpeg binaries remain subject to FFmpeg's own license terms.
