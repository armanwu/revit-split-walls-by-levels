# Split Walls by Selected Levels (Straight Walls)

Revit ExternalCommand to split **selected straight Basic Walls** by **selected Levels**, replacing the original walls with per-level segments.

## How to use
1. Select the walls you want to split.
2. Ctrl+select the Levels to use as split boundaries.
3. Run the command.

## Notes
- Straight **Basic Walls only**.
- Curtain/Stacked/Profile-edited walls are skipped.
- If no selected Level falls between the wall Base/Top, the wall is skipped (not deleted).
