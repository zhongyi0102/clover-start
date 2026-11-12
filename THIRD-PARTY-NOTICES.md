# Third-party notices

## Material Color Utilities — scalar color mathematics

Copyright 2021 Google LLC.

Source: https://github.com/material-foundation/material-color-utilities/blob/main/typescript/utils/color_utils.ts

License: Apache License, Version 2.0; full text in `licenses/Apache-2.0.txt`.

`src/core/MaterialColorMath.cs` adapts the sRGB linearization/delinearization and
L*/Y conversion functions to .NET Framework-compatible C#. Bounds checking and
names were changed. No full HCT quantizer, C# package, Rust binary, or source
generator is included. Wallpaper extraction/ranking and semantic palette
assembly in `WallpaperAppearance.cs` are CloverStart's own implementation.

## Design references (no copied source)

- Matugen: https://github.com/InioX/matugen — wallpaper-derived semantic palettes.
- Dusky merged PR #359: https://github.com/dusklinux/dusky/pull/359 — achromatic
  input uses neutral colors without overwriting the saved scheme.
- pywal PR #543 (not merged): https://github.com/dylanaraps/pywal/pull/543 —
  foreground/background pairs must also be checked in selected states.
- WinUI issue #5707 (open): https://github.com/microsoft/microsoft-ui-xaml/issues/5707
  — backdrop fallback must preserve foreground contrast.

These references are not runtime dependencies. No GPL-licensed source from
Matugen has been incorporated.
