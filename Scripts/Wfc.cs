using Godot;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

/* Known Problems */
// Collapse might get stuck trying to update the sequence of tiles that have been uncollapsed before
//  Effect => Never ending cycle of placing and deleting
//  Remedy: Lessen constrain depth, but that creates more work for the computer in the long run and also DOESN'T SOLVE THE PROBLEM!

public partial class Wfc : Node
{
    /* Internal Config - avoid changing unless you know what you're doing */
    const int TILE_VOID_ID = 0;
    const int CONSTRAIN_MAX_DEPTH = 2; // 2 - 5 is good
    const int UNCOLLAPSE_MAX_DEPTH = 5; // 3 - 5 is good
    int CYCLES_PER_PROCESS = 25;

    /* Types */
    public struct TilePrototype
    {
        public string sprite; // Path to sprite file
        public Vector2I atlasCoords;

        public string model; // Path to model file

        public string name;
        public float weight;

        public int[] northConnections;
        public int[] westConnections;
        public int[] eastConnections;
        public int[] southConnections;
    }

    public struct Config
    {
        public int seed;
        public float meshSize;
        public string modelFolder;

        public int initChunkCountX, initChunkCountY; // How many chunks in a world  // 10x10 chunks  with 10x10 size ==> 100 x 100 world
        public Vector2I chunkSize; // How big are the chunks 
    }

    public enum ReduceReturn
    {
        NO_CHANGE, CHANGE, NO_OPTIONS
    }

    public enum Direction
    {
        NONE, NORTH, EAST, SOUTH, WEST
    }

    public class Chunk
    {
        public Vector2I m_GlobalCoord;
        public Vector2I m_Size;
        public Tile[,] m_Tiles;

        public GridMap m_GridMap;

        public Chunk(Vector2I chunkCoord, Vector2I size, ref List<int> allTileOptions, ref MeshLibrary meshLibrary, float gridMapSize)
        {
            m_GlobalCoord = chunkCoord;
            m_Size = size;
            m_Tiles = new Tile[m_Size.X, m_Size.Y];

            for (int x = 0; x < m_Size.X; x++)
            {
                for (int y = 0; y < m_Size.Y; y++)
                {
                    m_Tiles[x, y] = new Tile(ref allTileOptions);
                }
            }

            m_GridMap = new GridMap();
            m_GridMap.MeshLibrary = meshLibrary;
            m_GridMap.CellSize = new Vector3(gridMapSize, gridMapSize, gridMapSize);
            m_GridMap.Name = chunkCoord.ToString();
            m_GridMap.Translate(new Vector3(gridMapSize * m_GlobalCoord.X * m_Size.X, 0, gridMapSize * m_GlobalCoord.Y * m_Size.Y));
        }

        public void SetVisualGrid(Vector2I localCellPos, int tileId)
        {
            m_GridMap.SetCellItem(new Vector3I(localCellPos.X, 0, localCellPos.Y), tileId);
        }

        public List<Tuple<int, Vector2I>> GetCollapseCell()
        {
            // TODO: Optimize
            Vector2I selectedCell = Vector2I.Zero;
            List<Tuple<int, Vector2I>> entropyList = new List<Tuple<int, Vector2I>>();
            for (int y = 0; y < m_Size.Y; y++)
            {
                for (int x = 0; x < m_Size.X; x++)
                {
                    if (!m_Tiles[x, y].collapsed) entropyList.Add(new Tuple<int, Vector2I>(m_Tiles[x, y].GetEntropy(), new Vector2I(x, y)));
                }
            }
            if (entropyList.Count == 0) { return new List<Tuple<int, Vector2I>>(); } // No more uncollapsed chunks

            List<Tuple<int, Vector2I>> sortedEntropy = entropyList.OrderBy(o => o.Item1).ToList();
            List<Tuple<int, Vector2I>> lowestValueEntropyEntries = (from entry in sortedEntropy where entry.Item1 == sortedEntropy[0].Item1 select entry).ToList();
            return lowestValueEntropyEntries;
        }

        public ref List<int> GetTileOptions(Vector2I localCoord)
        {
            return ref m_Tiles[localCoord.X, localCoord.Y].options;
        }

        public int GetTileEntropy(Vector2I localCoord)
        {
            return m_Tiles[localCoord.X, localCoord.Y].GetEntropy();
        }

        public void ClearGridCell(Vector2I localCellPos, ref List<int> allOptions)
        {
            m_GridMap.SetCellItem(new Vector3I(localCellPos.X, 0, localCellPos.Y), -1);
            m_Tiles[localCellPos.X, localCellPos.Y] = new Tile(ref allOptions);
        }

        public void SetGridCell(Vector2I localCellPos, int tileId)
        {
            m_GridMap.SetCellItem(new Vector3I(localCellPos.X, 0, localCellPos.Y), tileId);
            m_Tiles[localCellPos.X, localCellPos.Y] = new Tile(tileId);
        }

        public ref Tile GetTile(Vector2I localCellPos)
        {
            return ref m_Tiles[localCellPos.X, localCellPos.Y];
        }

        public int GetTotalEntropy()
        {
            int totalEntropy = 0;
            for (int x = 0; x < m_Size.X; x++)
            {
                for (int y = 0; y < m_Size.Y; y++)
                {
                    totalEntropy += m_Tiles[x, y].GetEntropy();
                }
            }
            return totalEntropy;
        }

        public void Reveal()
        {
            for (int x = 0; x < m_Size.X; x++)
            {
                for (int y = 0; y < m_Size.Y; y++)
                {
                    m_GridMap.SetCellItem(new Vector3I(x, 0, y), m_Tiles[x, y].tileId);
                }
            }
        }
    }

    public class Tile
    {
        public int tileId; // index to all known tiles
        public bool collapsed;
        public List<int> options; // holds ids of all possible tiles this tile can become

        public int GetEntropy() { return collapsed ? 0 : options.Count; }
        public Tile(ref List<int> allOptions)
        {
            tileId = -1;
            collapsed = false;
            options = new List<int>(allOptions);
        }

        public Tile(int tileId)
        {
            this.tileId = tileId;
            this.collapsed = true;
            this.options = null;
        }
    }

    /* World Data */
    private Dictionary<Vector2I, Chunk> m_ChunkMap; // Chunks accessed by their coord
    private List<int> m_AllTileOptions; // All possible options a tile can become (excludes fake tile, obviously), could add more during runtime technically
    private List<Vector2I> m_ChunksToBeUpdated; // List of chunks that still need updating as they have not finished generating or were nuked
    private Tile m_FakeTile; // Acts as a tile that is always full of entropy

    /* Config */
    private Config m_Config; // Generator options
    private TilePrototype[] m_KnownTiles; // Tile prototype data

    /* External things */
    [Export] public string RulesetFilePath = "res://Rules/RulesDefault.json";

    /* Internal nonsense */
    private Node3D m_ChunkFolder; // Parent to chunk GridMap nodes
    private bool m_Finished; // Are we done generating?
    private Random m_Random;
    private List<Vector2I> m_Visited; // GlobalConstrain recursion check
    private List<Vector2I> m_RecentlyNuked; // GlobalUncollapse recursion check

    /* 3D render stuff */
    private List<Mesh> m_ModelMeshes; // Contains all tile meshes, technically not needed
    private MeshLibrary m_MeshLibrary; // Resource for all chunk gridmaps, we could add more during runtime technically

    /* Timing nonsense */
    Stopwatch collapseSw = new Stopwatch();
    Stopwatch constrainSw = new Stopwatch();
    Stopwatch cycleStopwatch = new Stopwatch();
    Stopwatch uncollapseSw = new Stopwatch();
    Stopwatch pickSw = new Stopwatch();
    long collapseTotalCalled = 0, collapseTotalTime = 0;
    long constrainTotalCalled = 0, constrainTotalTime = 0;
    long uncollapseTotalTime = 0, uncollapseTotalCalled = 0;
    long pickTotalTime = 0, pickTotalCalled = 0;
    private long cycleCount = 0, cycleTotalTime = 0;
    private double totalDelta = 0;

    /* Internal loading and setup */
    private bool LoadConfig()
    {
        if (!FileAccess.FileExists(RulesetFilePath))
        {
            GD.PushError("Config file not found!");
            return false;
        }

        /* Open & read config file */
        FileAccess configFile = FileAccess.Open(RulesetFilePath, FileAccess.ModeFlags.Read);
        Json json = new Json();
        var err = json.Parse(configFile.GetAsText());
        configFile.Close();
        if (err != Error.Ok)
        {
            GD.PushError("Failed to parse config!");
            return false;
        }
        using var config = json.Data.AsGodotDictionary();


        /* Load Config Data */
        m_Config = new Config();
        var meta = config["Meta"].AsGodotDictionary();
        m_Config.seed = meta["Seed"].AsInt32();
        m_Config.modelFolder = meta["ModelFolder"].AsString();
        if (m_Config.seed == 0)
        {
            Random r = new Random();
            m_Config.seed = r.Next();
        }
        m_Random = new Random(m_Config.seed);

        m_Config.meshSize = (float)meta["MeshSize"];

        m_Config.chunkSize = new Vector2I(meta["ChunkColumns"].AsInt32(), meta["ChunkRows"].AsInt32());
        m_Config.initChunkCountX = meta["ChunkCountX"].AsInt32();
        m_Config.initChunkCountY = meta["ChunkCountY"].AsInt32();


        /* Also create a fake tile */
        /* Load Tile Names & push into all options */
        Dictionary<string, int> tileNames = new Dictionary<string, int>();
        m_AllTileOptions = new List<int>();
        int tileId = 1;
        //m_AllTileOptions.Add(tileId++); // 0 is reserved for the fake tile
        foreach (var tileName in config["Tiles"].AsStringArray())
        {
            tileNames[tileName] = tileId;
            m_AllTileOptions.Add(tileId);
            tileId++;
        }


        /* Load Tile Data */
        List<TilePrototype> tilePrototypes = new List<TilePrototype>();
        TilePrototype fakeTilePrototype = new TilePrototype();
        fakeTilePrototype.name = "Void";
        fakeTilePrototype.weight = 0;
        fakeTilePrototype.northConnections = m_AllTileOptions.ToArray();
        fakeTilePrototype.eastConnections = m_AllTileOptions.ToArray();
        fakeTilePrototype.southConnections = m_AllTileOptions.ToArray();
        fakeTilePrototype.westConnections = m_AllTileOptions.ToArray();
        tilePrototypes.Insert(0, fakeTilePrototype);

        foreach (var tileName in config["Tiles"].AsStringArray())
        {
            using var tileData = config[tileName].AsGodotDictionary();
            TilePrototype tile = new TilePrototype();

            tile.name = tileName;
            tile.weight = (float)tileData["Weight"];
            tile.model = (string)tileData["Model"];

            List<int> connections = new List<int>();
            foreach (var conn in tileData["North"].AsStringArray())
            {
                connections.Add(tileNames[conn]);
            }
            tile.northConnections = connections.ToArray();

            connections = new List<int>();
            foreach (var conn in tileData["East"].AsStringArray())
            {
                connections.Add(tileNames[conn]);
            }
            tile.eastConnections = connections.ToArray();

            connections = new List<int>();
            foreach (var conn in tileData["South"].AsStringArray())
            {
                connections.Add(tileNames[conn]);
            }
            tile.southConnections = connections.ToArray();

            connections = new List<int>();
            foreach (var conn in tileData["West"].AsStringArray())
            {
                connections.Add(tileNames[conn]);
            }
            tile.westConnections = connections.ToArray();
            tileId++;
            tilePrototypes.Add(tile);
        }
        m_KnownTiles = tilePrototypes.ToArray();

        GD.Print("[Config]");
        GD.Print("Seed: ", m_Config.seed);

        return true;
    }

    private bool InitializeGraphics()
    {
        m_MeshLibrary = new MeshLibrary();
        for (int i = 0; i < m_KnownTiles.Length; i++)
        {
            if (i == TILE_VOID_ID) continue; // Skipping a known fake tile
            ref TilePrototype tile = ref m_KnownTiles[i];

            ArrayMesh mesh = (ArrayMesh)ResourceLoader.Load(m_Config.modelFolder + tile.model);

            m_MeshLibrary.CreateItem(i);
            m_MeshLibrary.SetItemMesh(i, mesh);
        }

        return true;
    }

    private void InitializeWorld()
    {
        /* A little organization wouldn't hurt */
        m_ChunkFolder = new Node3D();
        m_ChunkFolder.Name = "Chunks";
        AddChild(m_ChunkFolder);

        /* Prepare new chunks with data and init drawing stuff */
        m_ChunkMap = new Dictionary<Vector2I, Chunk>();
        m_ChunksToBeUpdated = new List<Vector2I>();
        for (int x = 0; x < m_Config.initChunkCountX; x++)
        {
            for (int y = 0; y < m_Config.initChunkCountY; y++)
            {
                Vector2I chunkCoord = new Vector2I(x, y);
                m_ChunkMap[chunkCoord] = new Chunk(chunkCoord, m_Config.chunkSize, ref m_AllTileOptions, ref m_MeshLibrary, m_Config.meshSize);
                m_ChunkFolder.AddChild(m_ChunkMap[chunkCoord].m_GridMap);
                m_ChunksToBeUpdated.Add(chunkCoord);
            }
        }

        /* Other stuff that need some help */
        m_Finished = false;
        m_FakeTile = new Tile(ref m_AllTileOptions);
        m_FakeTile.collapsed = true;
        m_FakeTile.tileId = TILE_VOID_ID;
    }

    /* External calls - may be used internally hehe */
    public void AddChunk(Vector2I chunkCoord)
    {
        // Add new chunk to be drawn and to the update queue
        m_ChunkMap[chunkCoord] = new Chunk(chunkCoord, m_Config.chunkSize, ref m_AllTileOptions, ref m_MeshLibrary, m_Config.meshSize);
        m_ChunkFolder.AddChild(m_ChunkMap[chunkCoord].m_GridMap);
        m_ChunksToBeUpdated.Add(chunkCoord);

        // Reduce chunks based on existing ones
        Vector2I northChunkCoord = chunkCoord + Vector2I.Up;
        if (m_ChunkMap.ContainsKey(northChunkCoord))
        {
            for (int x = 0; x < m_Config.chunkSize.X; x++)
            {
                m_Visited.Clear();
                Vector2I tilePos = new Vector2I(chunkCoord.X * m_Config.chunkSize.X + x, chunkCoord.Y * m_Config.chunkSize.Y);
                GlobalReduce(tilePos + Vector2I.Up);
            }
        }
        Vector2I eastChunkCoord = chunkCoord + Vector2I.Right;
        if (m_ChunkMap.ContainsKey(eastChunkCoord))
        {
            GD.Print("East");
            for (int y = 0; y < m_Config.chunkSize.Y; y++)
            {
                m_Visited.Clear();
                Vector2I tilePos = new Vector2I(chunkCoord.X * m_Config.chunkSize.X + m_Config.chunkSize.X - 1, chunkCoord.Y * m_Config.chunkSize.Y + y);
                GlobalReduce(tilePos + Vector2I.Right);
            }
        }
        Vector2I southChunkCoord = chunkCoord + Vector2I.Down;
        if (m_ChunkMap.ContainsKey(southChunkCoord))
        {
            for (int x = 0; x < m_Config.chunkSize.X; x++)
            {
                m_Visited.Clear();
                Vector2I tilePos = new Vector2I(chunkCoord.X * m_Config.chunkSize.X + x, chunkCoord.Y * m_Config.chunkSize.Y + m_Config.chunkSize.Y - 1);
                GlobalReduce(tilePos + Vector2I.Down);
            }
        }
        Vector2I westChunkCoord = chunkCoord + Vector2I.Left;
        if (m_ChunkMap.ContainsKey(westChunkCoord))
        {
            for (int y = 0; y < m_Config.chunkSize.Y; y++)
            {
                m_Visited.Clear();
                Vector2I tilePos = new Vector2I(chunkCoord.X * m_Config.chunkSize.X, chunkCoord.Y * m_Config.chunkSize.Y + y);
                GlobalReduce(tilePos);
            }
        }

        // Start the algorithm again
        m_Finished = false;
    }

    public void GlobalClearCell(Vector2I globalCellPos)
    {
        (Vector2I chunkCoord, Vector2I localPos) = GlobalToChunkCoord(globalCellPos);
        //GD.Print(globalCellPos);
        if (m_ChunkMap.ContainsKey(chunkCoord))
        {
            m_ChunkMap[chunkCoord].ClearGridCell(localPos, ref m_AllTileOptions);
            if (!m_ChunksToBeUpdated.Contains(chunkCoord))
            {
                m_ChunksToBeUpdated.Insert(0, chunkCoord);
            }
        }
    }

    public void GlobalSetGridCell(Vector2I globalCellPos, int tileId)
    {
        Vector2I chunkCoord = globalCellPos / m_Config.chunkSize;
        m_ChunkMap[chunkCoord].m_GridMap.SetCellItem(new Vector3I(globalCellPos.X % m_Config.chunkSize.X, 0, globalCellPos.Y % m_Config.chunkSize.Y), tileId);
    }

    //Returns (ChunkCoordinate, LocalTilePosition) calculated from globalCellPos
    public (Vector2I, Vector2I) GlobalToChunkCoord(Vector2I globalCellPos)
    {
        Vector2I chunkCoord = new Vector2I(
            Mathf.FloorToInt((float)globalCellPos.X / m_Config.chunkSize.X),
            Mathf.FloorToInt((float)globalCellPos.Y / m_Config.chunkSize.Y)
        );
        Vector2I localPos = globalCellPos - chunkCoord * m_Config.chunkSize;

        return (chunkCoord, localPos);
    }

    /* Internal Utils */
    private int SelectOption(ref List<int> options)
    {
        List<int> weightedList = new List<int>();
        foreach (var o in options)
        {
            for (int i = 0; i < m_KnownTiles[o].weight; i++)
            {
                weightedList.Add(o);
            }
        }

        return weightedList[m_Random.Next(weightedList.Count)];
    }

    private Vector2I LocalChunkCoordToGlobal(Vector2I localCoord, Vector2I chunkCoord)
    {
        return localCoord + chunkCoord * m_Config.chunkSize;
    }

    public ref Tile GetGlobalTile(Vector2I globalCellPos)
    {
        (Vector2I chunkCoord, Vector2I localCoord) = GlobalToChunkCoord(globalCellPos);
        if (!m_ChunkMap.ContainsKey(chunkCoord)) return ref m_FakeTile;
        return ref m_ChunkMap[chunkCoord].GetTile(localCoord);
    }

    List<int> GetGlobalTilePossibleOptions(Vector2I globalCellPos, Direction whichNeighbour)
    {
        List<int> tileOptions = new List<int>();
        ref Tile tile = ref GetGlobalTile(globalCellPos);

        Vector2I checkedTilePos = globalCellPos;
        Direction connectionSide = Direction.NONE;
        switch (whichNeighbour)
        {
            case Direction.NORTH: checkedTilePos += Vector2I.Up; connectionSide = Direction.SOUTH; break;
            case Direction.EAST: checkedTilePos += Vector2I.Right; connectionSide = Direction.WEST; break;
            case Direction.SOUTH: checkedTilePos += Vector2I.Down; connectionSide = Direction.NORTH; break;
            case Direction.WEST: checkedTilePos += Vector2I.Left; connectionSide = Direction.EAST; break;
        }

        ref Tile checkedTile = ref GetGlobalTile(checkedTilePos);
        int[] checkedTileOptionsSide = new int[0];

        if (checkedTile.collapsed)
        {
            // Get all prototype options
            switch (connectionSide)
            {
                case Direction.NORTH: checkedTileOptionsSide = m_KnownTiles[checkedTile.tileId].northConnections; break;
                case Direction.EAST: checkedTileOptionsSide = m_KnownTiles[checkedTile.tileId].eastConnections; break;
                case Direction.SOUTH: checkedTileOptionsSide = m_KnownTiles[checkedTile.tileId].southConnections; break;
                case Direction.WEST: checkedTileOptionsSide = m_KnownTiles[checkedTile.tileId].westConnections; break;
            }

            tileOptions = new List<int>(checkedTileOptionsSide);
        }
        else
        {
            // Get all possible options of every possible tile option
            foreach (int possibleTile in checkedTile.options)
            {
                switch (connectionSide)
                {
                    case Direction.NORTH: checkedTileOptionsSide = m_KnownTiles[possibleTile].northConnections; break;
                    case Direction.EAST: checkedTileOptionsSide = m_KnownTiles[possibleTile].eastConnections; break;
                    case Direction.SOUTH: checkedTileOptionsSide = m_KnownTiles[possibleTile].southConnections; break;
                    case Direction.WEST: checkedTileOptionsSide = m_KnownTiles[possibleTile].westConnections; break;
                }
                tileOptions = tileOptions.Union(checkedTileOptionsSide).ToList();
            }
        }

        return tileOptions;
    }

    /* WFC Core */

    // Collapses one tile from chunks that need updating
    private Vector2I Collapse()
    {
        collapseTotalCalled++;
        Vector2I selectedChunkCoord = m_ChunksToBeUpdated.First();
        //int totalEntropy = m_ChunkMap[selectedChunkCoord].GetTotalEntropy();
        //GD.Print("Chunk ", selectedChunkCoord, " has ", totalEntropy, " entropy");

        /* Selecting a random cell from among cells with least entropy */
        int selectedCellIndex = 0;
        pickSw.Start();
        List<Tuple<int, Vector2I>> selectedCellLocalToChunkList = m_ChunkMap[selectedChunkCoord].GetCollapseCell();
        //List<Vector2I> selectedList = new List<Vector2I>(selectedCellLocalToChunkList.Count);
        //selectedCellLocalToChunkList.ForEach(o => selectedList.Add(o.Item2));
        Vector2I selectedCellLocalToChunk = new Vector2I(-2, -2);
        if (selectedCellLocalToChunkList.Count > 0)
            selectedCellLocalToChunk = selectedCellLocalToChunkList[m_Random.Next(selectedCellLocalToChunkList.Count)].Item2;

        pickSw.Stop();
        long usPick = pickSw.ElapsedTicks / (Stopwatch.Frequency / (1000L * 1000L));
        pickTotalTime += usPick;
        pickTotalCalled++;
        pickSw.Reset();

        //GD.Print("Collapse: ", selectedChunkCoord, " : ", selectedCellLocalToChunk);

        // Check if we're done with chunk
        if (selectedCellLocalToChunk.X == -2 && selectedCellLocalToChunk.Y == -2)
        {
            OnFinishGenerationChunk(selectedChunkCoord);
            return new Vector2I(-2, -2); // Return value so Cycle knows to skip processing further
        }

        // Select new option for collapsed tile
        Vector2I selectedTileGlobalCoord = LocalChunkCoordToGlobal(selectedCellLocalToChunk, selectedChunkCoord);
        ref Tile selectedTile = ref GetGlobalTile(selectedTileGlobalCoord);
        int selectedTileEntropy = selectedTile.GetEntropy();

        // We have no options, try generating new stuff by uncollapsing the map
        if (selectedTileEntropy == 0)
        {
            m_RecentlyNuked.Clear();
            uncollapseSw.Start();
            GlobalUncollapseRecursive(selectedTileGlobalCoord, 0);

            /* This is probably wrong - TODO */
            foreach (var nukedTile in m_RecentlyNuked)
            {
                GD.Print(nukedTile);
                m_Visited.Clear();
                GlobalReduce(nukedTile);
            }
            //

            uncollapseSw.Stop();
            long usUncollapse = uncollapseSw.ElapsedTicks / (Stopwatch.Frequency / (1000L * 1000L));
            uncollapseSw.Reset();
            uncollapseTotalTime += usUncollapse;

            return new Vector2I(-1, -1); // Return value so Cycle knows to skip processing further
        }

        // We have options, collapse tile to one of the possible options
        int selectedOptionId = SelectOption(ref selectedTile.options);
        m_ChunkMap[selectedChunkCoord].SetGridCell(selectedCellLocalToChunk, selectedOptionId);
        return selectedTileGlobalCoord;
    }

    // Updates tiles options based on its neighbours
    public ReduceReturn GlobalReduce(Vector2I globalCellPos)
    {
        (Vector2I thisChunkCoord, Vector2I thisLocalCoord) = GlobalToChunkCoord(globalCellPos);
        //GD.Print(globalCellPos, " => ", thisChunkCoord, " : ", thisLocalCoord);

        ref Tile tile = ref GetGlobalTile(globalCellPos);
        if (tile.collapsed || tile.tileId == TILE_VOID_ID) { return ReduceReturn.NO_CHANGE; } // This tile does not need reducing

        List<int> newOptions = new List<int>(m_AllTileOptions);
        List<int> directionOptions = new List<int>();

        // Get connections from our north neighbour
        directionOptions = GetGlobalTilePossibleOptions(globalCellPos, Direction.NORTH);
        newOptions = newOptions.Intersect(directionOptions).ToList();

        // Get connections from our east neighbour
        directionOptions = GetGlobalTilePossibleOptions(globalCellPos, Direction.EAST);
        newOptions = newOptions.Intersect(directionOptions).ToList();

        // Get connections from our south neighbour
        directionOptions = GetGlobalTilePossibleOptions(globalCellPos, Direction.SOUTH);
        newOptions = newOptions.Intersect(directionOptions).ToList();

        // Get connections from our west neighbour
        directionOptions = GetGlobalTilePossibleOptions(globalCellPos, Direction.WEST);
        newOptions = newOptions.Intersect(directionOptions).ToList();

        // We have no options, everything is going to hell
        if (newOptions.Count == 0)
        {
            tile.options = newOptions;
            return ReduceReturn.NO_OPTIONS;
        }

        // Do options need an udpate?
        if (tile.options.Count != newOptions.Count)
        {
            tile.options = newOptions;
            return ReduceReturn.CHANGE;
        }

        // No update to options
        return ReduceReturn.NO_CHANGE;
    }

    // Recursive GlobalReduce to propagate throughout the grid
    public void GlobalConstrain(Vector2I globalPos, Direction fromWhere, int currentDepth)
    {
        constrainTotalCalled++;
        if (currentDepth >= CONSTRAIN_MAX_DEPTH) { return; }
        if (m_Visited.Count >= CONSTRAIN_MAX_DEPTH * CONSTRAIN_MAX_DEPTH)
        {
            GD.Print("Constrain recursion! Started with: ", m_Visited.First(), " Count: ", m_Visited.Count, " Current: ", globalPos);
        }

        if (m_Visited.Contains(globalPos)) { return; } // Avoid infinite cycling, cleared before calling constrain
        m_Visited.Add(globalPos);

        (Vector2I thisChunkCoord, Vector2I thisLocalCoord) = GlobalToChunkCoord(globalPos);

        // Are we updating the void? Possibly not needed
        if (!m_ChunkMap.ContainsKey(thisChunkCoord) || GetGlobalTile(globalPos).tileId == TILE_VOID_ID) return;

        (Vector2I northChunkCoord, Vector2I northLocalCoord) = GlobalToChunkCoord(globalPos + Vector2I.Up);
        (Vector2I eastChunkCoord, Vector2I eastLocalCoord) = GlobalToChunkCoord(globalPos + Vector2I.Right);
        (Vector2I southChunkCoord, Vector2I southLocalCoord) = GlobalToChunkCoord(globalPos + Vector2I.Down);
        (Vector2I westChunkCoord, Vector2I westLocalCoord) = GlobalToChunkCoord(globalPos + Vector2I.Left);

        // Does such a chunk exist?
        if (fromWhere != Direction.NORTH && m_ChunkMap.ContainsKey(northChunkCoord))
        {
            // There is a tile above us, be it this chunk or another one we don't care
            Vector2I northGlobalPos = globalPos + Vector2I.Up;
            ReduceReturn ret = GlobalReduce(northGlobalPos);
            if (ret == ReduceReturn.CHANGE)
            {
                // If tile has been reduced we carry on constraining further, make sure not to get stuck in a recursive loop
                // We're checking against to the north, hence we're coming from south
                GlobalConstrain(northGlobalPos, Direction.SOUTH, currentDepth + 1);
            }
        }

        if (fromWhere != Direction.EAST && m_ChunkMap.ContainsKey(eastChunkCoord))
        {
            Vector2I eastGlobalPos = globalPos + Vector2I.Right;
            ReduceReturn ret = GlobalReduce(eastGlobalPos);
            if (ret == ReduceReturn.CHANGE)
            {
                GlobalConstrain(eastGlobalPos, Direction.WEST, currentDepth + 1);
            }
        }

        if (fromWhere != Direction.SOUTH && m_ChunkMap.ContainsKey(southChunkCoord))
        {
            Vector2I southGlobalPos = globalPos + Vector2I.Down;
            ReduceReturn ret = GlobalReduce(southGlobalPos);
            if (ret == ReduceReturn.CHANGE)
            {
                GlobalConstrain(southGlobalPos, Direction.NORTH, currentDepth + 1);
            }
        }

        if (fromWhere != Direction.WEST && m_ChunkMap.ContainsKey(westChunkCoord))
        {
            Vector2I westGlobalPos = globalPos + Vector2I.Left;
            ReduceReturn ret = GlobalReduce(westGlobalPos);
            if (ret == ReduceReturn.CHANGE)
            {
                GlobalConstrain(westGlobalPos, Direction.EAST, currentDepth + 1);
            }
        }

        // Special case for uncollapse, TODO: this shouldn't be here!
        if (fromWhere == Direction.NONE)
        {
            GlobalReduce(globalPos);
        }
    }

    // Clears tiles in and around specified position
    public void GlobalUncollapseRecursive(Vector2I globalCellPos, int depth)
    {
        uncollapseTotalCalled++;
        ref Tile tile = ref GetGlobalTile(globalCellPos);
        if (tile.tileId == 0 || depth >= UNCOLLAPSE_MAX_DEPTH) return; // Skip unnecessary headaches
        if (m_RecentlyNuked.Contains(globalCellPos)) return;
        m_RecentlyNuked.Add(globalCellPos);

        GlobalClearCell(globalCellPos);
        GlobalUncollapseRecursive(globalCellPos + Vector2I.Up, depth + 1);
        GlobalUncollapseRecursive(globalCellPos + Vector2I.Right, depth + 1);
        GlobalUncollapseRecursive(globalCellPos + Vector2I.Down, depth + 1);
        GlobalUncollapseRecursive(globalCellPos + Vector2I.Left, depth + 1);
    }

    // Runs an update cycle for WaveFunctionCollapse
    public void Cycle()
    {
        if (m_Finished) return;

        cycleCount++;
        if (m_ChunksToBeUpdated.Count == 0) { OnFinishGeneration(); return; } // No more chunks need updating

        collapseSw.Start();
        Vector2I collapsedCell = Collapse();
        collapseSw.Stop();
        long usC = collapseSw.ElapsedTicks / (Stopwatch.Frequency / (1000L * 1000L));
        collapseSw.Reset();
        collapseTotalTime += usC;

        if (collapsedCell.X == -1 && collapsedCell.Y == -2) { return; } // Skip processing further as dictated by Collapse

        m_Visited.Clear();
        constrainSw.Start();
        GlobalConstrain(collapsedCell, Direction.NONE, 0);
        constrainSw.Stop();
        long usConstrain = constrainSw.ElapsedTicks / (Stopwatch.Frequency / (1000L * 1000L));
        constrainSw.Reset();
        constrainTotalTime += usConstrain;
    }

    /* "Events" */
    private void OnFinishGeneration()
    {
        m_Finished = true;
        GD.Print("[Metrics]");
        GD.Print("Total time taken: ", totalDelta, " s");
        GD.Print("Cycle: {Count: ", cycleCount, ", Average time: ", cycleTotalTime / (double)cycleCount, "us }");
        GD.Print("Collapse: {Count: ", collapseTotalCalled, ", Average time: ", collapseTotalTime / (double)collapseTotalCalled, "us }");
        GD.Print("> Entropy selection: {Count: ", pickTotalCalled, ", Average time: ", pickTotalTime / (double)pickTotalCalled, "us }");
        GD.Print("> Constrain: {Count: ", constrainTotalCalled, ", Average time: ", constrainTotalTime / (double)constrainTotalCalled, "us }");
        GD.Print("Uncollapse: {Count: ", uncollapseTotalCalled, ", Average time: ", uncollapseTotalTime / (double)uncollapseTotalCalled, "us }");
    }

    private void OnFinishGenerationChunk(Vector2I chunkCoord)
    {
        m_ChunksToBeUpdated.Remove(chunkCoord);
    }

    /* Godot Overrides */
    public override void _Ready()
    {
        if (!LoadConfig()) { GD.PrintErr("Failed to load config."); return; }
        if (!InitializeGraphics()) { GD.PrintErr("Failed to build tiles."); return; }
        InitializeWorld();

        m_Visited = new List<Vector2I>();
        m_RecentlyNuked = new List<Vector2I>();
    }

    public override void _Process(double delta)
    {
        if (m_Finished) return;
        totalDelta += delta;

        for (int i = 0; i < CYCLES_PER_PROCESS; i++)
        {
            if (m_Finished) break;

            cycleStopwatch.Start();
            Cycle();
            cycleStopwatch.Stop();

            long usCycle = cycleStopwatch.ElapsedTicks / (Stopwatch.Frequency / (1000L * 1000L));
            cycleTotalTime += usCycle;
            cycleStopwatch.Reset();
        }

    }
}