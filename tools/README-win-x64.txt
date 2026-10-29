Shaft Additionality — Windows 64-bit build (self-contained)
============================================================

HOW TO RUN
----------
1. Extract this whole folder somewhere (e.g. Desktop\ShaftAdditionality).
2. Double-click ShaftAdditionality.exe.

That's it. The .NET runtime is embedded in the exe — nothing to install.
The dist\ folder next to the exe holds the game's textures, sounds and
models; keep it there.

If Windows SmartScreen warns you (the exe is not code-signed):
click "More info" -> "Run anyway".

CONTROLS
--------
  Click on window     capture mouse (look)        Esc        release mouse / menu
  W A S D             move                        Space      jump
  Shift               sneak                       Ctrl       sprint
  Mouse               look around                 Wheel      cycle hotbar
  Left mouse (hold)   mine block                  Right mouse  place / use
  1 - 9               select hotbar slot          E          inventory & crafting
  U                   gamemode menu               T or /     chat / commands
  M                   toggle music                N          reload chunks

Worlds are saved in the "saves" folder next to the exe.

TROUBLESHOOTING
---------------
  All options:          ShaftAdditionality.exe --help
  Skip title screen:    ShaftAdditionality.exe --start
  No sound card:        ShaftAdditionality.exe --no-audio
  Window size:          ShaftAdditionality.exe --width 1600 --height 900
                        (or --fullscreen)

Built from the C# / Silk.NET port of the original JavaScript game
(repo commits 7747b78 onward). Verified: 101 unit tests and 154
runtime selftest checks pass on the published binaries.
