# Captioned opaque grid regression

These PNGs contain synthetic VHP Training rows, retained from the failed November 4, 2026 exploration `518d96799b984cb797d61690069ab9bc` (tiles 0001 and 0003). They contain no production guest records.

The grid caption stays fixed while the columns move left by 53 pixels. The old layout detector incorrectly selected the caption at y=3 as the header. Its short text strokes satisfied the vertical-rule detector, even though they did not continue into the body. The regression test must identify the real header below the caption and verify the horizontal displacement without relaxing registration thresholds.
