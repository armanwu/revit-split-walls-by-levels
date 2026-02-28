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

## Install
1. Build the project in Visual Studio (**Release**, x64 if available).
2. Copy the compiled `.dll` to a folder, e.g. `C:\MyRevitAddins\SplitWalls\`.
3. Create a `.addin` file in:
   `%APPDATA%\Autodesk\Revit\Addins\20XX\` (match your Revit version)
4. Point the `.addin` to your DLL (example below).
5. Restart Revit.

### Example .addin
```xml
<?xml version="1.0" encoding="utf-8" standalone="no"?>
<RevitAddIns>
  <AddIn Type="Command">
    <Name>SplitWallsBySelectedLevels</Name>
    <Assembly>C:\MyRevitAddins\SplitWalls\SplitWallsBySelectedLevels.dll</Assembly>
    <AddInId>8D83C886-B739-4ACD-A9DB-2D1C21B2F2AA</AddInId>
    <FullClassName>SplitWallsBySelectedLevels_ListCurveOnly.CmdSplitWallsBySelectedLevels</FullClassName>
    <VendorId>ARMN</VendorId>
    <VendorDescription>Split selected straight basic walls by selected levels</VendorDescription>
  </AddIn>
</RevitAddIns>
