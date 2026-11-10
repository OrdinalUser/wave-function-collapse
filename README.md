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

| Key                                                 | Action                |
| --------------------------------------------------- | --------------------- |
| <kbd>W</kbd> <kbd>A</kbd> <kbd>S</kbd> <kbd>D</kbd> | Pan                   |
| <kbd>Q</kbd> / <kbd>E</kbd>                         | Zoom out / in         |
| <kbd>F</kbd>                                        | Frame the whole board |

To try a different ruleset, select the `WFC` node and change `Ruleset File Path`. `Cycles Per Process` sets how many collapse steps run per frame; lower it to watch generation step by step.

| Ruleset             | Board                                              |
| ------------------- | -------------------------------------------------- |
| `RulesDefault.json` | City roads, 3×3 chunks of 10×10 tiles              |
| `RulesTest1.json`   | City roads, 4×4 chunks of 25×25 tiles              |
| `RulesTest2.json`   | City roads with turns, 20×20 chunks of 25×25 tiles |
| `RulesBlank.json`   | Empty template                                     |

## Known limitations

- Backtracking can get stuck in a loop, repeatedly placing and removing the same tiles. Lowering the propagation depth makes this rarer but doesn't fix it.
- `RulesOverworld.json` is unfinished and doesn't work.
- `Scripts/WaveFunctionCollapse.cs` and `Scenes/Main.tscn` are an earlier single-chunk 2D version, kept for reference.

## Performance

Ad-hoc numbers from the built-in timers, not a proper benchmark or profile. All runs use seed `5`, on an i7-12700K, 32 GB DDR5 and an RTX 3070 Ti under Windows 11. Times are averages per call.

|                   | `RulesDefault`  |   `RulesTest2`    |   `RulesTest2`    |
| ----------------- | :-------------: | :---------------: | :---------------: |
| Board             |   30×30 tiles   |   500×500 tiles   |   500×500 tiles   |
| Chunk size        |      10×10      |       25×25       |       25×25       |
| Cycles per frame  |       25        |        25         |       1000        |
| **Total time**    |   **0.36 s**    |     **162 s**     |    **18.7 s**     |
| Cycles            |       910       |      250,401      |      250,401      |
| Cycle             |     63.1 µs     |      72.2 µs      |      62.6 µs      |
| Collapse          |     12.2 µs     |      17.6 µs      |      15.4 µs      |
| Entropy selection |     8.4 µs      |      15.9 µs      |      14.3 µs      |
| Constrain (calls) | 17.4 µs (2,627) | 16.8 µs (805,597) | 14.5 µs (805,597) |
| Uncollapse        |  not triggered  |   not triggered   |   not triggered   |

What the numbers show:

- **Per-step cost barely grows with the world.** The board grows about 280×, but a cycle only goes from 63 to 72 µs. Entropy selection scans just the current chunk, so its cost follows the chunk size (10×10 → 25×25), not the world size.
- **At 25 cycles per frame, the run is limited by the framerate.** The actual work on the large board is about 250,401 × 72 µs ≈ 18 s, but the run takes 162 s because only 25 cycles run per frame. At 1000 cycles per frame, the total drops to 18.7 s, close to the raw compute time.

<details>
<summary>Raw output</summary>

`RulesDefault`, 25 cycles per frame:

```
Total time taken: 0.36111111111111094 s
Cycle: {Count: 910, Average time: 63.137362637362635us }
Collapse: {Count: 909, Average time: 12.177117711771178us }
> Entropy selection: {Count: 909, Average time: 8.391639163916391us }
> Constrain: {Count: 2627, Average time: 17.403121431290444us }
Uncollapse: {Count: 0, Average time: NaNus }
```

`RulesTest2`, 25 cycles per frame:

```
Total time taken: 162.0746573333354 s
Cycle: {Count: 250401, Average time: 72.15060243369635us }
Collapse: {Count: 250400, Average time: 17.63461661341853us }
> Entropy selection: {Count: 250400, Average time: 15.94930910543131us }
> Constrain: {Count: 805597, Average time: 16.766754344914393us }
Uncollapse: {Count: 0, Average time: NaNus }
```

`RulesTest2`, 1000 cycles per frame:

```
Total time taken: 18.702137333333326 s
Cycle: {Count: 250401, Average time: 62.56124376500094us }
Collapse: {Count: 250400, Average time: 15.418849840255591us }
> Entropy selection: {Count: 250400, Average time: 14.328298722044728us }
> Constrain: {Count: 805597, Average time: 14.482707855168279us }
Uncollapse: {Count: 0, Average time: NaNus }
```

</details>

## Credits

- 3D Overworld Tileset by MariaIsMe (itch.io), used by the unfinished Overworld ruleset.
