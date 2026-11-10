# Wave Function Collapse

[![Godot](https://img.shields.io/badge/Godot-478CBF?logo=godotengine&logoColor=white)](https://godotengine.org/)
[![C#](https://img.shields.io/badge/C%23-512BD4?logo=dotnet&logoColor=white)](https://learn.microsoft.com/dotnet/csharp/)

A tile-based [Wave Function Collapse](https://github.com/mxgmn/WaveFunctionCollapse) generator in Godot and C#. It builds a road network out of 3D voxel tiles, chunk by chunk, using adjacency rules loaded from JSON.

![WFC generating a road network](media/showcase.gif)

## Features

- **Lowest-entropy collapse.** Each step picks one of the cells with the fewest remaining options and collapses it to a tile, chosen at random by tile weight.
- **Constraint propagation.** A collapsed cell narrows down its neighbours' options, recursively up to a fixed depth.
- **Local backtracking.** When a cell runs out of options, the generator uncollapses the area around it and tries again, instead of restarting the whole map.
- **Chunks.** The world is split into chunks, each drawn by its own `GridMap`, and constraints propagate across chunk borders. More chunks can be added at runtime with `AddChunk`.
- **JSON rulesets.** Tiles, weights and allowed neighbours for each direction are defined in `Rules/*.json`, along with chunk sizes and a seed (`0` picks a random one).
- **Timing metrics.** Once generation finishes, the average time spent in each phase is printed in microseconds.

## Running it

1. Open the project in [Godot 4 .NET](https://godotengine.org/download) (made in 4.3, also runs in 4.7).
2. Run the main scene, `Scenes/WFC3D.tscn`.

| Key | Action |
|---|---|
| <kbd>W</kbd> <kbd>A</kbd> <kbd>S</kbd> <kbd>D</kbd> | Pan |
| <kbd>Q</kbd> / <kbd>E</kbd> | Zoom out / in |
| <kbd>F</kbd> | Frame the whole board |

To try a different ruleset, select the `WFC` node and change `Ruleset File Path`. `Cycles Per Process` sets how many collapse steps run per frame; lower it to watch generation step by step.

| Ruleset | Board |
|---|---|
| `RulesDefault.json` | City roads, 3×3 chunks of 10×10 tiles |
| `RulesTest1.json` | City roads, 4×4 chunks of 25×25 tiles |
| `RulesTest2.json` | City roads with turns, 20×20 chunks of 25×25 tiles |
| `RulesBlank.json` | Empty template |

## Known limitations

- Backtracking can get stuck in a loop, repeatedly placing and removing the same tiles. Lowering the propagation depth makes this rarer but doesn't fix it.
- `RulesOverworld.json` is unfinished and doesn't work.
- `Scripts/WaveFunctionCollapse.cs` and `Scenes/Main.tscn` are an earlier single-chunk 2D version, kept for reference.

## Credits

- 3D Overworld Tileset by MariaIsMe (itch.io), used by the unfinished Overworld ruleset.
