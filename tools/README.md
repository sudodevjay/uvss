# Bench test tools

Small console programs used while bringing up the Basler a2A1920-51gcBAS on
the bench. None of them are part of the service. Stop UvssService before
running pylonprobe or focusassist, because only one program can open the
camera at a time.

| Tool | What it does | Run |
|---|---|---|
| `pylonprobe` | Lists pylon devices and test-grabs frames, then reports OK/failed counts. This is the quickest camera/NIC health check. | `dotnet run --project tools/pylonprobe -- --grab --fps 30 --n 600` |
| `focusassist` | Opens a live view and shows a sharpness score (Laplacian) for the image centre. Turn the lens ring until the score peaks. S saves a frame, Q quits. | `dotnet run --project tools/focusassist -- [seconds] [saveDir]` |
| `synth` | Builds synthetic passes offline (a real background plus a real texture moving at a known speed) and runs them through `FrameMosaic`/`DisMotionEstimator`. Use it to check speed independence without the camera. | `dotnet run --project tools/synth -- <dir with synth_src/bg.jpg + obj.jpg> [diag <px/frame>]` |

pylonprobe options: `--auto`, `--dump`, `--settle`, `--robust`, `--fps`,
`--n` (frames), `--ps` (packet size), `--ipd` (inter-packet delay), `--pf`,
`--w/--h`, `--drv`, `--tl`.

The Basler.Pylon reference points to the default pylon 26.x install path
(`C:\Program Files\Basler\pylon\...`).

## Bench notes (2026-10-13)

- Full-res 1920x1200 @ 30 fps grabs cleanly once jumbo frames are on: NIC
  Jumbo Packet 9014 and camera `GevSCPSPacketSize` 8192.
- Bursts of "buffer was incompletely grabbed" errors showed up during passes
  on the 4-core i7-8665U laptop:
  - Camera only (pylonprobe, 20 s): 0 failures.
  - App idle: 0 failures.
  - App with entry/exit pressed and nothing moving (15 s): 0 failures.
  - Real passes with the dashboard open: 126 and 182 failures, about 75k NIC
    discards. CPU was at 22–60% at the time.
  - One real pass with the dashboard closed: 0 failures.
  - A later pass with the dashboard closed: 274 failures.

  The cause is still open. Battery power, builds, or a browser recorder
  running during a pass made the failures clearly worse. Run no other heavy
  work during passes.
- Manual trigger without a loop controller: `POST /lanes/{lane}/manual/entry`
  and `POST /lanes/{lane}/manual/exit`. Press exit within 30 s of entry,
  otherwise the pass is reset as a sensor fault and is not saved.
- Focus is a manual lens ring, so lock it with focusassist (see the
  `UVSS Focus Assist*.png` screenshots in the repo root). In office light the
  camera runs at about 2000 µs and 19–24 dB gain, which makes the image noisy.
  An under-vehicle illuminator is the real fix.
